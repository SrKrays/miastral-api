using miastral_api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace miastral_api.Controllers
{
    // Endpoints que usa el checkout: lista de sucursales de Correo Argentino y
    // cotización en vivo. Hace falta estar logueado (igual que el carrito).
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class EnviosController : ControllerBase
    {
        private readonly MiCorreoService _micorreo;
        private readonly EnvioCotizadorService _cotizador;

        public EnviosController(MiCorreoService micorreo, EnvioCotizadorService cotizador)
        {
            _micorreo = micorreo;
            _cotizador = cotizador;
        }

        // GET api/envios/sucursales?provincia=B
        [HttpGet("sucursales")]
        public async Task<IActionResult> Sucursales([FromQuery] string provincia)
        {
            if (string.IsNullOrWhiteSpace(provincia) || provincia.Length != 1)
                return BadRequest(new { message = "Elegí una provincia." });
            if (!_micorreo.CredencialesCargadas)
                return StatusCode(503, new { message = "El listado de sucursales todavía no está disponible." });

            var (ok, mensaje, lista) = await _micorreo.ObtenerSucursalesAsync(provincia.ToUpperInvariant());
            if (!ok) return StatusCode(502, new { message = mensaje });

            return Ok(lista);
        }

        // POST api/envios/cotizar
        // Sin destino devuelve solo si el carrito requiere envío; con destino, el costo.
        [HttpPost("cotizar")]
        public async Task<IActionResult> Cotizar([FromBody] CotizarEnvioRequest req)
        {
            if (req.Items == null || req.Items.Count == 0)
                return BadRequest(new { message = "El carrito está vacío" });

            var r = await _cotizador.CotizarAsync(
                req.Items.Select(i => (i.ProductoId, i.Cantidad)),
                req.TipoEntrega, req.ProvinciaCodigo?.ToUpperInvariant(), req.Cp, req.SucursalCodigo);

            if (!r.Ok) return BadRequest(new { message = r.Mensaje });

            return Ok(new
            {
                requiereEnvio = r.RequiereEnvio,
                configurado = r.Configurado,
                costo = r.Costo,
                diasMin = r.DiasMin,
                diasMax = r.DiasMax,
                sucursalNombre = r.SucursalNombre,
            });
        }
    }

    public class CotizarEnvioRequest
    {
        public List<CrearOrdenItem> Items { get; set; } = new();
        public string? TipoEntrega { get; set; }      // "domicilio" | "sucursal"
        public string? ProvinciaCodigo { get; set; }  // letra MiCorreo
        public string? Cp { get; set; }               // solo domicilio
        public string? SucursalCodigo { get; set; }   // solo sucursal
    }
}
