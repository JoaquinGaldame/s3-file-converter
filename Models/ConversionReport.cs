using System;
using System.Collections.Generic;

namespace S3FileConverter.Models
{
    public class ConversionReport
    {
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public TimeSpan Duration => CompletedAt - StartedAt;

        public string BucketName { get; set; }
        public string PrefixFilter { get; set; }
        public int? LimitApplied { get; set; }
        public bool DryRun { get; set; }

        /// <summary>
        /// Source extensions that were converted.
        /// </summary>
        public List<string> SourceExtensions { get; set; } = new List<string>();

        /// <summary>
        /// Target extension for output.
        /// </summary>
        public string TargetExtension { get; set; }

        /// <summary>
        /// Converter ID used.
        /// </summary>
        public string ConverterId { get; set; }

        // Counts
        public int TotalProcessed { get; set; }
        public int Successful { get; set; }
        public int Skipped { get; set; }
        public int ManualReviewRequired { get; set; }
        public int Failed { get; set; }

        // Size savings
        public long TotalOriginalBytes { get; set; }
        public long TotalConvertedBytes { get; set; }
        public long BytesSaved => TotalOriginalBytes - TotalConvertedBytes;

        // Details
        public List<ConversionResult> Results { get; set; } = new List<ConversionResult>();
        public List<string> FailedKeys { get; set; } = new List<string>();
        public List<string> ManualReviewKeys { get; set; } = new List<string>();
    }
}
