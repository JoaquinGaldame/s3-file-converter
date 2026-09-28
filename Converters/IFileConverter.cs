using System.Collections.Generic;
using System.IO;

namespace S3FileConverter.Converters
{
    /// <summary>
    /// Contract for file format converters.
    /// Each converter handles specific input formats and produces a specific output format.
    /// </summary>
    public interface IFileConverter
    {
        /// <summary>
        /// Unique identifier for this converter (e.g., "heic-to-jpeg", "png-to-jpeg").
        /// Used in metadata for traceability.
        /// </summary>
        string ConverterId { get; }

        /// <summary>
        /// File extensions this converter can process (lowercase, without dot).
        /// Example: ["heic", "heif"]
        /// </summary>
        IReadOnlyList<string> SupportedInputExtensions { get; }

        /// <summary>
        /// The output file extension (lowercase, without dot).
        /// Example: "jpg"
        /// </summary>
        string OutputExtension { get; }

        /// <summary>
        /// MIME type of the output format.
        /// Example: "image/jpeg"
        /// </summary>
        string OutputContentType { get; }

        /// <summary>
        /// Check if this converter can handle a given file extension.
        /// </summary>
        bool CanConvert(string extension);

        /// <summary>
        /// Convert the input stream to the output format.
        /// </summary>
        /// <param name="inputStream">Source file stream (must be seekable)</param>
        /// <returns>Converted file stream (caller must dispose)</returns>
        MemoryStream Convert(Stream inputStream);

        /// <summary>
        /// Validate that an output stream contains valid data in the expected format.
        /// </summary>
        /// <param name="outputStream">Stream to validate (will be reset to position 0)</param>
        /// <returns>True if valid, false otherwise</returns>
        bool ValidateOutput(Stream outputStream);
    }
}
