using System;

namespace S3FileConverter.Models
{
    /// <summary>
    /// Metadata of a source file found in S3 that needs conversion.
    /// Generic replacement for HeicFileInfo.
    /// </summary>
    public class SourceFileInfo
    {
        public string Key { get; set; }
        public long SizeBytes { get; set; }
        public DateTime LastModified { get; set; }

        /// <summary>
        /// How the file was detected: "extension" or "magic-bytes"
        /// </summary>
        public string DetectionMethod { get; set; }

        /// <summary>
        /// The file extension (lowercase, without dot). Example: "heic"
        /// </summary>
        public string Extension { get; set; }

        /// <summary>
        /// The target extension after conversion (set by the converter).
        /// </summary>
        public string TargetExtension { get; set; }

        /// <summary>
        /// The expected output key after conversion.
        /// Computed from Key and TargetExtension.
        /// </summary>
        public string ExpectedOutputKey => ComputeOutputKey();

        private string ComputeOutputKey()
        {
            if (string.IsNullOrEmpty(TargetExtension))
                throw new InvalidOperationException("TargetExtension must be set before accessing ExpectedOutputKey");

            int lastDot = Key.LastIndexOf('.');
            string baseName = lastDot > 0 ? Key.Substring(0, lastDot) : Key;
            return baseName + "." + TargetExtension;
        }

        /// <summary>
        /// Extract extension from a key (lowercase, without dot).
        /// </summary>
        public static string GetExtension(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            int lastDot = key.LastIndexOf('.');
            if (lastDot < 0 || lastDot == key.Length - 1)
                return null;

            return key.Substring(lastDot + 1).ToLowerInvariant();
        }
    }
}
