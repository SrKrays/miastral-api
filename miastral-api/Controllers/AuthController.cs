using miastral_api.Data;
using miastral_api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace miastral_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly MiastralContext _db;
        private readonly IConfiguration _config;

        public AuthController(MiastralContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        // POST api/auth/login — usuarios clientes
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            var usuario = await _db.Usuarios
                .FirstOrDefaultAsync(u => u.Email == request.Email && u.Activo);

            if (usuario == null || usuario.PasswordHash == null || !BCrypt.Net.BCrypt.Verify(request.Password, usuario.PasswordHash))
                return Unauthorized(new { message = "Email o contraseña incorrectos" });

            usuario.LastLogin = DateTime.Now;
            await _db.SaveChangesAsync();

            var token = GenerarTokenUsuario(usuario);
            return Ok(new { token, nombre = usuario.Nombre, email = usuario.Email });
        }

        // POST api/auth/registro — registro de usuarios clientes
        [HttpPost("registro")]
        public async Task<IActionResult> Registro([FromBody] RegistroRequest request)
        {
            if (await _db.Usuarios.AnyAsync(u => u.Email == request.Email))
                return BadRequest(new { message = "Ya existe una cuenta con ese email" });

            var usuario = new Usuario
            {
                Nombre = request.Nombre,
                Apellido = request.Apellido,
                Email = request.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Telefono = request.Telefono ?? "",
                CreatedAt = DateTime.Now,
                Activo = true
            };

            _db.Usuarios.Add(usuario);
            await _db.SaveChangesAsync();

            var token = GenerarTokenUsuario(usuario);
            return Ok(new { token, nombre = usuario.Nombre, email = usuario.Email });
        }

        // POST api/auth/admin/login — login exclusivo para Valentina (admin)
        [HttpPost("admin/login")]
        public async Task<IActionResult> AdminLogin([FromBody] LoginRequest request)
        {
            var admin = await _db.Admins
                .FirstOrDefaultAsync(a => a.Email == request.Email && a.Activo);

            if (admin == null || !BCrypt.Net.BCrypt.Verify(request.Password, admin.PasswordHash))
                return Unauthorized(new { message = "Credenciales incorrectas" });

            admin.LastLogin = DateTime.Now;
            await _db.SaveChangesAsync();

            var token = GenerarTokenAdmin(admin);
            return Ok(new { token, nombre = admin.Nombre });
        }

        // POST api/auth/setup — crear primer admin (usar una sola vez)
        [HttpPost("setup")]
        public async Task<IActionResult> Setup([FromBody] SetupRequest request)
        {
            if (await _db.Admins.AnyAsync())
                return BadRequest(new { message = "Ya existe un administrador" });

            var admin = new Admin
            {
                Email = request.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                Nombre = request.Nombre,
                CreatedAt = DateTime.Now,
                Activo = true
            };

            _db.Admins.Add(admin);
            await _db.SaveChangesAsync();

            return Ok(new { message = "Administrador creado correctamente" });
        }

        private string GenerarTokenUsuario(Usuario usuario)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, usuario.Id.ToString()),
                new Claim(ClaimTypes.Email, usuario.Email),
                new Claim(ClaimTypes.Name, usuario.Nombre),
                new Claim(ClaimTypes.Role, "usuario")
            };
            return GenerarToken(claims);
        }

        private string GenerarTokenAdmin(Admin admin)
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                new Claim(ClaimTypes.Email, admin.Email),
                new Claim(ClaimTypes.Name, admin.Nombre),
                new Claim(ClaimTypes.Role, "admin")
            };
            return GenerarToken(claims);
        }

        private string GenerarToken(Claim[] claims)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddHours(8),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public class LoginRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public class RegistroRequest
    {
        public string Nombre { get; set; } = "";
        public string Apellido { get; set; } = "";
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string? Telefono { get; set; }
    }

    public class SetupRequest
    {
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string Nombre { get; set; } = "";
    }
}