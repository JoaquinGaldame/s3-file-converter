using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using S3FileConverter.Converters;
using S3FileConverter.Models;
using Serilog;

namespace S3FileConverter.Services
{
    /// <summary>
    /// Orchestrates the full conversion flow per file with proper state verification
    /// to handle interruptions and ensure no data loss.
    ///
    /// SAFETY RULES:
    /// 1. NEVER delete a source file without proving the output was created by this tool
    /// 2. Use S3 metadata to track migration provenance
    /// 3. Any ambiguous state → ManualReviewRequired, no deletion
    /// </summary>
    public class ConversionOrchestrator
    {
        // S3 metadata keys to identify outputs created by this migration tool (generic, not HEIC-specific)
        private const string MetadataKeyMigrationSource = "x-amz-meta-migration-source";
        private const string MetadataKeyMigrationTimestamp = "x-amz-meta-migration-timestamp";
        private const string MetadataKeyMigrationVersion = "x-amz-meta-migration-version";
        private const string MetadataKeyConverterType = "x-amz-meta-converter-type";
        private const string MigrationVersion = "2.0";

        private readonly IAmazonS3 _s3Client;
        private readonly string _bucketName;
        private readonly S3InventoryService _inventoryService;
        private readonly IFileConverter _converter;

        public ConversionOrchestrator(IAmazonS3 s3Client, string bucketName, IFileConverter converter)
        {
            _s3Client = s3Client ?? throw new ArgumentNullException(nameof(s3Client));
            _bucketName = bucketName ?? throw new ArgumentNullException(nameof(bucketName));
            _converter = converter ?? throw new ArgumentNullException(nameof(converter));
            _inventoryService = new S3InventoryService(s3Client, bucketName);
        }

        /// <summary>
        /// Process a batch of source files.
        /// </summary>
        public async Task<ConversionReport> ProcessBatchAsync(
            List<SourceFileInfo> files,
            bool dryRun,
            CancellationToken cancellationToken = default)
        {
            var report = new ConversionReport
            {
                StartedAt = DateTime.UtcNow,
                BucketName = _bucketName,
                DryRun = dryRun,
                ConverterId = _converter.ConverterId,
                TargetExtension = _converter.OutputExtension
            };

            int current = 0;

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Set target extension from converter
                file.TargetExtension = _converter.OutputExtension;

                current++;
                Log.Information("[{Current}/{Total}] Processing {Key}",
                    current, files.Count, file.Key);

                var result = await ProcessSingleFileAsync(file, dryRun, cancellationToken);
                report.Results.Add(result);

                switch (result.Status)
                {
                    case ConversionStatus.Success:
                        report.Successful++;
                        report.TotalOriginalBytes += result.OriginalSizeBytes;
                        report.TotalConvertedBytes += result.ConvertedSizeBytes;
                        break;
                    case ConversionStatus.Skipped:
                        report.Skipped++;
                        break;
                    case ConversionStatus.ManualReviewRequired:
                        report.ManualReviewRequired++;
                        report.ManualReviewKeys.Add(file.Key);
                        break;
                    case ConversionStatus.Failed:
                        report.Failed++;
                        report.FailedKeys.Add(file.Key);
                        break;
                    case ConversionStatus.DryRun:
                        report.Successful++; // Count as would-be-successful
                        break;
                }

                report.TotalProcessed++;
            }

            report.CompletedAt = DateTime.UtcNow;
            return report;
        }

        /// <summary>
        /// Process a single source file with full state verification.
        /// </summary>
        private async Task<ConversionResult> ProcessSingleFileAsync(
            SourceFileInfo file,
            bool dryRun,
            CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var result = new ConversionResult
            {
                OriginalKey = file.Key,
                OutputKey = file.ExpectedOutputKey,
                ConverterId = _converter.ConverterId,
                OriginalSizeBytes = file.SizeBytes
            };

            try
            {
                // Step 1: Check real state in S3
                bool sourceExists = await _inventoryService.KeyExistsAsync(file.Key, cancellationToken);
                bool outputExists = await _inventoryService.KeyExistsAsync(file.ExpectedOutputKey, cancellationToken);

                Log.Debug("State check: source exists={SourceExists}, output exists={OutputExists}",
                    sourceExists, outputExists);

                // Case 1: Source no longer exists
                if (!sourceExists)
                {
                    if (outputExists)
                    {
                        // Check if the output has our migration metadata
                        var outputMeta = await GetObjectMetadataAsync(file.ExpectedOutputKey, cancellationToken);
                        if (IsMigrationOutput(outputMeta, file.Key))
                        {
                            Log.Information("Already converted (verified by metadata): {Key}", file.Key);
                            result.Status = ConversionStatus.Skipped;
                        }
                        else
                        {
                            // Output exists but we can't prove it came from this source
                            // Since source is gone anyway, just skip (nothing to do)
                            Log.Information("Source gone, output exists but unrelated: {Key}", file.Key);
                            result.Status = ConversionStatus.Skipped;
                        }
                    }
                    else
                    {
                        Log.Warning("Source file no longer exists: {Key}", file.Key);
                        result.Status = ConversionStatus.Skipped;
                    }
                    result.Duration = sw.Elapsed;
                    return result;
                }

                // Case 2: Source exists AND output exists
                // This is the DANGEROUS case - we cannot assume relationship
                if (outputExists)
                {
                    var outputMeta = await GetObjectMetadataAsync(file.ExpectedOutputKey, cancellationToken);

                    if (IsMigrationOutput(outputMeta, file.Key))
                    {
                        // Output was created by this tool for this specific source
                        // This is a resumed/interrupted migration - safe to delete source
                        Log.Information("Resuming interrupted migration (verified by metadata): {Key}", file.Key);

                        if (!dryRun)
                        {
                            // Verify output is actually valid before deleting source
                            if (await VerifyOutputInS3Async(file.ExpectedOutputKey, cancellationToken))
                            {
                                await DeleteObjectAsync(file.Key, cancellationToken);
                                Log.Information("Completed interrupted migration: {Key}", file.Key);
                                result.Status = ConversionStatus.Success;
                            }
                            else
                            {
                                // Output exists with our metadata but is corrupt
                                // Delete corrupt output and reconvert
                                Log.Warning("Migration output is corrupt, will reconvert: {Key}", file.ExpectedOutputKey);
                                await DeleteObjectAsync(file.ExpectedOutputKey, cancellationToken);
                                // Fall through to conversion below
                                outputExists = false;
                            }
                        }
                        else
                        {
                            Log.Information("[DRY-RUN] Would complete interrupted migration: {Key}", file.Key);
                            result.Status = ConversionStatus.DryRun;
                        }

                        if (outputExists) // Didn't need to reconvert
                        {
                            result.Duration = sw.Elapsed;
                            return result;
                        }
                    }
                    else
                    {
                        // Output exists but NO migration metadata OR metadata doesn't match
                        // CANNOT prove relationship - mark for manual review
                        string reason = outputMeta == null
                            ? "Output exists but cannot read metadata"
                            : "Output exists without migration metadata - cannot prove it came from this source";

                        Log.Warning("MANUAL REVIEW REQUIRED: {Key} - {Reason}", file.Key, reason);
                        result.Status = ConversionStatus.ManualReviewRequired;
                        result.ReviewReason = reason;
                        result.Duration = sw.Elapsed;
                        return result;
                    }
                }

                // Case 3: Source exists, no output (or output was deleted above for reconversion)
                // → Normal conversion flow

                if (dryRun)
                {
                    Log.Information("[DRY-RUN] Would convert: {Key} -> {OutputKey}",
                        file.Key, file.ExpectedOutputKey);
                    result.Status = ConversionStatus.DryRun;
                    result.Duration = sw.Elapsed;
                    return result;
                }

                // Double-check output doesn't exist (race condition protection)
                if (await _inventoryService.KeyExistsAsync(file.ExpectedOutputKey, cancellationToken))
                {
                    Log.Warning("Output appeared during processing, marking for review: {Key}", file.ExpectedOutputKey);
                    result.Status = ConversionStatus.ManualReviewRequired;
                    result.ReviewReason = "Output appeared during processing - possible race condition";
                    result.Duration = sw.Elapsed;
                    return result;
                }

                // Step 2: Download source
                Log.Debug("Downloading source: {Key}", file.Key);
                using (var sourceStream = await DownloadObjectAsync(file.Key, cancellationToken))
                {
                    // Step 3: Convert
                    Log.Debug("Converting using {Converter}", _converter.ConverterId);
                    using (var outputStream = _converter.Convert(sourceStream))
                    {
                        // Step 4: Validate output
                        if (!_converter.ValidateOutput(outputStream))
                        {
                            throw new InvalidOperationException("Generated output failed validation");
                        }

                        result.ConvertedSizeBytes = outputStream.Length;

                        // Step 5: Upload output WITH MIGRATION METADATA
                        Log.Debug("Uploading output with migration metadata: {Key}", file.ExpectedOutputKey);
                        await UploadObjectWithMetadataAsync(
                            file.ExpectedOutputKey,
                            outputStream,
                            _converter.OutputContentType,
                            file.Key, // Original source key for traceability
                            cancellationToken);

                        // Step 6: Verify upload succeeded
                        var uploadedMeta = await GetObjectMetadataAsync(file.ExpectedOutputKey, cancellationToken);

                        if (uploadedMeta == null)
                        {
                            throw new InvalidOperationException("Upload verification failed: cannot read uploaded object metadata");
                        }

                        if (uploadedMeta.ContentLength != result.ConvertedSizeBytes)
                        {
                            throw new InvalidOperationException(
                                $"Upload verification failed: expected {result.ConvertedSizeBytes} bytes, " +
                                $"got {uploadedMeta.ContentLength}");
                        }

                        // Verify migration metadata was saved
                        if (!IsMigrationOutput(uploadedMeta, file.Key))
                        {
                            throw new InvalidOperationException(
                                "Upload verification failed: migration metadata not saved correctly");
                        }

                        Log.Debug("Upload verified: {Size} bytes with migration metadata", uploadedMeta.ContentLength);
                    }
                }

                // Step 7: Delete original source (only after verified output upload with metadata)
                Log.Debug("Deleting original source: {Key}", file.Key);
                await DeleteObjectAsync(file.Key, cancellationToken);

                Log.Information("Converted: {Original} -> {Output} ({OriginalSize} -> {ConvertedSize})",
                    file.Key, file.ExpectedOutputKey,
                    FormatBytes(result.OriginalSizeBytes),
                    FormatBytes(result.ConvertedSizeBytes));

                result.Status = ConversionStatus.Success;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to convert {Key}: {Error}", file.Key, ex.Message);
                result.Status = ConversionStatus.Failed;
                result.ErrorMessage = ex.Message;
            }

            result.Duration = sw.Elapsed;
            return result;
        }

        /// <summary>
        /// Check if an output's metadata indicates it was created by this migration tool
        /// for a specific source file.
        /// </summary>
        private bool IsMigrationOutput(GetObjectMetadataResponse metadata, string expectedSourceKey)
        {
            if (metadata?.Metadata == null)
                return false;

            // Check if migration metadata exists
            string sourceKey = metadata.Metadata[MetadataKeyMigrationSource.Replace("x-amz-meta-", "")];
            string version = metadata.Metadata[MetadataKeyMigrationVersion.Replace("x-amz-meta-", "")];

            if (string.IsNullOrEmpty(sourceKey) || string.IsNullOrEmpty(version))
                return false;

            // Verify the source key matches the file we're processing
            return string.Equals(sourceKey, expectedSourceKey, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<MemoryStream> DownloadObjectAsync(string key, CancellationToken cancellationToken)
        {
            var request = new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            using (var response = await _s3Client.GetObjectAsync(request, cancellationToken))
            {
                var ms = new MemoryStream();
                await response.ResponseStream.CopyToAsync(ms, 81920, cancellationToken);
                ms.Position = 0;
                return ms;
            }
        }

        private async Task UploadObjectWithMetadataAsync(
            string key,
            Stream content,
            string contentType,
            string sourceKey,
            CancellationToken cancellationToken)
        {
            content.Position = 0;

            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = content,
                ContentType = contentType
            };

            // Add migration metadata for traceability (generic keys)
            request.Metadata.Add("migration-source", sourceKey);
            request.Metadata.Add("migration-timestamp", DateTime.UtcNow.ToString("O"));
            request.Metadata.Add("migration-version", MigrationVersion);
            request.Metadata.Add("converter-type", _converter.ConverterId);

            await _s3Client.PutObjectAsync(request, cancellationToken);
        }

        private async Task<GetObjectMetadataResponse> GetObjectMetadataAsync(string key, CancellationToken cancellationToken)
        {
            try
            {
                var request = new GetObjectMetadataRequest
                {
                    BucketName = _bucketName,
                    Key = key
                };
                return await _s3Client.GetObjectMetadataAsync(request, cancellationToken);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        private async Task DeleteObjectAsync(string key, CancellationToken cancellationToken)
        {
            var request = new DeleteObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            await _s3Client.DeleteObjectAsync(request, cancellationToken);
        }

        private async Task<bool> VerifyOutputInS3Async(string key, CancellationToken cancellationToken)
        {
            try
            {
                using (var stream = await DownloadObjectAsync(key, cancellationToken))
                {
                    return _converter.ValidateOutput(stream);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to verify output {Key}: {Error}", key, ex.Message);
                return false;
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
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
