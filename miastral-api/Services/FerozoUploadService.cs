using System.Collections.Concurrent;
using FluentFTP;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace miastral_api.Services
{
    // Sube archivos al hosting de Ferozo/DonWeb por FTP y devuelve la URL pública.
    // Las credenciales se leen de configuración (variables de entorno en Render).
    public class FerozoUploadService
    {
        private readonly IConfiguration _config;

        // Última copia buena conocida de cada archivo de texto descargado (ej:
        // contenido.json), para no mostrar la web "vacía" si un reinicio del
        // servidor coincide con un hiccup momentáneo de conexión al FTP.
        private static readonly ConcurrentDictionary<string, string> _ultimaCopiaBuena = new();

        public FerozoUploadService(IConfiguration config)
        {
            _config = config;
        }

        // Devuelve (ok, urlOMensajeDeError)
        public async Task<(bool ok, string resultado)> SubirAsync(IFormFile archivo)
        {
            var host = _config["Ferozo:Host"];
            var user = _config["Ferozo:Username"];
            var pass = _config["Ferozo:Password"];
            var remoteDir = _config["Ferozo:RemoteDir"] ?? "public_html/img/productos";
            var publicBaseUrl = (_config["Ferozo:PublicBaseUrl"] ?? "").TrimEnd('/');

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
                return (false, "El servidor todavía no tiene configuradas las credenciales de Ferozo (Host/Username/Password).");

            // Nombre de archivo único para no pisar imágenes con el mismo nombre.
            var ext = Path.GetExtension(archivo.FileName).ToLowerInvariant();
            var nombreArchivo = $"{Guid.NewGuid():N}{ext}";
            var rutaRemota = $"{remoteDir.TrimEnd('/')}/{nombreArchivo}";

            using var client = new AsyncFtpClient(host, user, pass);
            try
            {
                await client.AutoConnect();
                await using var streamOriginal = archivo.OpenReadStream();
                using var streamProcesado = await ProcesarImagenAsync(streamOriginal, ext);
                var status = await client.UploadStream(streamProcesado, rutaRemota, FtpRemoteExists.Overwrite, createRemoteDir: true);

                if (status != FtpStatus.Success)
                    return (false, "No pudimos subir la imagen al hosting (FTP no confirmó la subida).");

                return (true, $"{publicBaseUrl}/{nombreArchivo}");
            }
            catch (Exception ex)
            {
                return (false, $"Error al conectar/subir por FTP: {ex.Message}");
            }
            finally
            {
                if (client.IsConnected) await client.Disconnect();
            }
        }

        // Achica y reencuadra la imagen antes de subirla, para que fotos pesadas
        // (por ejemplo sacadas de un Word) no rompan el diseño de las cards ni
        // tarden en cargar. Corrige la orientación (fotos de celular rotadas) y
        // limita el lado más largo a 1600px, conservando la proporción original
        // — el recorte final al tamaño exacto de cada card lo hace el CSS
        // (object-fit: cover) del lado del sitio. No se toca el GIF para no
        // perder la animación.
        private static async Task<Stream> ProcesarImagenAsync(Stream original, string extension)
        {
            if (extension == ".gif")
            {
                var copiaSinTocar = new MemoryStream();
                await original.CopyToAsync(copiaSinTocar);
                copiaSinTocar.Position = 0;
                return copiaSinTocar;
            }

            const int ladoMaximo = 1600;

            using var imagen = await Image.LoadAsync(original);

            imagen.Mutate(x => x.AutoOrient());

            if (imagen.Width > ladoMaximo || imagen.Height > ladoMaximo)
            {
                imagen.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(ladoMaximo, ladoMaximo)
                }));
            }

            var salida = new MemoryStream();

            if (extension == ".png")
                await imagen.SaveAsPngAsync(salida);
            else if (extension == ".webp")
                await imagen.SaveAsWebpAsync(salida);
            else
                await imagen.SaveAsJpegAsync(salida, new JpegEncoder { Quality = 82 });

            salida.Position = 0;
            return salida;
        }

        // Descarga un archivo de texto (usado para leer contenido.json, el índice
        // de imágenes/videos "editables" del sitio). Reintenta un par de veces
        // ante fallos de conexión transitorios (típico justo después de que
        // Render reinicia el servicio) y, si aun así falla, devuelve la última
        // copia buena que se haya leído con éxito en este proceso, en vez de
        // vaciar la web. Solo devuelve null si el archivo realmente no existe
        // todavía o si nunca se pudo leer.
        public async Task<string?> DescargarTextoAsync(string rutaRemota)
        {
            var host = _config["Ferozo:Host"];
            var user = _config["Ferozo:Username"];
            var pass = _config["Ferozo:Password"];
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
                return _ultimaCopiaBuena.GetValueOrDefault(rutaRemota);

            const int intentosMaximos = 3;
            for (var intento = 1; intento <= intentosMaximos; intento++)
            {
                using var client = new AsyncFtpClient(host, user, pass);
                try
                {
                    await client.AutoConnect();
                    if (!await client.FileExists(rutaRemota)) return null;

                    using var ms = new MemoryStream();
                    var ok = await client.DownloadStream(ms, rutaRemota);
                    if (!ok) throw new IOException("FTP no confirmó la descarga.");

                    ms.Position = 0;
                    using var reader = new StreamReader(ms);
                    var contenido = await reader.ReadToEndAsync();

                    _ultimaCopiaBuena[rutaRemota] = contenido;
                    return contenido;
                }
                catch when (intento < intentosMaximos)
                {
                    await Task.Delay(400 * intento);
                }
                catch
                {
                    return _ultimaCopiaBuena.GetValueOrDefault(rutaRemota);
                }
                finally
                {
                    if (client.IsConnected) await client.Disconnect();
                }
            }

            return _ultimaCopiaBuena.GetValueOrDefault(rutaRemota);
        }

        // Sube/pisa un archivo de texto plano (contenido.json).
        public async Task<bool> SubirTextoAsync(string rutaRemota, string contenido)
        {
            var host = _config["Ferozo:Host"];
            var user = _config["Ferozo:Username"];
            var pass = _config["Ferozo:Password"];
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
                return false;

            using var client = new AsyncFtpClient(host, user, pass);
            try
            {
                await client.AutoConnect();
                var bytes = System.Text.Encoding.UTF8.GetBytes(contenido);
                using var ms = new MemoryStream(bytes);
                var status = await client.UploadStream(ms, rutaRemota, FtpRemoteExists.Overwrite, createRemoteDir: true);
                if (status != FtpStatus.Success) return false;

                _ultimaCopiaBuena[rutaRemota] = contenido;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (client.IsConnected) await client.Disconnect();
            }
        }
    }
}
