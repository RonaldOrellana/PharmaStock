using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

var b = WebApplication.CreateBuilder(args);

// Configuración del DbContext con SQL Server
b.Services.AddDbContext<Db>(o => o.UseSqlServer(b.Configuration.GetConnectionString("PharmaStockConn")));

var app = b.Build();

using (var s = app.Services.CreateScope())
{
    var d = s.ServiceProvider.GetRequiredService<Db>();
    d.Database.EnsureCreated();   // Crea PharmaStockDB y las tablas si no existen
    Seed(d);                      // Carga datos iniciales solo si la BD está vacía
}

app.UseDefaultFiles();
app.UseStaticFiles();

IResult Err(string m) => Results.BadRequest(new { error = m });
IResult Forbidden(string m = "Acceso denegado. Función reservada para el Administrador.") =>
    Results.Json(new { error = m }, statusCode: 403);

// ---------- LOGIN FLEXIBLE COMPATIBLE CON SQL SERVER (RF-01, RNF-01) ----------
app.MapPost("/api/login", async (LoginReq r, Db d) =>
{
    if (string.IsNullOrWhiteSpace(r.u) || string.IsNullOrWhiteSpace(r.p))
        return Results.Json(new { error = "Ingresa usuario y contraseña." }, statusCode: 400);

    var hashHex = Hash(r.p);

    // Busca coincidencia ignore-case en el usuario
    var u = await d.Usuarios.Include(x => x.Rol)
        .FirstOrDefaultAsync(x => x.Usuario.ToLower() == r.u.ToLower() && x.Estado);

    // Compara el hash sin importar mayúsculas o minúsculas
    if (u != null && string.Equals(u.ClaveHash, hashHex, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Ok(new { id = u.IdUsuario, n = u.Nombre, r = u.Rol.NombreRol });
    }

    return Results.Json(new { error = "Credenciales incorrectas. Revisa usuario y contraseña." }, statusCode: 401);
});

// ---------- ESTADO COMPLETO PARA LA INTERFAZ ----------
app.MapGet("/api/state", async (Db d) =>
{
    var meds = await d.Medicamentos.Include(m => m.Categoria).ToListAsync();
    var lots = await d.Lotes.ToListAsync();
    var sales = await d.Ventas.Include(v => v.Usuario).Include(v => v.Cliente)
        .Include(v => v.Detalles).ThenInclude(x => x.Medicamento)
        .Include(v => v.Detalles).ThenInclude(x => x.Lote)
        .OrderByDescending(v => v.IdVenta).Take(200).ToListAsync();
    var movs = await d.MovimientosInventario.Include(m => m.Usuario)
        .OrderByDescending(m => m.IdMovimiento).Take(300).ToListAsync();

    return new
    {
        cats = await d.Categorias.Select(c => c.NombreCategoria).ToListAsync(),
        provs = await d.Proveedores.Select(p => new { id = p.IdProveedor, n = p.NombreEmpresa, t = p.Telefono }).ToListAsync(),
        meds = meds.Select(m => new { id = m.IdMedicamento, code = m.CodigoBarras, n = m.NombreComercial, cat = m.Categoria.NombreCategoria, price = m.PrecioVenta, min = m.StockMinimo, on = m.Estado ? 1 : 0 }),
        lots = lots.Select(l => new { id = l.IdLote, med = l.IdMedicamento, prov = l.IdProveedor, num = l.NumeroLote, exp = l.FechaVencimiento.ToString("yyyy-MM-dd"), ini = l.CantidadInicial, cur = l.CantidadActual }),
        sales = sales.Select(v => new
        {
            num = v.NumeroFactura,
            fecha = v.FechaVenta,
            user = v.Usuario != null ? v.Usuario.Nombre : "Sistema",
            cli = v.Cliente != null ? v.Cliente.NombreCompleto : "Consumidor final",
            items = v.Detalles.Select(x => new { n = x.Medicamento.NombreComercial, lote = x.Lote.NumeroLote, q = x.Cantidad, p = x.PrecioUnitario }),
            sub = v.Subtotal,
            iva = v.IVA,
            total = v.Total
        }),
        movs = movs.Select(m => new { id = m.IdMovimiento, med = m.IdMedicamento, lot = m.IdLote, t = m.TipoMovimiento, q = m.Cantidad, why = m.Motivo, u = m.Usuario != null ? m.Usuario.Nombre : "Sistema", d = m.FechaMovimiento.ToString("dd/MM/yyyy HH:mm") })
    };
});

// ---------- MEDICAMENTOS (RF-02, RN-04: SOLO ADMINISTRADOR) ----------
app.MapPost("/api/meds", async (MedReq r, Db d) =>
{
    var user = await d.Usuarios.Include(u => u.Rol).FirstOrDefaultAsync(u => u.IdUsuario == r.uid);
    if (user == null || !IsAdmin(user.Rol.NombreRol)) return Forbidden();

    if (r.price <= 0) return Err("El precio debe ser mayor que cero (RN-04).");
    if (await d.Medicamentos.AnyAsync(m => m.CodigoBarras == r.code)) return Err("Error: el código de barras ya existe.");
    var c = await d.Categorias.FirstOrDefaultAsync(x => x.NombreCategoria == r.cat);
    if (c == null) return Err("Categoría inválida.");

    d.Medicamentos.Add(new Medicamento { CodigoBarras = r.code, NombreComercial = r.n, IdCategoria = c.IdCategoria, PrecioVenta = r.price, StockMinimo = r.min });
    await d.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/meds/{id}/toggle", async (int id, int uid, Db d) =>
{
    var user = await d.Usuarios.Include(u => u.Rol).FirstOrDefaultAsync(u => u.IdUsuario == uid);
    if (user == null || !IsAdmin(user.Rol.NombreRol)) return Forbidden();

    var m = await d.Medicamentos.FindAsync(id);
    if (m == null) return Err("No existe el medicamento.");
    m.Estado = !m.Estado;
    await d.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

// ---------- LOTES (RF-03: PERMITIDO PARA CAJEROS Y ADMIN - CON CONTROL DE ERRORES SQL) ----------
app.MapPost("/api/lots", async (LotReq r, Db d) =>
{
    try
    {
        if (r.med <= 0)
            return Results.Json(new { error = "Selecciona un medicamento válido." }, statusCode: 400);

        if (r.prov <= 0)
            return Results.Json(new { error = "Selecciona un proveedor válido." }, statusCode: 400);

        if (string.IsNullOrWhiteSpace(r.num))
            return Results.Json(new { error = "El número de lote es obligatorio." }, statusCode: 400);

        if (r.q <= 0)
            return Results.Json(new { error = "La cantidad debe ser mayor a 0." }, statusCode: 400);

        if (!DateTime.TryParse(r.exp, out var expDate))
            return Results.Json(new { error = "Fecha de vencimiento inválida." }, statusCode: 400);

        var numLoteLimpio = r.num.Trim();

        // 1. Validar si ya existe el lote para ese mismo medicamento
        var existeLote = await d.Lotes.AnyAsync(l => l.IdMedicamento == r.med && l.NumeroLote == numLoteLimpio);
        if (existeLote)
        {
            return Results.Json(new { error = $"El número de lote '{numLoteLimpio}' ya existe para este medicamento." }, statusCode: 400);
        }

        var m = await d.Medicamentos.FindAsync(r.med);
        if (m == null)
            return Results.Json(new { error = "El medicamento seleccionado no existe." }, statusCode: 400);

        var p = await d.Proveedores.FindAsync(r.prov);
        if (p == null)
            return Results.Json(new { error = "El proveedor seleccionado no existe." }, statusCode: 400);

        // 2. Crear objeto Lote
        var l = new Lote
        {
            IdMedicamento = r.med,
            IdProveedor = r.prov,
            NumeroLote = numLoteLimpio,
            FechaFabricacion = DateTime.Today,
            FechaVencimiento = expDate.Date,
            CantidadInicial = r.q,
            CantidadActual = r.q
        };

        d.Lotes.Add(l);
        m.StockActual += r.q;

        int idUsuarioKardex = r.uid > 0 ? r.uid : 1;

        d.MovimientosInventario.Add(new Movimiento
        {
            IdMedicamento = m.IdMedicamento,
            Lote = l,
            TipoMovimiento = "ENTRADA",
            Cantidad = r.q,
            Motivo = "Ingreso de lote " + numLoteLimpio,
            IdUsuario = idUsuarioKardex
        });

        await d.SaveChangesAsync();
        return Results.Ok(new { ok = true, id = l.IdLote });
    }
    catch (DbUpdateException ex)
    {
        var innerMsg = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
        return Results.Json(new { error = "Error al guardar en la base de datos: " + innerMsg }, statusCode: 400);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "Error interno: " + ex.Message }, statusCode: 500);
    }
});

// ---------- VENTAS (RF-05, RF-06: PERMITIDO PARA CAJEROS Y ADMIN) ----------
app.MapPost("/api/sales", async (SaleReq r, Db d) =>
{
    var user = await d.Usuarios.FirstOrDefaultAsync(u => u.IdUsuario == r.uid);
    if (user == null) return Err("Usuario no identificado.");

    if (r.items == null || r.items.Count == 0) return Err("Agrega al menos un producto.");

    await using var tx = await d.Database.BeginTransactionAsync();
    var num = "FAC-" + ((await d.Ventas.CountAsync()) + 1).ToString("D6");
    var v = new Venta { NumeroFactura = num, IdUsuario = r.uid, FechaVenta = DateTime.Now };

    foreach (var i in r.items)
    {
        var l = await d.Lotes.Include(x => x.Medicamento).FirstOrDefaultAsync(x => x.IdLote == i.lot);
        if (l == null || i.q <= 0) return Err("Lote o cantidad inválidos.");
        if (l.FechaVencimiento.Date < DateTime.Today) return Err("Bloqueado: el lote " + l.NumeroLote + " está vencido (RN-01).");
        if (l.CantidadActual < i.q) return Err("Stock insuficiente en el lote " + l.NumeroLote + " (RN-02).");

        l.CantidadActual -= i.q;
        l.Medicamento.StockActual -= i.q;
        var sub = l.Medicamento.PrecioVenta * i.q;

        v.Detalles.Add(new DetalleVenta { IdMedicamento = l.IdMedicamento, IdLote = l.IdLote, Cantidad = i.q, PrecioUnitario = l.Medicamento.PrecioVenta, Subtotal = sub });
        d.MovimientosInventario.Add(new Movimiento { IdMedicamento = l.IdMedicamento, IdLote = l.IdLote, TipoMovimiento = "SALIDA", Cantidad = -i.q, Motivo = "Venta " + num, IdUsuario = r.uid });
        v.Subtotal += sub;
    }

    v.IVA = Math.Round(v.Subtotal * 0.13m, 2);
    v.Total = v.Subtotal + v.IVA;

    if (!string.IsNullOrWhiteSpace(r.cli))
        v.Cliente = await d.Clientes.FirstOrDefaultAsync(c => c.NombreCompleto == r.cli) ?? new Cliente { NombreCompleto = r.cli };
    else
        v.IdCliente = 1; // Consumidor Final

    d.Ventas.Add(v);
    await d.SaveChangesAsync();
    await tx.CommitAsync();
    return Results.Ok(new { ok = true });
});

// ---------- AJUSTES DE INVENTARIO (RF-08: SOLO ADMINISTRADOR) ----------
app.MapPost("/api/adjust", async (AdjReq r, Db d) =>
{
    var user = await d.Usuarios.Include(u => u.Rol).FirstOrDefaultAsync(u => u.IdUsuario == r.uid);
    if (user == null || !IsAdmin(user.Rol.NombreRol)) return Forbidden();

    var l = await d.Lotes.Include(x => x.Medicamento).FirstOrDefaultAsync(x => x.IdLote == r.lot);
    if (l == null || r.q == 0 || string.IsNullOrWhiteSpace(r.why)) return Err("Indica lote, cantidad y motivo.");
    if (l.CantidadActual + r.q < 0) return Err("El ajuste dejaría el lote en negativo.");

    l.CantidadActual += r.q;
    l.Medicamento.StockActual += r.q;
    d.MovimientosInventario.Add(new Movimiento { IdMedicamento = l.IdMedicamento, IdLote = l.IdLote, TipoMovimiento = "AJUSTE", Cantidad = r.q, Motivo = r.why, IdUsuario = r.uid });
    await d.SaveChangesAsync();
    return Results.Ok(new { ok = true });
});

app.Run();

static bool IsAdmin(string roleName) =>
    string.Equals(roleName, "Administrador", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(roleName, "Admin", StringComparison.OrdinalIgnoreCase);

static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

static void Seed(Db d)
{
    if (d.Usuarios.Any()) return;

    var adminRole = new Rol { NombreRol = "Administrador", Descripcion = "Acceso total al sistema" };
    var cajeroRole = new Rol { NombreRol = "Cajero", Descripcion = "Acceso a ventas, consultas y recepción de lotes" };

    d.Roles.AddRange(adminRole, cajeroRole);

    d.Usuarios.AddRange(
        new Usr { Nombre = "Administrador del Sistema", Usuario = "admin", ClaveHash = Hash("Admin123*"), Rol = adminRole, Estado = true },
        new Usr { Nombre = "Laura Fuentes", Usuario = "cajero1", ClaveHash = Hash("Cajero123*"), Rol = cajeroRole, Estado = true },
        new Usr { Nombre = "Miguel Rivas", Usuario = "cajero2", ClaveHash = Hash("Cajero123*"), Rol = cajeroRole, Estado = true }
    );

    d.SaveChanges();
}

// ---------- DTOs ----------
record LoginReq(string u, string p);
record MedReq(int uid, string code, string n, string cat, decimal price, int min);
record LotReq(int med, int prov, string num, string exp, int q, int uid);
record Item(int lot, int q);
record SaleReq(int uid, string cli, List<Item> items);
record AdjReq(int lot, int q, string why, int uid);

// ---------- ENTIDADES ----------
[Table("Roles")]
class Rol
{
    [Key] public int IdRol { get; set; }
    [MaxLength(50)] public string NombreRol { get; set; }
    [MaxLength(250)] public string Descripcion { get; set; }
}

[Table("Usuarios")]
class Usr
{
    [Key] public int IdUsuario { get; set; }
    [MaxLength(150)] public string Nombre { get; set; }
    [MaxLength(50)] public string Usuario { get; set; }
    [MaxLength(255)] public string ClaveHash { get; set; }
    public int IdRol { get; set; }
    [ForeignKey("IdRol")] public Rol Rol { get; set; }
    public bool Estado { get; set; } = true;
}

[Table("Categorias")]
class Categoria
{
    [Key] public int IdCategoria { get; set; }
    [MaxLength(100)] public string NombreCategoria { get; set; }
    [MaxLength(250)] public string Descripcion { get; set; }
}

[Table("Proveedores")]
class Proveedor
{
    [Key] public int IdProveedor { get; set; }
    [MaxLength(150)] public string NombreEmpresa { get; set; }
    [MaxLength(20)] public string Telefono { get; set; }
    [MaxLength(120)] public string Email { get; set; }
    [MaxLength(250)] public string Direccion { get; set; }
}

[Table("Medicamentos")]
class Medicamento
{
    [Key] public int IdMedicamento { get; set; }
    [MaxLength(30)] public string CodigoBarras { get; set; }
    [MaxLength(150)] public string NombreComercial { get; set; }
    public int IdCategoria { get; set; }
    [ForeignKey("IdCategoria")] public Categoria Categoria { get; set; }
    [Precision(10, 2)] public decimal PrecioVenta { get; set; }
    public int StockActual { get; set; }
    public int StockMinimo { get; set; } = 10;
    public bool Estado { get; set; } = true;
}

[Table("Lotes")]
class Lote
{
    [Key] public int IdLote { get; set; }
    public int IdMedicamento { get; set; }
    [ForeignKey("IdMedicamento")] public Medicamento Medicamento { get; set; }
    public int IdProveedor { get; set; }
    [ForeignKey("IdProveedor")] public Proveedor Proveedor { get; set; }
    [MaxLength(50)] public string NumeroLote { get; set; }
    [Column(TypeName = "date")] public DateTime? FechaFabricacion { get; set; }
    [Column(TypeName = "date")] public DateTime FechaVencimiento { get; set; }
    public int CantidadInicial { get; set; }
    public int CantidadActual { get; set; }
}

[Table("Clientes")]
class Cliente
{
    [Key] public int IdCliente { get; set; }
    [MaxLength(150)] public string NombreCompleto { get; set; }
    [MaxLength(20)] public string NIT_DUI { get; set; }
    [MaxLength(20)] public string Telefono { get; set; }
    [MaxLength(250)] public string Direccion { get; set; }
}

[Table("Ventas")]
class Venta
{
    [Key] public int IdVenta { get; set; }
    [MaxLength(20)] public string NumeroFactura { get; set; }
    public int IdUsuario { get; set; }
    [ForeignKey("IdUsuario")] public Usr Usuario { get; set; }
    public int IdCliente { get; set; }
    [ForeignKey("IdCliente")] public Cliente Cliente { get; set; }
    public DateTime FechaVenta { get; set; } = DateTime.Now;
    [Precision(12, 2)] public decimal Subtotal { get; set; }
    [Precision(12, 2)] public decimal IVA { get; set; }
    [Precision(12, 2)] public decimal Total { get; set; }
    [MaxLength(15)] public string Estado { get; set; } = "EMITIDA";
    public List<DetalleVenta> Detalles { get; set; } = new();
}

[Table("DetalleVentas")]
class DetalleVenta
{
    [Key] public int IdDetalle { get; set; }
    public int IdVenta { get; set; }
    [ForeignKey("IdVenta")] public Venta Venta { get; set; }
    public int IdMedicamento { get; set; }
    [ForeignKey("IdMedicamento")] public Medicamento Medicamento { get; set; }
    public int IdLote { get; set; }
    [ForeignKey("IdLote")] public Lote Lote { get; set; }
    public int Cantidad { get; set; }
    [Precision(10, 2)] public decimal PrecioUnitario { get; set; }
    [Precision(12, 2)] public decimal Subtotal { get; set; }
}

[Table("MovimientosInventario")]
class Movimiento
{
    [Key] public int IdMovimiento { get; set; }
    public int IdMedicamento { get; set; }
    [ForeignKey("IdMedicamento")] public Medicamento Medicamento { get; set; }
    public int? IdLote { get; set; }
    [ForeignKey("IdLote")] public Lote Lote { get; set; }
    [MaxLength(10)] public string TipoMovimiento { get; set; }
    public int Cantidad { get; set; }
    public DateTime FechaMovimiento { get; set; } = DateTime.Now;
    [MaxLength(250)] public string Motivo { get; set; }
    public int IdUsuario { get; set; }
    [ForeignKey("IdUsuario")] public Usr Usuario { get; set; }
}

// ---------- DB CONTEXT ----------
class Db : DbContext
{
    public Db(DbContextOptions<Db> o) : base(o) { }

    public DbSet<Rol> Roles { get; set; }
    public DbSet<Usr> Usuarios { get; set; }
    public DbSet<Categoria> Categorias { get; set; }
    public DbSet<Proveedor> Proveedores { get; set; }
    public DbSet<Medicamento> Medicamentos { get; set; }
    public DbSet<Lote> Lotes { get; set; }
    public DbSet<Cliente> Clientes { get; set; }
    public DbSet<Venta> Ventas { get; set; }
    public DbSet<DetalleVenta> DetalleVentas { get; set; }
    public DbSet<Movimiento> MovimientosInventario { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        foreach (var fk in mb.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;

        mb.Entity<Usr>().HasIndex(u => u.Usuario).IsUnique();
        mb.Entity<Medicamento>().HasIndex(m => m.CodigoBarras).IsUnique();
        mb.Entity<Venta>().HasIndex(v => v.NumeroFactura).IsUnique();
    }
}