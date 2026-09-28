using System;
using System.Collections.Generic;

namespace S3FileConverter.Models
{
    /// <summary>
    /// Report for video thumbnail audit - identifies videos missing their thumbnails.
    /// </summary>
    public class ThumbnailAuditReport
    {
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string BucketName { get; set; }
        public string PrefixFilter { get; set; }

        // Scan metrics
        public int TotalObjectsScanned { get; set; }
        public int PagesScanned { get; set; }
        public bool ScanCompleted { get; set; }
        public string ScanError { get; set; }

        // Video counts
        public int TotalVideosFound { get; set; }
        public int VideosWithThumbnail { get; set; }
        public int VideosMissingThumbnail { get; set; }

        // Details for videos missing thumbnails
        public List<MissingThumbnailEntry> MissingThumbnails { get; set; } = new List<MissingThumbnailEntry>();

        // Detection criteria documentation
        public string DetectionCriteria { get; set; }

        // Quick summary
        public string Summary
        {
            get
            {
                if (!string.IsNullOrEmpty(ScanError))
                    return $"SCAN INCOMPLETE: {ScanError}";

                if (!ScanCompleted)
                    return $"SCAN INCOMPLETE: Stopped before completion. Found {TotalVideosFound} videos, {VideosMissingThumbnail} missing thumbnails.";

                return $"Scanned {TotalObjectsScanned:N0} objects across {PagesScanned} pages. " +
                       $"Found {TotalVideosFound} videos: {VideosWithThumbnail} with thumbnail, {VideosMissingThumbnail} missing thumbnail.";
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

    /// <summary>
    /// Entry for a video that is missing its thumbnail.
    /// </summary>
    public class MissingThumbnailEntry
    {
        public string VideoKey { get; set; }
        public string ExpectedThumbnailKey { get; set; }
        public long VideoSizeBytes { get; set; }
        public DateTime VideoLastModified { get; set; }

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
