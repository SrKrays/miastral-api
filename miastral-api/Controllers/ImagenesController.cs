using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using miastral_api.Services;

namespace miastral_api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ImagenesController : ControllerBase
    {
        private readonly FerozoUploadService _uploadService;
        private static readonly string[] ExtensionesPermitidas = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
        private const long TamañoMaximoBytes = 8 * 1024 * 1024; // 8 MB

        public ImagenesController(FerozoUploadService uploadService)
        {
            _uploadService = uploadService;
        }

        // POST api/imagenes — sube un archivo al hosting de Ferozo y devuelve su URL
        // pública, para pegar directo en el campo "URL de la imagen" del producto.
        [HttpPost]
        [Authorize(Roles = "admin")]
        [RequestSizeLimit(TamañoMaximoBytes)]
        public async Task<IActionResult> Subir(IFormFile? archivo)
        {
            if (archivo == null || archivo.Length == 0)
                return BadRequest(new { message = "No se recibió ningún archivo." });

            if (archivo.Length > TamañoMaximoBytes)
                return BadRequest(new { message = "La imagen no puede pesar más de 8 MB." });

            var ext = Path.GetExtension(archivo.FileName).ToLowerInvariant();
            if (!ExtensionesPermitidas.Contains(ext))
                return BadRequest(new { message = "Formato no soportado. Usá JPG, PNG, WEBP o GIF." });

            if (!await ValidacionArchivos.EsImagenValida(archivo))
                return BadRequest(new { message = "El archivo no es una imagen válida." });

            var (ok, resultado) = await _uploadService.SubirAsync(archivo);
            if (!ok)
                return StatusCode(502, new { message = resultado });

            return Ok(new { url = resultado });
        }
    }
}
