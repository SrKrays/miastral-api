using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using miastral_api.Services;

namespace miastral_api.Controllers
{
    // Maneja el "contenido editable" del sitio: fotos y videos que hoy están
    // hardcodeados en el frontend (Sobre mí, Diseño Humano, el video de
    // bienvenida del Home, las miniaturas de Material Gratuito, etc.) y que
    // Vale va a poder reemplazar sola desde el panel admin, sin depender de
    // que alguien le toque código.
    //
    // No usa la base de datos — guarda un solo archivo contenido.json en el
    // hosting de Ferozo (mismo FTP que ya se usa para las fotos de producto)
    // con el mapa { clave: url }. El frontend lo lee público, sin login.
    [ApiController]
    [Route("api/[controller]")]
    public class ContenidoController : ControllerBase
    {
        private readonly FerozoUploadService _uploadService;

        private const string RutaJson = "public_html/contenido.json";

        private static readonly string[] ExtensionesImagen = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        private static readonly string[] ExtensionesVideo = { ".mp4", ".webm", ".mov" };
        private const long TamañoMaximoImagen = 8 * 1024 * 1024;   // 8 MB
        private const long TamañoMaximoVideo = 150 * 1024 * 1024;  // 150 MB

        public ContenidoController(FerozoUploadService uploadService)
        {
            _uploadService = uploadService;
        }

        // GET api/contenido — público, sin login. Devuelve el mapa completo
        // { clave: url }. Si todavía no se subió nada, devuelve {} vacío y el
        // frontend usa sus valores por defecto (los que ya tenía hardcodeados).
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Obtener()
        {
            var json = await _uploadService.DescargarTextoAsync(RutaJson);
            if (string.IsNullOrWhiteSpace(json)) return Ok(new Dictionary<string, string>());
            return Content(json, "application/json");
        }

        // PUT api/contenido/{clave} — solo admin. Sube el archivo (imagen o
        // video) a Ferozo y actualiza esa clave en contenido.json.
        // clave = identificador libre que usa el frontend (ej: "sobreMiFoto",
        // "disenoHumanoImagen", "homeVideoBienvenida", "materialVideo1"...).
        [HttpPut("{clave}")]
        [Authorize(Roles = "admin")]
        [RequestSizeLimit(TamañoMaximoVideo)]
        public async Task<IActionResult> Actualizar(string clave, IFormFile? archivo)
        {
            if (string.IsNullOrWhiteSpace(clave))
                return BadRequest(new { message = "Falta la clave del contenido a actualizar." });

            if (archivo == null || archivo.Length == 0)
                return BadRequest(new { message = "No se recibió ningún archivo." });

            var ext = Path.GetExtension(archivo.FileName).ToLowerInvariant();
            var esImagen = ExtensionesImagen.Contains(ext);
            var esVideo = ExtensionesVideo.Contains(ext);

            if (!esImagen && !esVideo)
                return BadRequest(new { message = "Formato no soportado. Imágenes: JPG, PNG, WEBP, GIF. Videos: MP4, WEBM, MOV." });

            var maxBytes = esVideo ? TamañoMaximoVideo : TamañoMaximoImagen;
            if (archivo.Length > maxBytes)
                return BadRequest(new { message = esVideo ? "El video no puede pesar más de 150 MB." : "La imagen no puede pesar más de 8 MB." });

            var (ok, resultado) = await _uploadService.SubirAsync(archivo);
            if (!ok) return StatusCode(502, new { message = resultado });

            // Actualizamos el índice contenido.json con la clave nueva.
            var jsonActual = await _uploadService.DescargarTextoAsync(RutaJson);
            Dictionary<string, string> mapa;
            try
            {
                mapa = string.IsNullOrWhiteSpace(jsonActual)
                    ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(jsonActual) ?? new Dictionary<string, string>();
            }
            catch
            {
                mapa = new Dictionary<string, string>();
            }

            mapa[clave] = resultado;
            var nuevoJson = JsonSerializer.Serialize(mapa);
            var guardado = await _uploadService.SubirTextoAsync(RutaJson, nuevoJson);

            if (!guardado)
                return StatusCode(502, new { message = "El archivo se subió, pero no pudimos actualizar el índice de contenido. Probá de nuevo." });

            return Ok(new { clave, url = resultado, tipo = esVideo ? "video" : "imagen" });
        }
    }
}
