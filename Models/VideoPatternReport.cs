using System;
using System.Collections.Generic;
using System.Linq;

namespace S3FileConverter.Models
{
    /// <summary>
    /// Report for video detection by key pattern (e.g., "/video" in key).
    /// Used for diagnostics when extension-based detection misses files.
    /// </summary>
    public class VideoPatternReport
    {
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string BucketName { get; set; }
        public string PrefixFilter { get; set; }
        public string PatternUsed { get; set; }

        // Core counts
        public int TotalObjectsScanned { get; set; }
        public int TotalVideosDetected { get; set; }
        public int ThumbnailsExcluded { get; set; }

        // Size
        public long TotalSizeBytes { get; set; }
        public string TotalSizeFormatted => FormatBytes(TotalSizeBytes);

        // Pagination breakdown
        public int PagesScanned { get; set; }
        public List<int> VideosPerPage { get; set; } = new List<int>();
        public bool ScanCompleted { get; set; }

        // Extension distribution (key = extension or "(none)", value = count)
        public Dictionary<string, int> ExtensionDistribution { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Sample keys (first 20 for verification)
        public List<string> SampleKeys { get; set; } = new List<string>();

        // Detection criteria documentation
        public string DetectionCriteria { get; set; }

        // Summary for quick diagnosis
        public string DiagnosisSummary
        {
            get
            {
                var lines = new List<string>
                {
                    $"Scanned {TotalObjectsScanned:N0} objects across {PagesScanned} pages.",
                    $"Detected {TotalVideosDetected} videos by pattern '{PatternUsed}'.",
                    $"Excluded {ThumbnailsExcluded} thumbnails (thumb_video*)."
                };

                if (ExtensionDistribution.Count > 0)
                {
                    lines.Add("Extension breakdown:");
                    foreach (var kvp in ExtensionDistribution.OrderByDescending(x => x.Value))
                    {
                        lines.Add($"  - {kvp.Key}: {kvp.Value}");
                    }
                }

                return string.Join("\n", lines);
            }
        }

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
