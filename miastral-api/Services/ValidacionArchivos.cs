namespace miastral_api.Services
{
    // Confirma que un archivo subido sea realmente una imagen del tipo que dice
    // ser, mirando sus primeros bytes en vez de confiar solo en la extensión
    // del nombre.
    public static class ValidacionArchivos
    {
        public static async Task<bool> EsImagenValida(IFormFile archivo)
        {
            var buffer = new byte[12];
            await using var stream = archivo.OpenReadStream();
            var leidos = await stream.ReadAsync(buffer, 0, buffer.Length);

            if (leidos < 4) return false;

            // JPEG
            if (buffer[0] == 0xFF && buffer[1] == 0xD8 && buffer[2] == 0xFF) return true;
            // PNG
            if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47) return true;
            // GIF
            if (buffer[0] == 0x47 && buffer[1] == 0x49 && buffer[2] == 0x46) return true;
            // WEBP (contenedor RIFF con marca WEBP)
            if (leidos >= 12 && buffer[0] == 0x52 && buffer[1] == 0x49 && buffer[2] == 0x46 && buffer[3] == 0x46
                && buffer[8] == 0x57 && buffer[9] == 0x45 && buffer[10] == 0x42 && buffer[11] == 0x50) return true;

            return false;
        }
    }
}
