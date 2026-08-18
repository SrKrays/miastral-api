using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using miastral_api.Services;

namespace miastral_api.Controllers
{
    // Maneja el "contenido editable" del sitio: fotos, links de YouTube y textos
    // cortos que hoy están hardcodeados en el frontend (Sobre mí, Diseño Humano,
    // Bienestar, Material Gratuito, el video de bienvenida del Home, etc.) y que
    // Vale va a poder reemplazar sola desde el panel admin, sin depender de que
    // alguien le toque código.
    //
    // No usa la base de datos — guarda un solo archivo contenido.json en el
    // hosting de Ferozo (mismo FTP que ya se usa para las fotos de producto)
    // con el mapa { clave: valor }. El valor puede ser una URL de imagen, un
    // link de YouTube o un texto corto (título/descripción de una card). El
    // frontend lo lee público, sin login, y si una clave no está, usa su
    // valor por defecto (el que ya tenía hardcodeado).
    [ApiController]
    [Route("api/[controller]")]
    public class ContenidoController : ControllerBase
    {
        private readonly FerozoUploadService _uploadService;

        private const string RutaJson = "public_html/contenido.json";

        private static readonly string[] ExtensionesImagen = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        private const long TamañoMaximoImagen = 8 * 1024 * 1024;   // 8 MB

        public ContenidoController(FerozoUploadService uploadService)
        {
            _uploadService = uploadService;
        }

        // GET api/contenido — público, sin login. Devuelve el mapa completo
        // { clave: valor }. Si todavía no se subió/guardó nada, devuelve {}
        // vacío y el frontend usa sus valores por defecto.
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> Obtener()
        {
            var json = await _uploadService.DescargarTextoAsync(RutaJson);
            if (string.IsNullOrWhiteSpace(json)) return Ok(new Dictionary<string, string>());
            return Content(json, "application/json");
        }

        // PUT api/contenido/{clave} — solo admin. Sube una imagen a Ferozo y
        // actualiza esa clave en contenido.json con la URL resultante.
        // clave = identificador libre que usa el frontend (ej: "sobreMiFoto",
        // "disenoHumanoImagen", "bienestar1Imagen", "materialGuia2Imagen"...).
        [HttpPut("{clave}")]
        [Authorize(Roles = "admin")]
        [RequestSizeLimit(TamañoMaximoImagen)]
        public async Task<IActionResult> ActualizarImagen(string clave, IFormFile? archivo)
        {
            if (string.IsNullOrWhiteSpace(clave))
                return BadRequest(new { message = "Falta la clave del contenido a actualizar." });

            if (archivo == null || archivo.Length == 0)
                return BadRequest(new { message = "No se recibió ningún archivo." });

            var ext = Path.GetExtension(archivo.FileName).ToLowerInvariant();
            if (!ExtensionesImagen.Contains(ext))
                return BadRequest(new { message = "Formato no soportado. Usá JPG, PNG, WEBP o GIF." });

            if (archivo.Length > TamañoMaximoImagen)
                return BadRequest(new { message = "La imagen no puede pesar más de 8 MB." });

            var (ok, resultado) = await _uploadService.SubirAsync(archivo);
            if (!ok) return StatusCode(502, new { message = resultado });

            var guardado = await GuardarClaveAsync(clave, resultado);
            if (!guardado)
                return StatusCode(502, new { message = "La imagen se subió, pero no pudimos actualizar el índice de contenido. Probá de nuevo." });

            return Ok(new { clave, url = resultado, tipo = "imagen" });
        }

        public class ActualizarTextoRequest
        {
            public string? Valor { get; set; }
        }

        // PUT api/contenido/{clave}/texto — solo admin. Guarda un texto corto
        // (título, descripción, link de YouTube, etc.) sin pasar por Ferozo.
        // Si el valor viene vacío, se borra la clave y esa card vuelve a
        // mostrar su valor por defecto.
        [HttpPut("{clave}/texto")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> ActualizarTexto(string clave, [FromBody] ActualizarTextoRequest? body)
        {
            if (string.IsNullOrWhiteSpace(clave))
                return BadRequest(new { message = "Falta la clave del contenido a actualizar." });

            var valor = body?.Valor?.Trim() ?? "";

            bool guardado;
            if (string.IsNullOrEmpty(valor))
                guardado = await BorrarClaveAsync(clave);
            else
                guardado = await GuardarClaveAsync(clave, valor);

            if (!guardado)
                return StatusCode(502, new { message = "No pudimos guardar el cambio. Probá de nuevo." });

            return Ok(new { clave, valor });
        }

        // DELETE api/contenido/{clave} — solo admin. Saca la clave del índice
        // para que esa card vuelva a mostrar su valor por defecto del sitio.
        // No borra el archivo ya subido a Ferozo (no genera ningún problema,
        // solo queda un archivo huérfano).
        [HttpDelete("{clave}")]
        [Authorize(Roles = "admin")]
        public async Task<IActionResult> Borrar(string clave)
        {
            if (string.IsNullOrWhiteSpace(clave))
                return BadRequest(new { message = "Falta la clave del contenido a borrar." });

            var guardado = await BorrarClaveAsync(clave);
            if (!guardado)
                return StatusCode(502, new { message = "No pudimos guardar el cambio. Probá de nuevo." });

            return Ok(new { clave });
        }

        // ── Helpers privados: leer/escribir el mapa completo en contenido.json ──

        private async Task<Dictionary<string, string>> CargarMapaAsync()
        {
            var jsonActual = await _uploadService.DescargarTextoAsync(RutaJson);
            try
            {
                return string.IsNullOrWhiteSpace(jsonActual)
                    ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(jsonActual) ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private async Task<bool> GuardarClaveAsync(string clave, string valor)
        {
            var mapa = await CargarMapaAsync();
            mapa[clave] = valor;
            var nuevoJson = JsonSerializer.Serialize(mapa);
            return await _uploadService.SubirTextoAsync(RutaJson, nuevoJson);
        }

        private async Task<bool> BorrarClaveAsync(string clave)
        {
            var mapa = await CargarMapaAsync();
            mapa.Remove(clave);
            var nuevoJson = JsonSerializer.Serialize(mapa);
            return await _uploadService.SubirTextoAsync(RutaJson, nuevoJson);
        }
    }
}
