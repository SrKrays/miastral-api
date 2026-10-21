using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using miastral_api.Models;

namespace miastral_api.Services
{
    // Integración con la API MiCorreo de Correo Argentino (doc "Correo Argentino -
    // API MiCorreo", versión 2026-05-18). Hace cuatro cosas:
    //   1) Listar sucursales de una provincia (para que el cliente elija dónde retirar).
    //   2) Cotizar un envío (a domicilio "D" o a sucursal "S").
    //   3) Importar el envío a MiCorreo cuando la orden se paga (reemplaza el
    //      copiar y pegar manual de Vale).
    //   4) Consultar el seguimiento.
    //
    // Config (appsettings / variables de entorno de Render, formato MiCorreo__Usuario):
    //   MiCorreo:BaseUrl        https://apitest.correoargentino.com.ar/micorreo/v1 (test)
    //                           https://api.correoargentino.com.ar/micorreo/v1     (producción)
    //   MiCorreo:Usuario        usuario de API que mandó Correo Argentino
    //   MiCorreo:Password       contraseña de API
    //   MiCorreo:CustomerId     id de cliente MiCorreo de Vale (si no lo tenés, cargá
    //                           CuentaEmail + CuentaPassword y lo busca solo)
    //   MiCorreo:CuentaEmail    email de la cuenta MiCorreo de Vale (opcional)
    //   MiCorreo:CuentaPassword contraseña de esa cuenta (opcional)
    //   MiCorreo:OrigenCP       código postal desde donde despacha Vale
    //
    // Mientras falten Usuario/Password/CustomerId/OrigenCP, CredencialesCargadas es
    // false y el resto del sistema se comporta como antes (sin cotizar ni importar).
    public class MiCorreoService
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpFactory;
        private readonly ILogger<MiCorreoService> _logger;

        private static string? _tokenCacheado;
        private static DateTime _tokenExpira = DateTime.MinValue;
        private static string? _customerIdCacheado;
        private static readonly SemaphoreSlim _lock = new(1, 1);

        public MiCorreoService(IConfiguration config, IHttpClientFactory httpFactory, ILogger<MiCorreoService> logger)
        {
            _config = config;
            _httpFactory = httpFactory;
            _logger = logger;
        }

        private string BaseUrl => (_config["MiCorreo:BaseUrl"] ?? "https://apitest.correoargentino.com.ar/micorreo/v1").TrimEnd('/');
        private string? Usuario => _config["MiCorreo:Usuario"];
        private string? Password => _config["MiCorreo:Password"];
        public string? OrigenCP => _config["MiCorreo:OrigenCP"];

        public bool CredencialesCargadas =>
            !string.IsNullOrWhiteSpace(Usuario) && !string.IsNullOrWhiteSpace(Password)
            && !string.IsNullOrWhiteSpace(OrigenCP)
            && (!string.IsNullOrWhiteSpace(_config["MiCorreo:CustomerId"])
                || (!string.IsNullOrWhiteSpace(_config["MiCorreo:CuentaEmail"]) && !string.IsNullOrWhiteSpace(_config["MiCorreo:CuentaPassword"])));

        // ── Token (POST /token con Basic Auth) ───────────────────────────
        private async Task<(bool ok, string tokenOMensaje)> ObtenerTokenAsync(bool forzar = false)
        {
            if (string.IsNullOrWhiteSpace(Usuario) || string.IsNullOrWhiteSpace(Password))
                return (false, "Todavía no cargamos las credenciales de MiCorreo (MiCorreo:Usuario / MiCorreo:Password).");

            await _lock.WaitAsync();
            try
            {
                if (!forzar && _tokenCacheado != null && DateTime.UtcNow < _tokenExpira)
                    return (true, _tokenCacheado);

                var client = _httpFactory.CreateClient();
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Usuario}:{Password}"));
                var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/token");
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

                var resp = await client.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return (false, $"MiCorreo rechazó el login ({(int)resp.StatusCode}): {body}");

                using var doc = JsonDocument.Parse(body);
                var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(token))
                    return (false, $"El login de MiCorreo respondió OK pero sin token: {body}");

                _tokenCacheado = token;
                _tokenExpira = DateTime.UtcNow.AddMinutes(100); // el token dura ~2h30
                return (true, token);
            }
            catch (Exception ex)
            {
                return (false, $"No pudimos conectar con MiCorreo para autenticar: {ex.Message}");
            }
            finally
            {
                _lock.Release();
            }
        }

        // Manda un request con el Bearer; si responde 401 renueva el token y reintenta una vez.
        private async Task<(bool ok, int status, string body)> EnviarAsync(HttpMethod metodo, string ruta, object? payload = null)
        {
            for (var intento = 0; intento < 2; intento++)
            {
                var (okToken, token) = await ObtenerTokenAsync(forzar: intento == 1);
                if (!okToken) return (false, 0, token);

                try
                {
                    var req = new HttpRequestMessage(metodo, $"{BaseUrl}{ruta}");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    if (payload != null)
                        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                    var resp = await _httpFactory.CreateClient().SendAsync(req);
                    var body = await resp.Content.ReadAsStringAsync();

                    if ((int)resp.StatusCode == 401 && intento == 0) continue;
                    return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
                }
                catch (Exception ex)
                {
                    return (false, 0, $"No pudimos conectar con MiCorreo: {ex.Message}");
                }
            }
            return (false, 401, "MiCorreo rechazó el token.");
        }

        private static string MensajeDeError(int status, string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString()!;
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString()!;
            }
            catch { /* body no JSON */ }
            return status == 0 ? body : $"({status}) {body}";
        }

        // ── customerId de Vale ───────────────────────────────────────────
        private async Task<(bool ok, string idOMensaje)> ObtenerCustomerIdAsync()
        {
            var fijo = _config["MiCorreo:CustomerId"];
            if (!string.IsNullOrWhiteSpace(fijo)) return (true, fijo);
            if (_customerIdCacheado != null) return (true, _customerIdCacheado);

            var (ok, status, body) = await EnviarAsync(HttpMethod.Post, "/users/validate", new
            {
                email = _config["MiCorreo:CuentaEmail"],
                password = _config["MiCorreo:CuentaPassword"],
            });
            if (!ok) return (false, $"No pudimos obtener el customerId de MiCorreo: {MensajeDeError(status, body)}");

            try
            {
                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.GetProperty("customerId").GetString();
                if (string.IsNullOrWhiteSpace(id)) return (false, "MiCorreo no devolvió customerId.");
                _customerIdCacheado = id;
                return (true, id);
            }
            catch (Exception ex)
            {
                return (false, $"No pudimos leer el customerId de MiCorreo: {ex.Message}");
            }
        }

        // ── 1) Sucursales de una provincia (GET /agencies) ───────────────
        public async Task<(bool ok, string mensaje, List<SucursalDto> sucursales)> ObtenerSucursalesAsync(string provinceCode)
        {
            var vacio = new List<SucursalDto>();
            var (okId, id) = await ObtenerCustomerIdAsync();
            if (!okId) return (false, id, vacio);

            var ruta = $"/agencies?customerId={Uri.EscapeDataString(id)}&provinceCode={Uri.EscapeDataString(provinceCode)}&services=pickup_availability";
            var (ok, status, body) = await EnviarAsync(HttpMethod.Get, ruta);
            if (!ok) return (false, MensajeDeError(status, body), vacio);

            try
            {
                var lista = new List<SucursalDto>();
                using var doc = JsonDocument.Parse(body);
                foreach (var a in doc.RootElement.EnumerateArray())
                {
                    if (a.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() != "ACTIVE") continue;

                    var addr = a.GetProperty("location").GetProperty("address");
                    string Txt(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var calle = $"{Txt(addr, "streetName")} {Txt(addr, "streetNumber")}".Trim();
                    var localidad = Txt(addr, "locality");

                    lista.Add(new SucursalDto(
                        Txt(a, "code"),
                        Txt(a, "name"),
                        calle,
                        localidad,
                        Txt(addr, "postalCode"),
                        Txt(addr, "provinceCode")));
                }
                return (true, "OK", lista.OrderBy(s => s.Localidad).ThenBy(s => s.Nombre).ToList());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No pudimos leer la lista de sucursales de MiCorreo: {Body}", body);
                return (false, "MiCorreo devolvió las sucursales en un formato inesperado.", vacio);
            }
        }

        // ── 2) Cotizar (POST /rates) ─────────────────────────────────────
        // deliveredType: "D" domicilio | "S" sucursal. Devuelve la tarifa Clásico (CP).
        public async Task<(bool ok, string mensaje, CotizacionDto? cotizacion)> CotizarAsync(string cpDestino, string deliveredType, Bulto bulto)
        {
            var (okId, id) = await ObtenerCustomerIdAsync();
            if (!okId) return (false, id, null);

            var (ok, status, body) = await EnviarAsync(HttpMethod.Post, "/rates", new
            {
                customerId = id,
                postalCodeOrigin = OrigenCP,
                postalCodeDestination = LimpiarCP(cpDestino),
                deliveredType,
                dimensions = new { weight = bulto.Gramos, height = bulto.AltoCm, width = bulto.AnchoCm, length = bulto.LargoCm },
            });
            if (!ok) return (false, MensajeDeError(status, body), null);

            try
            {
                using var doc = JsonDocument.Parse(body);
                CotizacionDto? mejor = null;
                foreach (var r in doc.RootElement.GetProperty("rates").EnumerateArray())
                {
                    var tipo = r.TryGetProperty("deliveredType", out var dt) ? dt.GetString() : null;
                    var prod = r.TryGetProperty("productType", out var pt) ? pt.GetString() : null;
                    if (tipo != deliveredType || prod != "CP") continue; // solo Clásico

                    mejor = new CotizacionDto(
                        r.GetProperty("price").GetDecimal(),
                        r.TryGetProperty("deliveryTimeMin", out var mn) ? mn.GetString() : null,
                        r.TryGetProperty("deliveryTimeMax", out var mx) ? mx.GetString() : null,
                        r.TryGetProperty("productName", out var pn) ? pn.GetString() ?? "Correo Argentino Clásico" : "Correo Argentino Clásico");
                    break;
                }
                return mejor == null
                    ? (false, "MiCorreo no devolvió una tarifa Clásico para ese destino.", null)
                    : (true, "OK", mejor);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No pudimos leer la cotización de MiCorreo: {Body}", body);
                return (false, "MiCorreo devolvió la cotización en un formato inesperado.", null);
            }
        }

        // ── 3) Importar el envío (POST /shipping/import) ─────────────────
        public async Task<(bool ok, string mensaje)> ImportarEnvioAsync(Orden orden, Bulto bulto, decimal valorDeclarado)
        {
            var (okId, id) = await ObtenerCustomerIdAsync();
            if (!okId) return (false, id);

            var esSucursal = orden.EnvioTipoEntrega == "sucursal";
            if (esSucursal && string.IsNullOrWhiteSpace(orden.EnvioSucursalCodigo))
                return (false, "La orden es a sucursal pero no tiene código de sucursal.");

            var (calle, numero) = SepararCalle(orden.EnvioCalle);
            var telefono = orden.EnvioWhatsapp ?? orden.EnvioTelefono ?? "";

            var payload = new
            {
                customerId = id,
                extOrderId = orden.Id.ToString(),
                orderNumber = orden.Id.ToString(),
                // Remitente en null → MiCorreo usa los datos de la cuenta de Vale.
                sender = new
                {
                    name = (string?)null, phone = (string?)null, cellPhone = (string?)null, email = (string?)null,
                    originAddress = new
                    {
                        streetName = (string?)null, streetNumber = (string?)null, floor = (string?)null,
                        apartment = (string?)null, city = (string?)null, provinceCode = (string?)null, postalCode = (string?)null,
                    },
                },
                recipient = new
                {
                    name = orden.EnvioNombre ?? "",
                    phone = telefono,
                    cellPhone = telefono,
                    email = orden.EnvioEmail ?? orden.Usuario?.Email ?? "",
                },
                shipping = new
                {
                    deliveryType = esSucursal ? "S" : "D",
                    agency = esSucursal ? orden.EnvioSucursalCodigo : null,
                    address = new
                    {
                        streetName = esSucursal ? null : calle,
                        streetNumber = esSucursal ? null : numero,
                        floor = "",
                        apartment = "",
                        city = orden.EnvioCiudad ?? orden.EnvioLocalidad,
                        provinceCode = orden.EnvioProvinciaCodigo,
                        postalCode = LimpiarCP(orden.EnvioCP),
                    },
                    productType = "CP",
                    weight = bulto.Gramos,
                    declaredValue = valorDeclarado,
                    height = bulto.AltoCm,
                    length = bulto.LargoCm,
                    width = bulto.AnchoCm,
                },
            };

            var (ok, status, respBody) = await EnviarAsync(HttpMethod.Post, "/shipping/import", payload);
            if (!ok)
            {
                var msj = MensajeDeError(status, respBody);
                // Si ya estaba importada lo damos por bueno (es idempotente por extOrderId).
                if (msj.Contains("ya fue importada", StringComparison.OrdinalIgnoreCase)) return (true, "Ya estaba importada en MiCorreo.");
                return (false, msj);
            }
            return (true, "OK");
        }

        // ── 4) Seguimiento (GET /shipping/tracking) ──────────────────────
        public async Task<(bool ok, string mensaje, string? ultimoEstado)> ConsultarSeguimientoAsync(string shippingId)
        {
            var (ok, status, body) = await EnviarAsync(HttpMethod.Get, $"/shipping/tracking?shippingId={Uri.EscapeDataString(shippingId)}");
            if (!ok) return (false, MensajeDeError(status, body), null);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var raiz = doc.RootElement;
                if (raiz.ValueKind == JsonValueKind.Object && raiz.TryGetProperty("error", out var err))
                    return (false, err.GetString() ?? "MiCorreo no encontró ese envío.", null);

                var primero = raiz.ValueKind == JsonValueKind.Array && raiz.GetArrayLength() > 0 ? raiz[0] : raiz;
                if (primero.TryGetProperty("events", out var ev) && ev.ValueKind == JsonValueKind.Array && ev.GetArrayLength() > 0)
                {
                    var e = ev[0]; // el primero es el más reciente
                    var nombre = e.TryGetProperty("event", out var n) ? n.GetString() : null;
                    var fecha = e.TryGetProperty("date", out var d) ? d.GetString() : null;
                    return (true, "OK", $"{nombre} ({fecha})");
                }
                return (true, "OK", "Sin movimientos todavía");
            }
            catch (Exception ex)
            {
                return (false, $"No pudimos leer el seguimiento de MiCorreo: {ex.Message}", null);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────
        // Deja solo los dígitos del CP (MiCorreo acepta "1704" y también "B1842ZAB").
        private static string LimpiarCP(string? cp)
        {
            var s = (cp ?? "").Trim().ToUpperInvariant();
            var m = Regex.Match(s, @"^[A-Z]?(\d{4})[A-Z]{0,3}$");
            return m.Success ? m.Groups[1].Value : s;
        }

        // "Av. Siempre Viva 742" → ("Av. Siempre Viva", "742"); sin número → ("...", "S/N").
        private static (string calle, string numero) SepararCalle(string? calleYNumero)
        {
            var s = (calleYNumero ?? "").Trim();
            var m = Regex.Match(s, @"^(.*?)[\s,]+(\d+\w*)\b");
            return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value) : (s, "S/N");
        }

        // Junta todos los productos físicos de una orden/carrito en un solo bulto:
        // suma los pesos, apila las alturas y toma el mayor ancho/largo.
        // Devuelve null si algún producto físico no tiene peso/medidas cargados.
        public static (Bulto? bulto, string? faltantes) ArmarBulto(IEnumerable<(Producto producto, int cantidad)> items)
        {
            int gramos = 0, alto = 0, ancho = 0, largo = 0;
            var faltantes = new List<string>();
            var hayFisico = false;

            foreach (var (p, cant) in items)
            {
                if (p.Tipo != "producto") continue;
                hayFisico = true;
                if (p.PesoGramos == null || p.AltoCm == null || p.AnchoCm == null || p.LargoCm == null)
                {
                    faltantes.Add(p.Nombre);
                    continue;
                }
                gramos += p.PesoGramos.Value * cant;
                alto += p.AltoCm.Value * cant;
                ancho = Math.Max(ancho, p.AnchoCm.Value);
                largo = Math.Max(largo, p.LargoCm.Value);
            }

            if (!hayFisico) return (null, null);
            if (faltantes.Count > 0) return (null, string.Join(", ", faltantes.Distinct()));
            return (new Bulto(Math.Max(gramos, 1), Math.Max(alto, 1), Math.Max(ancho, 1), Math.Max(largo, 1)), null);
        }
    }

    public record Bulto(int Gramos, int AltoCm, int AnchoCm, int LargoCm);
    public record SucursalDto(string Codigo, string Nombre, string Direccion, string Localidad, string CP, string ProvinciaCodigo);
    public record CotizacionDto(decimal Precio, string? DiasMin, string? DiasMax, string Producto);
}
