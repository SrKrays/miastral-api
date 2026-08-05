using FluentFTP;

namespace miastral_api.Services
{
    // Sube archivos (imágenes de productos) al hosting de Ferozo/DonWeb por FTP
    // y devuelve la URL pública para guardar en el campo imageUrl del producto.
    //
    // Credenciales: NUNCA hardcodeadas acá. Vienen de configuración —
    // appsettings.Development.json (gitignored) en local, o de las env vars
    // Ferozo__Host / Ferozo__Username / Ferozo__Password en Render en producción.
    public class FerozoUploadService
    {
        private readonly IConfiguration _config;

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
            var ext = Path.GetExtension(archivo.FileName);
            var nombreArchivo = $"{Guid.NewGuid():N}{ext}";
            var rutaRemota = $"{remoteDir.TrimEnd('/')}/{nombreArchivo}";

            using var client = new AsyncFtpClient(host, user, pass);
            try
            {
                await client.AutoConnect();
                await using var stream = archivo.OpenReadStream();
                var status = await client.UploadStream(stream, rutaRemota, FtpRemoteExists.Overwrite, createRemoteDir: true);

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
    }
}
