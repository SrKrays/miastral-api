using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using miastral_api.Data;

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

// ── CORS ─────────────────────────────────────────────────────
builder.Services.AddCors(options =>
{
    options.AddPolicy("MiastralPolicy", policy =>
    {
        policy.WithOrigins(
            "https://miastral.vercel.app",
            "http://localhost:5173"
        )
        .AllowAnyHeader()
        .AllowAnyMethod();
    });
});

builder.Services.AddControllers();

var app = builder.Build();

app.UseCors("MiastralPolicy");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();