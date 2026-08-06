using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json.Serialization;
using MercadoPago.Config;
using miastral_api.Data;
using miastral_api.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Base de datos MySQL ──────────────────────────────────────
builder.Services.AddDbContext<MiastralContext>(options =>
    options.UseMySql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        new MySqlServerVersion(new Version(8, 0, 0))
    )
);

// ── JWT ──────────────────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"]!;
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Sin esto, las versiones nuevas del validador de JWT no traducen el
        // claim corto "role" al tipo completo que usa [Authorize(Roles=...)]
        // para comparar. Resultado: el token es válido, pero el chequeo de rol
        // "admin" siempre falla con 403, aunque el usuario sí sea admin.
        options.MapInboundClaims = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

// ── Subida de imágenes a Ferozo/DonWeb por FTP ──────────────────
builder.Services.AddScoped<FerozoUploadService>();

// ── MercadoPago ──────────────────────────────────────────────
// El AccessToken es global/estático en el SDK, alcanza con setearlo una vez acá.
// Sale vacío en appsettings.json (el archivo público) — la clave real viene de
// appsettings.Development.json (gitignored) en local, o de la env var
// MercadoPago__AccessToken en Render en producción, igual que la clave JWT.
MercadoPagoConfig.AccessToken = builder.Configuration["MercadoPago:AccessToken"];

// ── CORS ─────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("MiastralPolicy", policy =>
    {
        policy.WithOrigins(
            "https://miastral.vercel.app",
            "https://byvalentinam.com",
            "https://www.byvalentinam.com",
            "http://localhost:5173"
        )
        .AllowAnyHeader()
        .AllowAnyMethod();
    });
});

// ReferenceHandler.IgnoreCycles: Orden.Items <-> OrdenItem.Orden es una relación
// bidireccional — EF hace "fixup" automático de la navegación inversa al cargar
// o agregar entidades relacionadas, y sin esto System.Text.Json revienta con
// "A possible object cycle was detected" al serializar cualquier Orden con Items.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    });

// ── Swagger (documentación interactiva, solo se activa en Development) ──
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "miastral API", Version = "v1" });

    // Permite probar endpoints con [Authorize] pegando el JWT en el botón "Authorize"
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Description = "Pegá acá el token que te devuelve /api/auth/login o /api/auth/admin/login (sin la palabra 'Bearer')"
    });
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Fallback manual de CORS — va primero, antes que cualquier otra cosa, para
// que el header se agregue siempre pase lo que pase más adelante en el
// pipeline (incluso si el exception handler intercepta algo). Lo agregamos
// porque en producción (Render, detrás de su proxy Cloudflare) se vieron
// respuestas sin el header Access-Control-Allow-Origin pese a que el origen
// estaba en la whitelist y app.UseCors() de por sí debería haberlo agregado.
// Esto fuerza el header a mano y contesta el preflight OPTIONS directo, sin
// depender de que UseCors lo resuelva correctamente en ese entorno.
var origenesPermitidos = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "https://miastral.vercel.app",
    "https://byvalentinam.com",
    "https://www.byvalentinam.com",
    "http://localhost:5173",
};

app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    var permitido = !string.IsNullOrEmpty(origin) && origenesPermitidos.Contains(origin);

    // OnStarting corre justo antes de que Kestrel mande los headers — es el
    // último punto posible para tocarlos, así nadie (ni un proxy intermedio
    // reusando una respuesta cacheada) los pisa después. También forzamos
    // no-store: sospechamos que el proxy de Cloudflare delante de Render
    // estaba sirviendo una respuesta cacheada de OTRO origen (ej: Vercel)
    // a los pedidos de byvalentinam.com, porque nuestra respuesta no traía
    // ningún header que le dijera "esto varía según el origen, no cachear".
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        context.Response.Headers["Pragma"] = "no-cache";
        if (permitido)
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Access-Control-Allow-Headers"] = "*";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS";
            context.Response.Headers["Vary"] = "Origin";
        }
        return Task.CompletedTask;
    });

    if (HttpMethods.IsOptions(context.Request.Method))
    {
        if (permitido)
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Access-Control-Allow-Headers"] = "*";
            context.Response.Headers["Access-Control-Allow-Methods"] = "GET,POST,PUT,DELETE,OPTIONS";
            context.Response.Headers["Vary"] = "Origin";
        }
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        await context.Response.CompleteAsync();
        return;
    }

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(); // disponible en /swagger
}
else
{
    // Sin esto, cualquier excepción no manejada en producción devuelve una
    // respuesta vacía (sin body) y sin mensaje — así fue muy difícil detectar
    // el bug de la columna alto_cm faltante. Con este handler, cualquier error
    // futuro va a devolver un JSON con el mensaje real, visible en el panel.
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
            var mensaje = feature?.Error?.Message ?? "Error interno del servidor";
            await context.Response.WriteAsJsonAsync(new { message = $"Error interno: {mensaje}" });
        });
    });
}

app.UseCors("MiastralPolicy");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();