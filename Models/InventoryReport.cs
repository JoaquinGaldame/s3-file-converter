using System;
using System.Collections.Generic;

namespace S3FileConverter.Models
{
    public class InventoryReport
    {
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string BucketName { get; set; }
        public string PrefixFilter { get; set; }
        public int? LimitApplied { get; set; }

        /// <summary>
        /// Extensions that were searched for.
        /// </summary>
        public List<string> TargetExtensions { get; set; } = new List<string>();

        // Counts
        public int TotalFiles { get; set; }
        public int DetectedByExtension { get; set; }
        public int DetectedByMagicBytes { get; set; }

        // Size
        public long TotalSizeBytes { get; set; }
        public string TotalSizeFormatted => FormatBytes(TotalSizeBytes);

        // Pagination info
        public int PagesScanned { get; set; }
        public bool ScanCompleted { get; set; }

        // Sample keys (first 10 for verification)
        public List<string> SampleKeys { get; set; } = new List<string>();

        // Detection criteria documentation
        public string DetectionCriteria =>
            "Files are identified by:\n" +
            "1. Extension: matching target extensions (case-insensitive)\n" +
            "2. Magic bytes: format-specific signatures (where supported)\n\n" +
            $"Target extensions: {string.Join(", ", TargetExtensions)}";

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            int order = 0;
            double size = bytes;
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            return $"{size:0.##} {sizes[order]}";
        }
    }
}
