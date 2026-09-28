using System;
using System.Collections.Generic;

namespace S3FileConverter.Models
{
    /// <summary>
    /// Report for video thumbnail generation operations.
    /// </summary>
    public class ThumbnailGenerationReport
    {
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string BucketName { get; set; }
        public string PrefixFilter { get; set; }
        public bool DryRun { get; set; }

        // Operation mode
        public string Mode { get; set; } // "general" or "individual"
        public string SingleVideoKey { get; set; } // Only for individual mode

        // Counts
        public int TotalVideosProcessed { get; set; }
        public int ThumbnailsGenerated { get; set; }
        public int ThumbnailsAlreadyExist { get; set; }
        public int ThumbnailsFailed { get; set; }

        // Details
        public List<ThumbnailOperationEntry> Operations { get; set; } = new List<ThumbnailOperationEntry>();

        // Timing
        public TimeSpan TotalDuration { get; set; }

        public string Summary
        {
            get
            {
                string modeText = DryRun ? "[DRY RUN] " : "";
                return $"{modeText}Processed {TotalVideosProcessed} videos: " +
                       $"{ThumbnailsGenerated} generated, " +
                       $"{ThumbnailsAlreadyExist} already exist, " +
                       $"{ThumbnailsFailed} failed. " +
                       $"Duration: {TotalDuration.TotalSeconds:F1}s";
            }
        }
    }

    /// <summary>
    /// Entry for each thumbnail operation.
    /// </summary>
    public class ThumbnailOperationEntry
    {
        public string VideoKey { get; set; }
        public string ThumbnailKey { get; set; }
        public string Status { get; set; } // "generated", "exists", "failed", "skipped"
        public string ErrorMessage { get; set; }
        public long VideoSizeBytes { get; set; }
        public double? VideoDurationSeconds { get; set; }
        public double? ProcessingTimeMs { get; set; }

        public string VideoSizeFormatted
        {
            get
            {
                string[] sizes = { "B", "KB", "MB", "GB", "TB" };
                int order = 0;
                double size = VideoSizeBytes;
                while (size >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    size /= 1024;
                }
                return $"{size:0.##} {sizes[order]}";
            }
        }
    }
}
