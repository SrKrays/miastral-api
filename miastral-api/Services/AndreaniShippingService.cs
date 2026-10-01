using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using miastral_api.Models;

namespace miastral_api.Services
{
    // Integración con la API de Andreani (https://developers.andreani.com/document).
    // Cotiza el envío de una orden y genera el pre-envío + etiqueta cuando Vale
    // marca "Generar etiqueta" en el panel admin.
    //
    // IMPORTANTE — estado de esta implementación:
    // Las URLs de login, cotizador, creación de orden y etiqueta, y el tipo de
    // autenticación (Basic Auth → token en header x-authorization-token) están
    // confirmadas contra la documentación pública de Andreani Developers
    // (developers.andreani.com), leída el 15/09/2026.
    // Los NOMBRES EXACTOS de los campos del JSON de cotización y de creación de
    // orden de envío están en los excels que Andreani deja descargar desde esa
    // misma página (api-cotizador-v2-1.xlsx y api-orden-envio-3.xlsx) — no los
    // pudimos abrir desde acá (son binarios, bloqueados por la sandbox). Los
    // completé con la estructura estándar que usa Andreani en su API v2 de
    // Transporte y Distribución. Cuando lleguen las credenciales, hay que:
    //   1) Correr un pedido de prueba contra QA (ver CotizarAsync/CrearEnvioAsync).
    //   2) Si algún campo no matchea, se ajusta ACÁ (un solo archivo) — nada
    //      más del proyecto depende de la forma exacta de estos JSON.
    public class AndreaniShippingService
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpFactory;

        // Cache simple del token en memoria del proceso (vale 24hs según la doc).
        private static string? _tokenCacheado;
        private static DateTime _tokenExpira = DateTime.MinValue;
        private static readonly SemaphoreSlim _tokenLock = new(1, 1);

        public AndreaniShippingService(IConfiguration config, IHttpClientFactory httpFactory)
        {
            _config = config;
            _httpFactory = httpFactory;
        }

        private string BaseUrl => (_config["Andreani:BaseUrl"] ?? "https://apisqa.andreani.com").TrimEnd('/');
        private string? Usuario => _config["Andreani:Usuario"];
        private string? Password => _config["Andreani:Password"];
        private string? Contrato => _config["Andreani:Contrato"];

        public bool CredencialesCargadas =>
            !string.IsNullOrWhiteSpace(Usuario) && !string.IsNullOrWhiteSpace(Password) && !string.IsNullOrWhiteSpace(Contrato);

        // ── Autenticación ────────────────────────────────────────────────
        // GET {BaseUrl}/login con Basic Auth → devuelve un token que se manda
        // en el header x-authorization-token del resto de los llamados.
        private async Task<(bool ok, string tokenOMensaje)> ObtenerTokenAsync()
        {
            if (!CredencialesCargadas)
                return (false, "Todavía no cargamos las credenciales de Andreani (Usuario/Password/Contrato en appsettings o variables de entorno de Render).");

            await _tokenLock.WaitAsync();
            try
            {
                if (_tokenCacheado != null && DateTime.UtcNow < _tokenExpira)
                    return (true, _tokenCacheado);

                var client = _httpFactory.CreateClient();
                var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Usuario}:{Password}"));

                var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/login");
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

                var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return (false, $"Andreani rechazó el login ({(int)response.StatusCode}): {body}");

                // La doc no especifica si el body es texto plano o JSON con un
                // campo "token" — contemplamos las dos formas.
                var token = ExtraerToken(body);
                if (string.IsNullOrWhiteSpace(token))
                    return (false, $"El login de Andreani respondió OK pero no pudimos leer el token del body: {body}");

                _tokenCacheado = token;
                _tokenExpira = DateTime.UtcNow.AddHours(23); // vale 24hs, dejamos 1hs de margen
                return (true, token);
            }
            catch (Exception ex)
            {
                return (false, $"No pudimos conectar con Andreani para autenticar: {ex.Message}");
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        private static string? ExtraerToken(string body)
        {
            var trimmed = body.Trim().Trim('"');
            if (!trimmed.StartsWith("{")) return trimmed; // texto plano

            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var clave in new[] { "token", "accessToken", "access_token", "authToken" })
                    if (doc.RootElement.TryGetProperty(clave, out var v)) return v.GetString();
            }
            catch { /* cae al fallback de abajo */ }

            return trimmed;
        }

        private async Task<HttpClient> ClienteAutenticadoAsync()
        {
            var (ok, token) = await ObtenerTokenAsync();
            if (!ok) throw new InvalidOperationException(token);

            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.Add("x-authorization-token", token);
            return client;
        }

        // ── Cotizador — GET {BaseUrl}/v1/tarifas ────────────────────────
        public async Task<(bool ok, string mensaje, decimal? costo)> CotizarAsync(string cpDestino, decimal kilos, decimal volumenCm3, decimal valorDeclarado)
        {
            try
            {
                var client = await ClienteAutenticadoAsync();
                var origenCp = _config["Andreani:OrigenCP"] ?? "";

                var query = $"?cpOrigen={Uri.EscapeDataString(origenCp)}&cpDestino={Uri.EscapeDataString(cpDestino)}" +
                            $"&contrato={Uri.EscapeDataString(Contrato ?? "")}" +
                            $"&bultos[0].kilos={kilos.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                            $"&bultos[0].volumen={volumenCm3.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                            $"&bultos[0].valorDeclarado={valorDeclarado.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

                var response = await client.GetAsync($"{BaseUrl}/v1/tarifas{query}");
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return (false, $"Andreani rechazó la cotización ({(int)response.StatusCode}): {body}", null);

                var costo = ExtraerCosto(body);
                if (costo == null)
                    return (false, $"Andreani respondió OK pero no reconocimos el campo del costo. Respuesta cruda: {body}", null);

                return (true, "OK", costo);
            }
            catch (InvalidOperationException ex)
            {
                return (false, ex.Message, null);
            }
            catch (Exception ex)
            {
                return (false, $"No pudimos cotizar con Andreani: {ex.Message}", null);
            }
        }

        private static decimal? ExtraerCosto(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                // Si la respuesta es una lista de tarifas, tomamos la primera.
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    root = root[0];

                foreach (var clave in new[] { "tarifaConIva", "tarifaConIVA", "total", "precioConIva", "precio", "tarifaSinIva" })
                    if (root.TryGetProperty(clave, out var v) && v.ValueKind == JsonValueKind.Number)
                        return v.GetDecimal();
            }
            catch { /* devolvemos null y mostramos el body crudo al admin */ }
            return null;
        }

        // ── Crear orden de envío — POST {BaseUrl}/v2/ordenes-de-envio ───
        public async Task<(bool ok, string mensaje, string? numeroAndreani)> CrearEnvioAsync(Orden orden, decimal kilos, decimal volumenCm3)
        {
            try
            {
                var client = await ClienteAutenticadoAsync();

                var (calleOrigen, numeroOrigen) = (_config["Andreani:OrigenCalle"] ?? "", _config["Andreani:OrigenNumero"] ?? "S/N");
                var (calleDestino, numeroDestino) = SepararCalleYNumero(orden.EnvioCalle ?? "");

                var payload = new
                {
                    contrato = Contrato,
                    origen = new
                    {
                        postal = new
                        {
                            codigoPostal = _config["Andreani:OrigenCP"],
                            calle = calleOrigen,
                            numero = numeroOrigen,
                            localidad = _config["Andreani:OrigenLocalidad"],
                            region = _config["Andreani:OrigenRegion"],
                            pais = "Argentina",
                        }
                    },
                    destino = new
                    {
                        postal = new
                        {
                            codigoPostal = orden.EnvioCP,
                            calle = calleDestino,
                            numero = numeroDestino,
                            localidad = orden.EnvioCiudad,
                            region = orden.EnvioProvincia,
                            pais = "Argentina",
                        }
                    },
                    remitente = new
                    {
                        nombreCompleto = _config["Andreani:RemitenteNombre"],
                        email = _config["Andreani:RemitenteEmail"],
                        telefonos = new[] { new { tipo = "1", numero = _config["Andreani:RemitenteTelefono"] } },
                    },
                    destinatario = new
                    {
                        nombreCompleto = orden.EnvioNombre,
                        email = orden.EnvioEmail,
                        telefonos = new[] { new { tipo = "1", numero = orden.EnvioTelefono } },
                    },
                    bultos = new[]
                    {
                        new
                        {
                            kilos,
                            volumenCm3,
                            valorDeclarado = orden.Total,
                            referenciaExterna = $"miastral-orden-{orden.Id}",
                        }
                    },
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                });
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync($"{BaseUrl}/v2/ordenes-de-envio", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return (false, $"Andreani rechazó la creación del envío ({(int)response.StatusCode}): {body}", null);

                var numero = ExtraerNumeroAndreani(body);
                if (numero == null)
                    return (false, $"Andreani respondió OK pero no reconocimos el número de envío. Respuesta cruda: {body}", null);

                return (true, "OK", numero);
            }
            catch (InvalidOperationException ex)
            {
                return (false, ex.Message, null);
            }
            catch (Exception ex)
            {
                return (false, $"No pudimos crear el envío en Andreani: {ex.Message}", null);
            }
        }

        private static (string calle, string numero) SepararCalleYNumero(string calleCompleta)
        {
            // El checkout de la web pide "Calle y número" en un solo campo libre
            // (ej: "Av. Rivadavia 1234"). Separamos el número final si lo hay;
            // si no, Andreani recibe "S/N".
            var partes = calleCompleta.Trim().Split(' ');
            if (partes.Length > 1 && int.TryParse(partes[^1], out _))
                return (string.Join(' ', partes[..^1]), partes[^1]);
            return (calleCompleta, "S/N");
        }

        private static string? ExtraerNumeroAndreani(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    root = root[0];

                foreach (var clave in new[] { "numeroAndreani", "numero", "numeroDeEnvio", "id" })
                    if (root.TryGetProperty(clave, out var v))
                        return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
            }
            catch { }
            return null;
        }

        // ── Etiqueta — GET {BaseUrl}/v2/ordenes-de-envio/{numero}/etiquetas ─
        // Devuelve directamente el PDF (bytes). El controller lo puede exponer
        // como URL propia (ej: api/ordenes/{id}/etiqueta) que hace este mismo
        // llamado al vuelo, para no tener que subir el PDF a ningún lado.
        public async Task<(bool ok, string mensaje, byte[]? pdf)> ObtenerEtiquetaAsync(string numeroAndreani)
        {
            try
            {
                var client = await ClienteAutenticadoAsync();
                var response = await client.GetAsync($"{BaseUrl}/v2/ordenes-de-envio/{Uri.EscapeDataString(numeroAndreani)}/etiquetas");

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    return (false, $"Andreani rechazó el pedido de etiqueta ({(int)response.StatusCode}): {body}", null);
                }

                var bytes = await response.Content.ReadAsByteArrayAsync();
                return (true, "OK", bytes);
            }
            catch (InvalidOperationException ex)
            {
                return (false, ex.Message, null);
            }
            catch (Exception ex)
            {
                return (false, $"No pudimos obtener la etiqueta de Andreani: {ex.Message}", null);
            }
        }

        // ── Estado del envío — GET {BaseUrl}/v2/ordenes-de-envio/{numero} ──
        public async Task<(bool ok, string mensaje, string? estado)> ConsultarEstadoAsync(string numeroAndreani)
        {
            try
            {
                var client = await ClienteAutenticadoAsync();
                var response = await client.GetAsync($"{BaseUrl}/v2/ordenes-de-envio/{Uri.EscapeDataString(numeroAndreani)}");
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return (false, $"Andreani rechazó la consulta de estado ({(int)response.StatusCode}): {body}", null);

                string? estado = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
                    foreach (var clave in new[] { "estado", "estadoActual", "status" })
                        if (root.TryGetProperty(clave, out var v)) { estado = v.ToString(); break; }
                }
                catch { estado = body; }

                return (true, "OK", estado ?? body);
            }
            catch (InvalidOperationException ex)
            {
                return (false, ex.Message, null);
            }
            catch (Exception ex)
            {
                return (false, $"No pudimos consultar el estado en Andreani: {ex.Message}", null);
            }
        }
    }
}
