using System;

namespace S3FileConverter.Models
{
    public enum ConversionStatus
    {
        Success,
        Skipped,               // Already converted (output exists with valid migration metadata, source gone)
        ManualReviewRequired,  // Ambiguous state: output exists but cannot prove relationship to source
        Failed,
        DryRun                 // Would convert but --dry-run was set
    }

    public class ConversionResult
    {
        public string OriginalKey { get; set; }
        public string OutputKey { get; set; }
        public string ConverterId { get; set; }
        public ConversionStatus Status { get; set; }
        public string ErrorMessage { get; set; }
        public string ReviewReason { get; set; }  // Explanation for ManualReviewRequired cases
        public long OriginalSizeBytes { get; set; }
        public long ConvertedSizeBytes { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
