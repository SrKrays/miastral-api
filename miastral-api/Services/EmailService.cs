using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using miastral_api.Models;

namespace miastral_api.Services
{
    // Manda los dos mails automáticos que se disparan cuando una orden pasa a
    // "pagado" (ver el webhook de PagosController):
    //   1) Al cliente: agradecimiento + resumen de su compra.
    //   2) A Vale: aviso de venta + los datos del cliente ya ordenados tal cual
    //      los pide el formulario de Correo Argentino, para copiar y pegar.
    //
    // Usa la API HTTP de Resend (https://resend.com) en vez de SMTP directo:
    // Render bloquea los puertos SMTP (465/587) salientes en su plan gratuito
    // desde sept/2025 (confirmado — cualquier intento de SMTP directo da
    // TimeoutException), así que mandar por HTTPS es la única opción viable
    // sin pagar un plan de Render más caro.
    //
    // Necesita: una cuenta gratis en resend.com, el dominio byvalentinam.com
    // verificado ahí (un par de registros DNS), y un API Key generado en su
    // dashboard. Mientras Email:ResendApiKey no esté cargado, los métodos no
    // fallan la compra: solo devuelven ok=false y el webhook lo loguea sin
    // romper nada.
    public class EmailService
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration config, IHttpClientFactory httpFactory, ILogger<EmailService> logger)
        {
            _config = config;
            _httpFactory = httpFactory;
            _logger = logger;
        }

        private bool CredencialesCargadas => !string.IsNullOrWhiteSpace(_config["Email:ResendApiKey"]);

        private static string FormatARS(decimal n) => $"${n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("es-AR"))}";

        // ── Envío genérico — POST https://api.resend.com/emails ──────────
        private async Task<(bool ok, string mensaje)> EnviarAsync(string destinatario, string asunto, string htmlBody)
        {
            if (!CredencialesCargadas)
                return (false, "Todavía no cargamos la API Key de Resend (Email:ResendApiKey).");

            if (string.IsNullOrWhiteSpace(destinatario))
                return (false, "No hay un email de destino para mandar este mail.");

            try
            {
                var remitenteNombre = _config["Email:NombreRemitente"] ?? "By Valentina M.";
                var remitenteDireccion = _config["Email:FromAddress"] ?? "notificaciones@byvalentinam.com";

                var payload = new
                {
                    from = $"{remitenteNombre} <{remitenteDireccion}>",
                    to = new[] { destinatario },
                    subject = asunto,
                    html = htmlBody,
                };

                var client = _httpFactory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config["Email:ResendApiKey"]);
                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await client.PostAsync("https://api.resend.com/emails", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Resend rechazó el mail a {Destinatario} ({Status}): {Body}", destinatario, (int)response.StatusCode, body);
                    return (false, $"Resend rechazó el mail ({(int)response.StatusCode}): {body}");
                }

                return (true, "OK");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No pudimos mandar el mail a {Destinatario}", destinatario);
                return (false, $"No pudimos mandar el mail: {ex.Message}");
            }
        }

        // ── Header común (logo si está cargado, si no el nombre en texto) ──
        private string Header()
        {
            var logo = _config["Email:LogoUrl"];
            return string.IsNullOrWhiteSpace(logo)
                ? "<div style=\"font-family:Georgia,serif;font-size:22px;color:#f2e4d8;letter-spacing:1px;\">By Valentina M.</div>"
                : $"<img src=\"{logo}\" alt=\"By Valentina M.\" style=\"max-height:60px;\" />";
        }

        private static string TablaItems(Orden orden)
        {
            var filas = orden.Items.Select(i =>
                $"<tr><td style=\"padding:6px 0;\">{i.Cantidad}× {(i.Producto?.Nombre ?? "Producto")}</td>" +
                $"<td style=\"padding:6px 0;text-align:right;\">{FormatARS(i.PrecioUnitario * i.Cantidad)}</td></tr>");
            return string.Join("", filas);
        }

        // ── 1) Mail al cliente ──────────────────────────────────────────
        public async Task<(bool ok, string mensaje)> EnviarConfirmacionClienteAsync(Orden orden)
        {
            var nombre = orden.EnvioNombre ?? orden.Usuario?.Nombre ?? "";
            var destino = orden.EnvioEmail ?? orden.Usuario?.Email;

            var hayFisico = orden.Items.Any(i => i.Producto?.Tipo == "producto");
            var notaEnvio = hayFisico
                ? "<p style=\"color:#8fa9c9;\">Tu compra incluye un producto físico — en breve nos ponemos en contacto para coordinar la entrega.</p>"
                : "";

            var html = $@"
<div style=""background:#0d1017;padding:32px 24px;font-family:Arial,Helvetica,sans-serif;color:#e8e4dc;"">
  <div style=""max-width:520px;margin:0 auto;background:#161a24;border-radius:12px;padding:32px;"">
    <div style=""text-align:center;margin-bottom:24px;"">{Header()}</div>
    <h2 style=""color:#f2e4d8;font-weight:400;"">¡Gracias por tu compra{(string.IsNullOrEmpty(nombre) ? "" : $", {nombre}")}!</h2>
    <p style=""color:#b8b4ac;"">Tu pedido #{orden.Id} quedó confirmado. Acá el resumen:</p>
    <table style=""width:100%;border-collapse:collapse;margin:16px 0;color:#e8e4dc;"">
      {TablaItems(orden)}
      <tr><td style=""padding-top:12px;border-top:1px solid #2a2f3a;font-weight:bold;"">Total</td>
          <td style=""padding-top:12px;border-top:1px solid #2a2f3a;text-align:right;font-weight:bold;"">{FormatARS(orden.Total)}</td></tr>
    </table>
    {notaEnvio}
    <p style=""color:#b8b4ac;margin-top:24px;"">Cualquier duda, escribinos — ¡gracias por confiar en este espacio!</p>
    <p style=""color:#6f7684;font-size:12px;margin-top:32px;"">By Valentina M. — Diseño Humano & Física Cuántica</p>
  </div>
</div>";

            return await EnviarAsync(destino ?? "", $"¡Gracias por tu compra! — Pedido #{orden.Id}", html);
        }

        // ── 2) Mail a Vale: aviso de venta + datos listos para Correo Argentino ──
        public async Task<(bool ok, string mensaje)> EnviarNotificacionVentaAsync(Orden orden)
        {
            var destino = _config["Email:EmailVale"];

            // Mismo orden de campos que pide el formulario web de Correo Argentino,
            // para que Vale pueda ir copiando uno abajo del otro sin buscar nada.
            var datosEnvio = $@"
<table style=""width:100%;border-collapse:collapse;margin:12px 0;color:#e8e4dc;"">
  <tr><td style=""padding:4px 0;color:#8fa9c9;"">Destinatario</td><td style=""padding:4px 0;"">{orden.EnvioNombre ?? "—"}</td></tr>
  <tr><td style=""padding:4px 0;color:#8fa9c9;"">Teléfono</td><td style=""padding:4px 0;"">{orden.EnvioTelefono ?? "—"}</td></tr>
  <tr><td style=""padding:4px 0;color:#8fa9c9;"">Email</td><td style=""padding:4px 0;"">{orden.EnvioEmail ?? "—"}</td></tr>
  <tr><td style=""padding:4px 0;color:#8fa9c9;"">Calle y número</td><td style=""padding:4px 0;"">{orden.EnvioCalle ?? "—"}</td></tr>
  <tr><td style=""padding:4px 0;color:#8fa9c9;"">Ciudad</td><td style=""padding:4px 0;"">{orden.EnvioCiudad ?? "—"}</td></tr>
  <tr><td style=""padding:4px 0;color:#8fa9c9;"">Provincia</td><td style=""padding:4px 0;"">{orden.EnvioProvincia ?? "—"}</td></tr>
  <tr><td style=""padding:4px 0;color:#8fa9c9;"">Código postal</td><td style=""padding:4px 0;"">{orden.EnvioCP ?? "—"}</td></tr>
</table>";

            var pesos = orden.Items
                .Where(i => i.Producto?.PesoGramos != null)
                .Select(i => $"<div style=\"color:#8fa9c9;font-size:12px;\">{i.Producto!.Nombre}: {i.Producto.PesoGramos}g · {i.Producto.AltoCm}×{i.Producto.AnchoCm}×{i.Producto.LargoCm}cm</div>");

            var html = $@"
<div style=""background:#0d1017;padding:32px 24px;font-family:Arial,Helvetica,sans-serif;color:#e8e4dc;"">
  <div style=""max-width:560px;margin:0 auto;background:#161a24;border-radius:12px;padding:32px;"">
    <h2 style=""color:#f2e4d8;font-weight:400;"">Nueva venta — Pedido #{orden.Id}</h2>
    <p style=""color:#b8b4ac;"">{orden.FechaCreacion:dd/MM/yyyy HH:mm} · Mercado Pago · pago #{orden.MpPaymentId}</p>

    <h3 style=""color:#e8735a;font-weight:400;font-size:14px;text-transform:uppercase;letter-spacing:0.05em;margin-top:24px;"">Productos</h3>
    <table style=""width:100%;border-collapse:collapse;margin:8px 0;color:#e8e4dc;"">
      {TablaItems(orden)}
      <tr><td style=""padding-top:12px;border-top:1px solid #2a2f3a;font-weight:bold;"">Total</td>
          <td style=""padding-top:12px;border-top:1px solid #2a2f3a;text-align:right;font-weight:bold;"">{FormatARS(orden.Total)}</td></tr>
    </table>
    {string.Join("", pesos)}

    <h3 style=""color:#e8735a;font-weight:400;font-size:14px;text-transform:uppercase;letter-spacing:0.05em;margin-top:24px;"">Datos para Correo Argentino</h3>
    {datosEnvio}

    <p style=""color:#6f7684;font-size:12px;margin-top:24px;"">También podés ver este pedido y copiar estos datos con un clic desde el panel admin → Órdenes.</p>
  </div>
</div>";

            return await EnviarAsync(destino ?? "", $"Nueva venta #{orden.Id} — {orden.Items.FirstOrDefault()?.Producto?.Nombre ?? "pedido"}", html);
        }
    }
}
