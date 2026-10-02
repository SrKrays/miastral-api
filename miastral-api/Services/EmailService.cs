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

        // ── Paleta — tomada tal cual de src/styles/variables.css del frontend,
        // así los mails se sienten parte de la misma marca que la web. ──────
        private const string ColorBg       = "#f7f3ec"; // --bg-cream
        private const string ColorCard     = "#ffffff";
        private const string ColorTextDark = "#19232e"; // --c-900
        private const string ColorTextMid  = "#3a5069"; // --c-700
        private const string ColorTextMute = "#4a6787"; // --c-600
        private const string ColorBlue       = "#7894b5"; // --c-400
        private const string ColorSand       = "#B4A484"; // --accent-coral (hoy es un marrón/arena)
        private const string ColorGold       = "#A6883E"; // --accent-gold
        private const string ColorTerracotta = "#C17B54"; // acento cálido para los títulos grandes (GRACIAS)
        private const string ColorSage       = "#7C8C5B"; // verde salvia para el subtítulo en cursiva
        private const string FontDisplay     = "Georgia,'Cormorant Garamond','Times New Roman',serif";
        private const string FontBody        = "'Helvetica Neue',Helvetica,Arial,sans-serif";

        // Header de marca: nombre + tagline + línea separadora, igual en los dos mails.
        private string Header()
        {
            return $@"
<div style=""text-align:center;"">
  <div style=""font-family:{FontDisplay};font-size:20px;letter-spacing:0.5px;color:{ColorTextDark};"">By Valentina M.</div>
  <div style=""font-family:{FontBody};font-size:11px;letter-spacing:2px;text-transform:uppercase;color:{ColorBlue};margin-top:4px;"">Diseño Humano &amp; Física Cuántica</div>
</div>
<div style=""border-top:1px solid rgba(25,35,46,0.12);margin:20px 0 24px;""></div>";
        }

        // Ilustración de marca (sol/órbita/hojas — public/logo.png) con una
        // estrellita arriba, igual que en la imagen de referencia.
        private string Ilustracion()
        {
            var frontendUrl = (_config["Frontend:Url"] ?? "https://byvalentinam.com").TrimEnd('/');
            return $@"
<div style=""text-align:center;color:{ColorSand};font-size:14px;margin-bottom:8px;"">✦</div>
<div style=""text-align:center;margin-bottom:4px;"">
  <img src=""{frontendUrl}/logo.png"" alt=""By Valentina M."" style=""width:130px;height:auto;"" />
</div>";
        }

        // Título grande estilo editorial: palabra grande en terracota + subtítulo
        // en cursiva salvia + línea/corazón decorativo + bajada de texto.
        private string Titular(string linea1, string linea2, string subtitulo)
        {
            return $@"
<div style=""text-align:center;margin-bottom:28px;"">
  <div style=""font-family:{FontDisplay};font-size:48px;line-height:1.05;letter-spacing:1px;color:{ColorTerracotta};"">{linea1}</div>
  <div style=""font-family:{FontDisplay};font-style:italic;font-size:26px;color:{ColorSage};margin-top:2px;"">{linea2}</div>
  <div style=""text-align:center;margin-top:14px;"">
    <div style=""width:1px;height:14px;background:rgba(25,35,46,0.25);margin:0 auto;""></div>
    <div style=""color:{ColorTerracotta};font-size:15px;margin-top:4px;"">♥</div>
  </div>
  <div style=""font-family:{FontBody};color:{ColorTextMute};font-size:14px;margin-top:14px;"">{subtitulo}</div>
</div>";
        }

        // Pie de marca: línea + estrellita + nombre, igual en los dos mails.
        private string Footer()
        {
            return $@"
<div style=""text-align:center;margin-top:28px;"">
  <div style=""border-top:1px solid rgba(25,35,46,0.12);margin-bottom:12px;""></div>
  <div style=""color:{ColorSand};font-size:12px;margin-bottom:8px;"">✦</div>
  <div style=""font-family:{FontBody};color:{ColorTextMid};font-size:11px;letter-spacing:0.5px;"">By Valentina M. — Diseño Humano &amp; Física Cuántica</div>
</div>";
        }

        private static string TablaItems(Orden orden)
        {
            var filas = orden.Items.Select(i =>
                $"<tr><td style=\"padding:7px 0;color:{ColorTextDark};\">{i.Cantidad}× {(i.Producto?.Nombre ?? "Producto")}</td>" +
                $"<td style=\"padding:7px 0;text-align:right;color:{ColorTextDark};\">{FormatARS(i.PrecioUnitario * i.Cantidad)}</td></tr>");
            return string.Join("", filas);
        }

        // ── 1) Mail al cliente ──────────────────────────────────────────
        public async Task<(bool ok, string mensaje)> EnviarConfirmacionClienteAsync(Orden orden)
        {
            var nombre = orden.EnvioNombre ?? orden.Usuario?.Nombre ?? "";
            var destino = orden.EnvioEmail ?? orden.Usuario?.Email;

            var hayFisico = orden.Items.Any(i => i.Producto?.Tipo == "producto");
            var notaEnvio = hayFisico
                ? $"<p style=\"color:{ColorGold};font-family:{FontBody};font-size:13px;background:rgba(166,136,62,0.08);border-radius:8px;padding:12px 14px;\">Tu compra incluye un producto físico — en breve nos ponemos en contacto para coordinar la entrega.</p>"
                : "";

            var html = $@"
<div style=""background:{ColorBg};padding:40px 20px;"">
  <div style=""max-width:560px;margin:0 auto;"">
    {Header()}
    {Ilustracion()}
    {Titular("GRACIAS", "Por tu compra", "Esperamos que lo disfrutes. Cada producto está hecho con mucho amor.")}

    <div style=""background:{ColorCard};border:1px solid rgba(25,35,46,0.1);border-radius:12px;padding:32px;font-family:{FontBody};"">
      <div style=""font-family:{FontDisplay};font-size:22px;color:{ColorTerracotta};margin-bottom:6px;"">¡Gracias por tu compra{(string.IsNullOrEmpty(nombre) ? "" : $", {nombre}")}!</div>
      <div style=""color:{ColorTextMute};font-size:14px;margin-bottom:20px;"">Tu pedido #{orden.Id} quedó confirmado. Acá el resumen:</div>
      <table style=""width:100%;border-collapse:collapse;margin-bottom:8px;"">
        {TablaItems(orden)}
        <tr><td style=""padding-top:12px;border-top:1px solid rgba(25,35,46,0.12);font-weight:bold;color:{ColorTextDark};"">Total</td>
            <td style=""padding-top:12px;border-top:1px solid rgba(25,35,46,0.12);text-align:right;font-weight:bold;color:{ColorTextDark};"">{FormatARS(orden.Total)}</td></tr>
      </table>
      {(hayFisico ? $"<div style=\"margin:16px 0;\">{notaEnvio}</div>" : "")}
      <div style=""border-top:1px solid rgba(25,35,46,0.1);margin-top:20px;padding-top:16px;color:{ColorTextMute};font-size:13px;"">
        Cualquier duda, escribinos — ¡gracias por confiar en este espacio!
      </div>
    </div>
    {Footer()}
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
            // Va en una caja resaltada (tono arena) para que salte a la vista.
            var datosEnvio = $@"
<div style=""background:rgba(180,164,132,0.1);border:1px solid rgba(180,164,132,0.3);border-radius:10px;padding:18px 20px;margin:10px 0;"">
  <table style=""width:100%;border-collapse:collapse;"">
    <tr><td style=""padding:4px 0;color:{ColorGold};font-size:13px;width:140px;"">Destinatario</td><td style=""padding:4px 0;color:{ColorTextDark};"">{orden.EnvioNombre ?? "—"}</td></tr>
    <tr><td style=""padding:4px 0;color:{ColorGold};font-size:13px;"">Teléfono</td><td style=""padding:4px 0;color:{ColorTextDark};"">{orden.EnvioTelefono ?? "—"}</td></tr>
    <tr><td style=""padding:4px 0;color:{ColorGold};font-size:13px;"">Email</td><td style=""padding:4px 0;color:{ColorTextDark};"">{orden.EnvioEmail ?? "—"}</td></tr>
    <tr><td style=""padding:4px 0;color:{ColorGold};font-size:13px;"">Calle y número</td><td style=""padding:4px 0;color:{ColorTextDark};"">{orden.EnvioCalle ?? "—"}</td></tr>
    <tr><td style=""padding:4px 0;color:{ColorGold};font-size:13px;"">Ciudad</td><td style=""padding:4px 0;color:{ColorTextDark};"">{orden.EnvioCiudad ?? "—"}</td></tr>
    <tr><td style=""padding:4px 0;color:{ColorGold};font-size:13px;"">Provincia</td><td style=""padding:4px 0;color:{ColorTextDark};"">{orden.EnvioProvincia ?? "—"}</td></tr>
    <tr><td style=""padding:4px 0;color:{ColorGold};font-size:13px;"">Código postal</td><td style=""padding:4px 0;color:{ColorTextDark};"">{orden.EnvioCP ?? "—"}</td></tr>
  </table>
</div>";

            var pesos = orden.Items
                .Where(i => i.Producto?.PesoGramos != null)
                .Select(i => $"<div style=\"color:{ColorTextMute};font-size:12px;font-family:{FontBody};\">{i.Producto!.Nombre}: {i.Producto.PesoGramos}g · {i.Producto.AltoCm}×{i.Producto.AnchoCm}×{i.Producto.LargoCm}cm</div>");

            var html = $@"
<div style=""background:{ColorBg};padding:40px 20px;"">
  <div style=""max-width:560px;margin:0 auto;"">
    {Header()}
    {Ilustracion()}
    {Titular("VENTA", "Confirmada", $"Pedido #{orden.Id} · {orden.FechaCreacion:dd/MM/yyyy HH:mm} · Mercado Pago · pago #{orden.MpPaymentId}")}

    <div style=""background:{ColorCard};border:1px solid rgba(25,35,46,0.1);border-radius:12px;padding:32px;font-family:{FontBody};"">
      <div style=""font-family:{FontDisplay};font-size:20px;color:{ColorTerracotta};margin-bottom:16px;"">Pedido #{orden.Id}</div>

      <div style=""font-family:{FontBody};font-size:11px;text-transform:uppercase;letter-spacing:0.08em;color:{ColorBlue};margin-bottom:8px;"">Productos</div>
      <table style=""width:100%;border-collapse:collapse;margin-bottom:4px;"">
        {TablaItems(orden)}
        <tr><td style=""padding-top:12px;border-top:1px solid rgba(25,35,46,0.12);font-weight:bold;color:{ColorTextDark};"">Total</td>
            <td style=""padding-top:12px;border-top:1px solid rgba(25,35,46,0.12);text-align:right;font-weight:bold;color:{ColorTextDark};"">{FormatARS(orden.Total)}</td></tr>
      </table>
      {string.Join("", pesos)}

      <div style=""font-family:{FontBody};font-size:11px;text-transform:uppercase;letter-spacing:0.08em;color:{ColorBlue};margin-top:24px;margin-bottom:4px;"">Datos para Correo Argentino</div>
      {datosEnvio}

      <div style=""color:{ColorTextMute};font-size:12px;margin-top:16px;"">También podés ver este pedido y copiar estos datos con un clic desde el panel admin → Órdenes.</div>
    </div>
    {Footer()}
  </div>
</div>";

            return await EnviarAsync(destino ?? "", $"Nueva venta #{orden.Id} — {orden.Items.FirstOrDefault()?.Producto?.Nombre ?? "pedido"}", html);
        }
    }
}
