PHARMASTOCK - ASP.NET Core 8 + Entity Framework Core + SQL Server

REQUISITOS: Visual Studio 2022 (carga de trabajo ASP.NET) o .NET 8 SDK, y SQL Server (Express/LocalDB sirve).

PASOS
1. Extraer el ZIP completo (clic derecho > Extraer todo) y abrir PharmaStock.sln con doble clic (o Visual Studio > Abrir un proyecto o solucion > PharmaStock.sln).
2. Editar appsettings.json -> ConnectionStrings:PharmaStockConn con SU servidor/base de datos.
   Ej. SQL Express: Server=.\SQLEXPRESS;Database=PharmaStockDB;Trusted_Connection=True;TrustServerCertificate=True;
3. Presionar F5. Al iniciar crea la BD con las 10 tablas y carga datos de ejemplo.
4. Usuarios: admin / 12345 (Administrador) y cajero / 12345 (Cajero). Clave guardada con SHA256.

Si ya creo la BD con su script SQL del documento, deje la linea EnsureCreated() o comentela; el Seed solo corre si la tabla Roles esta vacia.
El PDF de comprobantes y reportes usa Imprimir > Guardar como PDF del navegador.
