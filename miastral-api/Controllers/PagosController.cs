using MercadoPago.Client.Payment;
using MercadoPago.Client.Preference;
using MercadoPago.Resource.Payment;
using miastral_api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace miastral_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PagosController : ControllerBase
    {
        private readonly MiastralContext _db;
        private readonly IConfiguration _config;

        public PagosController(MiastralContext db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        private int UsuarioIdActual =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        // POST api/pagos/ordenes/5/preferencia — genera el link de pago de MercadoPago
        // para una orden pendiente del usuario logueado.
        [HttpPost("ordenes/{ordenId}/preferencia")]
        [Authorize]
        public async Task<IActionResult> CrearPreferencia(int ordenId)
        {
            var orden = await _db.Ordenes
                .Include(o => o.Items).ThenInclude(i => i.Producto)
                .FirstOrDefaultAsync(o => o.Id == ordenId);

            if (orden == null) return NotFound(new { message = "Orden no encontrada" });
            if (orden.UsuarioId != UsuarioIdActual) return Forbid();
            if (orden.Estado != "pendiente") return BadRequest(new { message = "Esta orden ya no está pendiente de pago" });
            if (orden.Items.Count == 0) return BadRequest(new { message = "La orden no tiene items" });

            var frontendUrl = _config["Frontend:Url"];
            var backendUrl = _config["Backend:Url"];

            var request = new PreferenceRequest
            {
                ExternalReference = orden.Id.ToString(),
                Items = orden.Items.Select(i => new PreferenceItemRequest
                {
                    Id = i.ProductoId.ToString(),
                    Title = i.Producto?.Nombre ?? "Producto",
                    Quantity = i.Cantidad,
                    CurrencyId = "ARS",
                    UnitPrice = i.PrecioUnitario,
                }).ToList(),
                BackUrls = new PreferenceBackUrlsRequest
                {
                    Success = $"{frontendUrl}/carrito/confirmacion?orden={orden.Id}",
                    Pending = $"{frontendUrl}/carrito/confirmacion?orden={orden.Id}",
                    Failure = $"{frontendUrl}/carrito?pago=fallido",
                },
                AutoReturn = "approved",
                NotificationUrl = $"{backendUrl}/api/pagos/webhook",
            };

            var client = new PreferenceClient();
            var preference = await client.CreateAsync(request);

            orden.MetodoPago = "mercadopago";
            await _db.SaveChangesAsync();

            return Ok(new { initPoint = preference.InitPoint, preferenceId = preference.Id });
        }

        // POST api/pagos/webhook — Mercado Pago llama acá cuando se crea o actualiza un pago.
        //
        // Es público (MP no manda nuestro JWT). Por eso nunca confiamos en los datos que
        // vienen en la notificación en sí — solo usamos el id de pago que nos avisan para
        // volver a pedirle el estado real a la API de MP con nuestro propio access token,
        // y recién ahí actualizamos la orden.
        //
        // MP puede mandar el aviso como query params (?type=payment&data.id=123) o como
        // body JSON ({"type":"payment","data":{"id":"123"}}) según cómo se haya configurado
        // — contemplamos las dos formas.
        [HttpPost("webhook")]
        [AllowAnonymous]
        public async Task<IActionResult> Webhook(
            [FromQuery] string? type,
            [FromQuery] string? topic,
            [FromQuery(Name = "data.id")] string? dataId,
            [FromQuery] string? id,
            [FromBody] MpWebhookPayload? payload)
        {
            var tipo = type ?? topic ?? payload?.Type;
            var paymentIdRaw = dataId ?? id ?? payload?.Data?.Id;

            if (tipo != "payment" || string.IsNullOrEmpty(paymentIdRaw) || !long.TryParse(paymentIdRaw, out var paymentId))
                return Ok(); // no es un evento de pago, pero igual confirmamos recepción

            Payment payment;
            try
            {
                var client = new PaymentClient();
                payment = await client.GetAsync(paymentId);
            }
            catch
            {
                // Si MP todavía no tiene el pago disponible o hay un hiccup de red,
                // devolvemos 200 igual: MP reintenta solo cada 15 min si no confirmamos,
                // y no queremos generar reintentos infinitos por un error transitorio.
                return Ok();
            }

            if (payment?.ExternalReference == null || !int.TryParse(payment.ExternalReference, out var ordenId))
                return Ok();

            var orden = await _db.Ordenes.FindAsync(ordenId);
            if (orden == null) return Ok();

            orden.MpPaymentId = payment.Id.ToString();
            orden.Estado = payment.Status switch
            {
                PaymentStatus.Approved => "pagado",
                PaymentStatus.Rejected => "cancelado",
                PaymentStatus.Cancelled => "cancelado",
                _ => orden.Estado, // pending / in_process / in_mediation: la dejamos como está
            };

            await _db.SaveChangesAsync();
            return Ok();
        }

        // GET api/pagos/ordenes/5/estado — la pantalla de confirmación del front consulta
        // esto cuando MP redirige de vuelta, para saber si ya llegó el webhook o todavía no.
        [HttpGet("ordenes/{ordenId}/estado")]
        [Authorize]
        public async Task<IActionResult> GetEstado(int ordenId)
        {
            var orden = await _db.Ordenes.FindAsync(ordenId);
            if (orden == null) return NotFound(new { message = "Orden no encontrada" });
            if (orden.UsuarioId != UsuarioIdActual) return Forbid();

            return Ok(new { orden.Id, orden.Estado, orden.Total });
        }
    }

    public class MpWebhookPayload
    {
        public string? Type { get; set; }
        public MpWebhookData? Data { get; set; }
    }

    public class MpWebhookData
    {
        public string? Id { get; set; }
    }
}
