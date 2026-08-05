using miastral_api.Data;
using miastral_api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace miastral_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // hace falta estar logueado (usuario o admin) para todo este controller
    public class OrdenesController : ControllerBase
    {
        private readonly MiastralContext _db;

        public OrdenesController(MiastralContext db)
        {
            _db = db;
        }

        private int UsuarioIdActual =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        private bool EsAdmin =>
            User.IsInRole("admin");

        // POST api/ordenes — crea una orden "pendiente" a partir del carrito.
        // El precio y el stock se validan siempre contra la BD, nunca contra lo que
        // mande el cliente (evita que alguien manipule el total desde el navegador).
        //
        // Importante: todavía no descontamos stock acá. El stock se descuenta recién
        // cuando la orden pasa a "pagado" (Fase 3, webhook de MercadoPago) — si
        // descontáramos ahora, un carrito abandonado sin pagar dejaría el producto
        // reservado para siempre.
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CrearOrdenRequest request)
        {
            if (request.Items == null || request.Items.Count == 0)
                return BadRequest(new { message = "El carrito está vacío" });

            var productoIds = request.Items.Select(i => i.ProductoId).ToList();
            var productos = await _db.Productos
                .Where(p => productoIds.Contains(p.Id) && p.Activo)
                .ToListAsync();

            var errores = new List<string>();
            var orden = new Orden
            {
                UsuarioId = UsuarioIdActual,
                Estado = "pendiente",
                EnvioNombre = request.Envio?.Nombre,
                EnvioEmail = request.Envio?.Email,
                EnvioTelefono = request.Envio?.Telefono,
                EnvioCalle = request.Envio?.Calle,
                EnvioCiudad = request.Envio?.Ciudad,
                EnvioProvincia = request.Envio?.Provincia,
                EnvioCP = request.Envio?.Cp,
            };

            decimal total = 0;

            foreach (var item in request.Items)
            {
                var producto = productos.FirstOrDefault(p => p.Id == item.ProductoId);
                if (producto == null)
                {
                    errores.Add($"Un producto de tu carrito ya no está disponible.");
                    continue;
                }
                if (item.Cantidad < 1)
                {
                    errores.Add($"Cantidad inválida para \"{producto.Nombre}\".");
                    continue;
                }
                if (producto.Stock.HasValue && producto.Stock.Value < item.Cantidad)
                {
                    errores.Add($"\"{producto.Nombre}\" solo tiene {producto.Stock.Value} unidades disponibles.");
                    continue;
                }
                if (producto.Precio == null)
                {
                    errores.Add($"\"{producto.Nombre}\" no tiene precio fijo — consultá por mail o WhatsApp.");
                    continue;
                }

                var precioUnitario = producto.Precio.Value;
                total += precioUnitario * item.Cantidad;

                orden.Items.Add(new OrdenItem
                {
                    ProductoId = producto.Id,
                    Cantidad = item.Cantidad,
                    PrecioUnitario = precioUnitario,
                });
            }

            if (errores.Count > 0)
                return BadRequest(new { message = string.Join(" ", errores) });

            orden.Total = total;

            _db.Ordenes.Add(orden);
            await _db.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = orden.Id }, orden);
        }

        // GET api/ordenes — TODAS las órdenes, solo admin (panel de Vale).
        // Proyectamos a mano en vez de devolver el Usuario completo: la entidad
        // Usuario trae PasswordHash y no queremos que eso viaje en la respuesta.
        [HttpGet]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> GetAllAdmin()
        {
            var ordenes = await _db.Ordenes
                .Include(o => o.Items).ThenInclude(i => i.Producto)
                .Include(o => o.Usuario)
                .OrderByDescending(o => o.FechaCreacion)
                .Select(o => new
                {
                    o.Id,
                    o.FechaCreacion,
                    o.Estado,
                    o.Total,
                    o.MetodoPago,
                    o.MpPaymentId,
                    o.EnvioNombre,
                    o.EnvioEmail,
                    o.EnvioTelefono,
                    o.EnvioCalle,
                    o.EnvioCiudad,
                    o.EnvioProvincia,
                    o.EnvioCP,
                    Comprador = o.Usuario == null ? null : new { o.Usuario.Nombre, o.Usuario.Apellido, o.Usuario.Email },
                    Items = o.Items.Select(i => new { i.Id, i.ProductoId, i.Cantidad, i.PrecioUnitario, ProductoNombre = i.Producto != null ? i.Producto.Nombre : null }),
                })
                .ToListAsync();

            return Ok(ordenes);
        }

        // PUT api/ordenes/5/estado — solo admin. Para marcar "enviado", corregir a mano, etc.
        [HttpPut("{id}/estado")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> UpdateEstado(int id, [FromBody] ActualizarEstadoRequest request)
        {
            var estadosValidos = new[] { "pendiente", "pagado", "enviado", "cancelado" };
            if (!estadosValidos.Contains(request.Estado))
                return BadRequest(new { message = "Estado inválido" });

            var orden = await _db.Ordenes.FindAsync(id);
            if (orden == null) return NotFound(new { message = "Orden no encontrada" });

            orden.Estado = request.Estado;
            await _db.SaveChangesAsync();

            return Ok(orden);
        }

        // GET api/ordenes/mis-ordenes — historial del usuario logueado (para "Mi cuenta")
        [HttpGet("mis-ordenes")]
        public async Task<IActionResult> GetMisOrdenes()
        {
            var ordenes = await _db.Ordenes
                .Where(o => o.UsuarioId == UsuarioIdActual)
                .Include(o => o.Items).ThenInclude(i => i.Producto)
                .OrderByDescending(o => o.FechaCreacion)
                .ToListAsync();

            return Ok(ordenes);
        }

        // DELETE api/ordenes/5 — solo admin. Borrado real (no soft-delete): para
        // sacar de encima órdenes de prueba que no van a ningún lado. Los
        // orden_items se borran solos por el Cascade configurado en el DbContext.
        [HttpDelete("{id}")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> Delete(int id)
        {
            var orden = await _db.Ordenes.FindAsync(id);
            if (orden == null) return NotFound(new { message = "Orden no encontrada" });

            _db.Ordenes.Remove(orden);
            await _db.SaveChangesAsync();

            return NoContent();
        }

        // GET api/ordenes/5 — el dueño de la orden, o un admin
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var orden = await _db.Ordenes
                .Include(o => o.Items).ThenInclude(i => i.Producto)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (orden == null) return NotFound(new { message = "Orden no encontrada" });
            if (orden.UsuarioId != UsuarioIdActual && !EsAdmin) return Forbid();

            return Ok(orden);
        }
    }

    public class CrearOrdenRequest
    {
        public List<CrearOrdenItem> Items { get; set; } = new();
        public EnvioRequest? Envio { get; set; }
    }

    public class CrearOrdenItem
    {
        public int ProductoId { get; set; }
        public int Cantidad { get; set; }
    }

    public class EnvioRequest
    {
        public string? Nombre { get; set; }
        public string? Email { get; set; }
        public string? Telefono { get; set; }
        public string? Calle { get; set; }
        public string? Ciudad { get; set; }
        public string? Provincia { get; set; }
        public string? Cp { get; set; }
    }

    public class ActualizarEstadoRequest
    {
        public string Estado { get; set; } = "";
    }
}
