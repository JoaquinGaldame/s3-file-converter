using System;
using System.Collections.Generic;
using System.IO;
using ImageMagick;
using Serilog;

namespace S3FileConverter.Converters
{
    /// <summary>
    /// Converts HEIC/HEIF images to JPEG using ImageMagick.
    /// Production-tested converter from original HeicBatchConverter.
    /// </summary>
    public class HeicToJpegConverter : IFileConverter
    {
        private const int JpegQuality = 80;

        private static readonly IReadOnlyList<string> _supportedExtensions = new[] { "heic", "heif" };

        public string ConverterId => "heic-to-jpeg";

        public IReadOnlyList<string> SupportedInputExtensions => _supportedExtensions;

        public string OutputExtension => "jpg";

        public string OutputContentType => "image/jpeg";

        /// <summary>
        /// Configure ImageMagick resource limits to prevent memory exhaustion.
        /// Call once at application startup.
        /// </summary>
        public static void ConfigureResourceLimits()
        {
            ResourceLimits.Width = 10000;
            ResourceLimits.Height = 10000;
            ResourceLimits.Memory = 512 * 1024 * 1024; // 512 MB

            Log.Information("ImageMagick resource limits configured: " +
                "MaxWidth={Width}, MaxHeight={Height}, MaxMemory={Memory}MB",
                ResourceLimits.Width, ResourceLimits.Height, ResourceLimits.Memory / (1024 * 1024));
        }

        public bool CanConvert(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return false;

            extension = extension.ToLowerInvariant().TrimStart('.');
            return extension == "heic" || extension == "heif";
        }

        public MemoryStream Convert(Stream inputStream)
        {
            if (inputStream == null)
                throw new ArgumentNullException(nameof(inputStream));

            if (inputStream.CanSeek)
                inputStream.Position = 0;

            using (var image = new MagickImage(inputStream))
            {
                // Fix orientation based on EXIF data
                image.AutoOrient();

                // Strip metadata (privacy + smaller file)
                image.Strip();

                // Set output format
                image.Format = MagickFormat.Jpeg;
                image.Quality = JpegQuality;

                var outputStream = new MemoryStream();
                image.Write(outputStream);
                outputStream.Position = 0;

                Log.Debug("Converted HEIC to JPEG: {Width}x{Height}, {Size} bytes",
                    image.Width, image.Height, outputStream.Length);

                return outputStream;
            }
        }

        public bool ValidateOutput(Stream outputStream)
        {
            if (outputStream == null || outputStream.Length == 0)
            {
                Log.Warning("JPEG validation failed: stream is null or empty");
                return false;
            }

            try
            {
                outputStream.Position = 0;

                using (var image = new MagickImage(outputStream))
                {
                    // Check basic properties
                    if (image.Width <= 0 || image.Height <= 0)
                    {
                        Log.Warning("JPEG validation failed: invalid dimensions {Width}x{Height}",
                            image.Width, image.Height);
                        return false;
                    }

                    // Verify it's actually JPEG
                    if (image.Format != MagickFormat.Jpeg && image.Format != MagickFormat.Jpg)
                    {
                        Log.Warning("JPEG validation failed: format is {Format}, expected JPEG",
                            image.Format);
                        return false;
                    }

                    Log.Debug("JPEG validation passed: {Width}x{Height}, format={Format}",
                        image.Width, image.Height, image.Format);

                    return true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("JPEG validation failed with exception: {Error}", ex.Message);
                return false;
            }
            finally
            {
                // Reset stream for subsequent use
                if (outputStream.CanSeek)
                    outputStream.Position = 0;
            }
        }
    }
}
