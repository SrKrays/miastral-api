using miastral_api.Data;
using miastral_api.Models;
using miastral_api.Services;
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
        private readonly AndreaniShippingService _andreani;

        public OrdenesController(MiastralContext db, AndreaniShippingService andreani)
        {
            _db = db;
            _andreani = andreani;
        }

        private int UsuarioIdActual =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        private bool EsAdmin =>
            User.IsInRole("admin");

        // POST api/ordenes — crea una orden "pendiente" a partir del carrito.
        // El precio y el stock se calculan siempre contra la base de datos.
        // El stock se descuenta recién cuando la orden pasa a "pagado".
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

        // GET api/ordenes — todas las órdenes, solo admin (panel de Vale).
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
                    o.EnvioTransportista,
                    o.EnvioNumeroAndreani,
                    o.EnvioCosto,
                    o.EnvioEtiquetaUrl,
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

        // Suma el peso y volumen de los productos físicos de una orden, para
        // mandárselo a Andreani como un único bulto. Devuelve error si algún
        // producto de la orden todavía no tiene peso/medidas cargadas en el
        // panel admin (Productos → Peso/Alto/Ancho/Largo).
        private async Task<(bool ok, string mensaje, decimal kilos, decimal volumenCm3)> CalcularBultoAsync(int ordenId)
        {
            var orden = await _db.Ordenes.Include(o => o.Items).ThenInclude(i => i.Producto)
                .FirstOrDefaultAsync(o => o.Id == ordenId);
            if (orden == null) return (false, "Orden no encontrada", 0, 0);

            decimal gramos = 0, volumen = 0;
            var faltantes = new List<string>();

            foreach (var item in orden.Items)
            {
                var p = item.Producto;
                if (p == null) continue;
                if (p.PesoGramos == null || p.AltoCm == null || p.AnchoCm == null || p.LargoCm == null)
                {
                    faltantes.Add(p.Nombre);
                    continue;
                }
                gramos += p.PesoGramos.Value * item.Cantidad;
                volumen += (decimal)p.AltoCm.Value * p.AnchoCm.Value * p.LargoCm.Value * item.Cantidad;
            }

            if (faltantes.Count > 0)
                return (false, $"Faltan peso/medidas en el panel admin de estos productos: {string.Join(", ", faltantes.Distinct())}.", 0, 0);

            return (true, "OK", gramos / 1000m, volumen);
        }

        // POST api/ordenes/5/cotizar-envio — solo admin. Cotiza sin generar nada,
        // para que Vale vea el costo antes de imprimir la etiqueta.
        [HttpPost("{id}/cotizar-envio")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> CotizarEnvio(int id)
        {
            var orden = await _db.Ordenes.FindAsync(id);
            if (orden == null) return NotFound(new { message = "Orden no encontrada" });
            if (string.IsNullOrWhiteSpace(orden.EnvioCP))
                return BadRequest(new { message = "Esta orden no tiene código postal de envío cargado." });

            var (okBulto, msjBulto, kilos, volumen) = await CalcularBultoAsync(id);
            if (!okBulto) return BadRequest(new { message = msjBulto });

            var (ok, mensaje, costo) = await _andreani.CotizarAsync(orden.EnvioCP!, kilos, volumen, orden.Total);
            if (!ok) return StatusCode(502, new { message = mensaje });

            return Ok(new { costo });
        }

        // POST api/ordenes/5/generar-envio — solo admin. Crea el pre-envío en
        // Andreani y guarda tracking/costo en la orden. Requiere credenciales
        // de Andreani cargadas (Usuario/Contraseña/Contrato) y peso/medidas
        // cargados en todos los productos físicos de la orden.
        [HttpPost("{id}/generar-envio")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> GenerarEnvio(int id)
        {
            var orden = await _db.Ordenes.FindAsync(id);
            if (orden == null) return NotFound(new { message = "Orden no encontrada" });
            if (string.IsNullOrWhiteSpace(orden.EnvioCP))
                return BadRequest(new { message = "Esta orden no tiene dirección de envío cargada." });
            if (!string.IsNullOrEmpty(orden.EnvioNumeroAndreani))
                return BadRequest(new { message = $"Esta orden ya tiene un envío de Andreani generado (#{orden.EnvioNumeroAndreani})." });

            var (okBulto, msjBulto, kilos, volumen) = await CalcularBultoAsync(id);
            if (!okBulto) return BadRequest(new { message = msjBulto });

            var (ok, mensaje, numero) = await _andreani.CrearEnvioAsync(orden, kilos, volumen);
            if (!ok) return StatusCode(502, new { message = mensaje });

            var (okCotiza, _, costo) = await _andreani.CotizarAsync(orden.EnvioCP!, kilos, volumen, orden.Total);

            orden.EnvioTransportista = "andreani";
            orden.EnvioNumeroAndreani = numero;
            orden.EnvioCosto = okCotiza ? costo : null;
            orden.EnvioEtiquetaUrl = $"{Request.Scheme}://{Request.Host}/api/ordenes/{id}/etiqueta";
            await _db.SaveChangesAsync();

            return Ok(new { orden.EnvioTransportista, orden.EnvioNumeroAndreani, orden.EnvioCosto, orden.EnvioEtiquetaUrl });
        }

        // GET api/ordenes/5/etiqueta — solo admin. Trae el PDF de Andreani al
        // vuelo (no lo guardamos en ningún hosting propio).
        [HttpGet("{id}/etiqueta")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> ObtenerEtiqueta(int id)
        {
            var orden = await _db.Ordenes.FindAsync(id);
            if (orden == null) return NotFound(new { message = "Orden no encontrada" });
            if (string.IsNullOrEmpty(orden.EnvioNumeroAndreani))
                return BadRequest(new { message = "Esta orden todavía no tiene un envío de Andreani generado." });

            var (ok, mensaje, pdf) = await _andreani.ObtenerEtiquetaAsync(orden.EnvioNumeroAndreani);
            if (!ok || pdf == null) return StatusCode(502, new { message = mensaje });

            return File(pdf, "application/pdf", $"etiqueta-orden-{id}.pdf");
        }

        // GET api/ordenes/5/estado-envio — solo admin. Consulta el tracking en
        // Andreani (no toca el campo "Estado" propio de la orden, que Vale
        // sigue manejando a mano desde el selector).
        [HttpGet("{id}/estado-envio")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> EstadoEnvio(int id)
        {
            var orden = await _db.Ordenes.FindAsync(id);
            if (orden == null) return NotFound(new { message = "Orden no encontrada" });
            if (string.IsNullOrEmpty(orden.EnvioNumeroAndreani))
                return BadRequest(new { message = "Esta orden todavía no tiene un envío de Andreani generado." });

            var (ok, mensaje, estado) = await _andreani.ConsultarEstadoAsync(orden.EnvioNumeroAndreani);
            if (!ok) return StatusCode(502, new { message = mensaje });

            return Ok(new { estado });
        }

        // DELETE api/ordenes/5 — solo admin. Borrado real de la orden.
        [HttpDelete("{id}")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> Eliminar(int id)
        {
            var orden = await _db.Ordenes.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id);
            if (orden == null) return NotFound(new { message = "Orden no encontrada" });

            _db.Ordenes.Remove(orden);
            await _db.SaveChangesAsync();

            return NoContent();
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
