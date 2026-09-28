using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using S3FileConverter.Configuration;
using S3FileConverter.Models;
using S3FileConverter.Services;
using Newtonsoft.Json;
using Serilog;

namespace S3FileConverter.Commands
{
    /// <summary>
    /// Command to generate missing video thumbnails.
    /// Two modes: general (all missing) or individual (single video).
    /// Default is dry-run; use --apply to actually upload.
    /// </summary>
    public static class ThumbnailGenerateCommand
    {
        public static Command Create()
        {
            var keyOption = new Option<string>(
                "--key",
                description: "Process a single video by its S3 key (individual mode)");

            var prefixOption = new Option<string>(
                "--prefix",
                description: "Filter by S3 key prefix (general mode only)");

            var limitOption = new Option<int?>(
                "--limit",
                description: "Maximum number of videos to process (general mode only)");

            var applyOption = new Option<bool>(
                "--apply",
                getDefaultValue: () => false,
                description: "Actually upload generated thumbnails (default: dry-run/simulation)");

            var outputOption = new Option<string>(
                "--output",
                description: "Save detailed report to JSON file");

            var command = new Command("thumbnail-generate", "Generate missing video thumbnails")
            {
                keyOption,
                prefixOption,
                limitOption,
                applyOption,
                outputOption
            };

            command.SetHandler(async (InvocationContext context) =>
            {
                var key = context.ParseResult.GetValueForOption(keyOption);
                var prefix = context.ParseResult.GetValueForOption(prefixOption);
                var limit = context.ParseResult.GetValueForOption(limitOption);
                var apply = context.ParseResult.GetValueForOption(applyOption);
                var output = context.ParseResult.GetValueForOption(outputOption);
                var cancellationToken = context.GetCancellationToken();

                context.ExitCode = await RunAsync(key, prefix, limit, apply, output, cancellationToken);
            });

            return command;
        }

        private static async Task<int> RunAsync(
            string key,
            string prefix,
            int? limit,
            bool apply,
            string output,
            CancellationToken cancellationToken)
        {
            // Validate configuration
            AppConfig.Validate();

            bool isIndividualMode = !string.IsNullOrEmpty(key);
            bool isDryRun = !apply;

            Log.Information("=== Video Thumbnail Generator ===\"");
            Log.Information("Bucket: {Bucket}", AppConfig.BucketName);
            Log.Information("Region: {Region}", AppConfig.RegionName);
            Log.Information("Mode: {Mode}", isIndividualMode ? "Individual (single video)" : "General (scan for missing)");
            Log.Information("Operation: {Op}", isDryRun ? "DRY RUN (simulation)" : "APPLY (will upload)");
            Log.Information("");

            if (isIndividualMode)
            {
                Log.Information("Target video: {Key}", key);
                if (!string.IsNullOrEmpty(prefix))
                    Log.Warning("--prefix is ignored in individual mode");
                if (limit.HasValue)
                    Log.Warning("--limit is ignored in individual mode");
            }
            else
            {
                if (!string.IsNullOrEmpty(prefix))
                    Log.Information("Prefix filter: {Prefix}", prefix);
                if (limit.HasValue)
                    Log.Information("Limit: {Limit}", limit.Value);
            }

            Log.Information("");

            var report = new ThumbnailGenerationReport
            {
                BucketName = AppConfig.BucketName,
                PrefixFilter = prefix,
                DryRun = isDryRun,
                Mode = isIndividualMode ? "individual" : "general",
                SingleVideoKey = isIndividualMode ? key : null
            };

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var credentials = new BasicAWSCredentials(AppConfig.AccessKeyId, AppConfig.SecretAccessKey);
                using (var s3Client = new AmazonS3Client(credentials, AppConfig.Region))
                using (var generator = new ThumbnailGeneratorService(s3Client, AppConfig.BucketName))
                {
                    // Verify ffmpeg/ffprobe are available
                    var (toolsOk, ffmpegVer, ffprobeVer, toolError) = await generator.VerifyToolsAsync();
                    if (!toolsOk)
                    {
                        Log.Fatal("Tool verification failed: {Error}", toolError);
                        return 1;
                    }
                    Log.Information("ffmpeg: {Version}", ffmpegVer?.Split('\n')[0] ?? "unknown");
                    Log.Information("ffprobe: {Version}", ffprobeVer?.Split('\n')[0] ?? "unknown");
                    Log.Information("");

                    if (isIndividualMode)
                    {
                        await ProcessSingleVideoAsync(generator, key, isDryRun, report, cancellationToken);
                    }
                    else
                    {
                        await ProcessAllMissingAsync(generator, s3Client, prefix, limit, isDryRun, report, cancellationToken);
                    }
                }

                stopwatch.Stop();
                report.TotalDuration = stopwatch.Elapsed;

                // Display summary
                Log.Information("");
                Log.Information("=== Summary ===\"");
                Log.Information(report.Summary);

                // Save JSON if requested
                if (!string.IsNullOrEmpty(output))
                {
                    var json = JsonConvert.SerializeObject(report, Formatting.Indented);
                    File.WriteAllText(output, json);
                    Log.Information("");
                    Log.Information("Detailed report saved to: {Path}", output);
                }

                return report.ThumbnailsFailed > 0 ? 1 : 0;
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Operation cancelled by user");
                return 1;
            }
            catch (AmazonS3Exception ex)
            {
                Log.Fatal("AWS S3 error: {Message}", ex.Message);
                return 1;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Unexpected error");
                return 1;
            }
        }

        private static async Task ProcessSingleVideoAsync(
            ThumbnailGeneratorService generator,
            string videoKey,
            bool isDryRun,
            ThumbnailGenerationReport report,
            CancellationToken cancellationToken)
        {
            var entry = new ThumbnailOperationEntry
            {
                VideoKey = videoKey,
                ThumbnailKey = ThumbnailGeneratorService.BuildThumbKey(videoKey)
            };

            var entryStopwatch = Stopwatch.StartNew();

            try
            {
                // Verify video exists
                Log.Information("Checking if video exists: {Key}", videoKey);
                if (!await generator.KeyExistsAsync(videoKey, cancellationToken))
                {
                    entry.Status = "failed";
                    entry.ErrorMessage = "Video does not exist in S3";
                    Log.Error("Video not found: {Key}", videoKey);
                    report.ThumbnailsFailed++;
                    report.Operations.Add(entry);
                    return;
                }

                // Get video metadata
                var metadata = await generator.GetMetadataAsync(videoKey, cancellationToken);
                entry.VideoSizeBytes = metadata.ContentLength;
                Log.Information("Video size: {Size}", entry.VideoSizeFormatted);

                // Check if thumbnail already exists
                Log.Information("Checking for existing thumbnail: {Key}", entry.ThumbnailKey);
                if (await generator.KeyExistsAsync(entry.ThumbnailKey, cancellationToken))
                {
                    entry.Status = "exists";
                    Log.Information("Thumbnail already exists, skipping");
                    report.ThumbnailsAlreadyExist++;
                    report.Operations.Add(entry);
                    return;
                }

                Log.Information("Thumbnail missing, will generate");

                // Generate thumbnail
                await GenerateThumbnailForVideoAsync(generator, videoKey, entry, isDryRun, report, cancellationToken);
            }
            catch (Exception ex)
            {
                entry.Status = "failed";
                entry.ErrorMessage = ex.Message;
                Log.Error("Failed to process {Key}: {Error}", videoKey, ex.Message);
                report.ThumbnailsFailed++;
            }
            finally
            {
                entryStopwatch.Stop();
                entry.ProcessingTimeMs = entryStopwatch.Elapsed.TotalMilliseconds;
                report.TotalVideosProcessed++;
                report.Operations.Add(entry);
            }
        }

        private static async Task ProcessAllMissingAsync(
            ThumbnailGeneratorService generator,
            IAmazonS3 s3Client,
            string prefix,
            int? limit,
            bool isDryRun,
            ThumbnailGenerationReport report,
            CancellationToken cancellationToken)
        {
            // Use inventory service to find videos missing thumbnails
            var inventoryService = new S3InventoryService(s3Client, AppConfig.BucketName);

            Log.Information("Scanning for videos missing thumbnails...");
            var auditReport = await inventoryService.AuditVideoThumbnailsAsync(prefix, cancellationToken);

            Log.Information("Found {Total} videos, {Missing} missing thumbnails",
                auditReport.TotalVideosFound, auditReport.VideosMissingThumbnail);

            if (auditReport.VideosMissingThumbnail == 0)
            {
                Log.Information("No thumbnails to generate.");
                return;
            }

            // Apply limit
            var videosToProcess = auditReport.MissingThumbnails;
            if (limit.HasValue && videosToProcess.Count > limit.Value)
            {
                Log.Information("Applying limit: processing first {Limit} of {Total} videos",
                    limit.Value, videosToProcess.Count);
                videosToProcess = videosToProcess.GetRange(0, limit.Value);
            }

            Log.Information("");
            Log.Information("Processing {Count} videos...", videosToProcess.Count);
            Log.Information("");

            int processed = 0;
            foreach (var video in videosToProcess)
            {
                cancellationToken.ThrowIfCancellationRequested();

                processed++;
                Log.Information("[{Current}/{Total}] {Key}",
                    processed, videosToProcess.Count, video.VideoKey);

                var entry = new ThumbnailOperationEntry
                {
                    VideoKey = video.VideoKey,
                    ThumbnailKey = video.ExpectedThumbnailKey,
                    VideoSizeBytes = video.VideoSizeBytes
                };

                var entryStopwatch = Stopwatch.StartNew();

                try
                {
                    await GenerateThumbnailForVideoAsync(generator, video.VideoKey, entry, isDryRun, report, cancellationToken);
                }
                catch (Exception ex)
                {
                    entry.Status = "failed";
                    entry.ErrorMessage = ex.Message;
                    Log.Error("  Failed: {Error}", ex.Message);
                    report.ThumbnailsFailed++;
                }
                finally
                {
                    entryStopwatch.Stop();
                    entry.ProcessingTimeMs = entryStopwatch.Elapsed.TotalMilliseconds;
                    report.TotalVideosProcessed++;
                    report.Operations.Add(entry);
                }
            }
        }

        private static async Task GenerateThumbnailForVideoAsync(
            ThumbnailGeneratorService generator,
            string videoKey,
            ThumbnailOperationEntry entry,
            bool isDryRun,
            ThumbnailGenerationReport report,
            CancellationToken cancellationToken)
        {
            string videoPath = null;
            string thumbPath = null;

            try
            {
                // Download video
                Log.Information("  Downloading video...");
                videoPath = await generator.DownloadVideoAsync(videoKey, cancellationToken);
                Log.Debug("  Downloaded to: {Path}", videoPath);

                // Get duration
                var duration = await generator.GetVideoDurationAsync(videoPath, cancellationToken);
                entry.VideoDurationSeconds = duration;
                if (duration.HasValue)
                {
                    Log.Information("  Duration: {Duration:F1}s", duration.Value);
                }

                // Generate thumbnail
                string extension = Path.GetExtension(videoPath);
                thumbPath = videoPath.Replace(extension, ".thumb.jpg");

                Log.Information("  Extracting frame...");
                var (success, error) = await generator.GenerateThumbnailAsync(videoPath, thumbPath, cancellationToken);

                if (!success)
                {
                    entry.Status = "failed";
                    entry.ErrorMessage = error;
                    Log.Error("  Thumbnail generation failed: {Error}", error);
                    report.ThumbnailsFailed++;
                    return;
                }

                var thumbInfo = new FileInfo(thumbPath);
                Log.Information("  Generated thumbnail: {Size} bytes", thumbInfo.Length);

                // Upload or simulate
                if (isDryRun)
                {
                    entry.Status = "generated";
                    Log.Information("  [DRY RUN] Would upload to: {Key}", entry.ThumbnailKey);
                    report.ThumbnailsGenerated++;
                }
                else
                {
                    Log.Information("  Uploading to: {Key}", entry.ThumbnailKey);
                    await generator.UploadThumbnailAsync(thumbPath, entry.ThumbnailKey, cancellationToken);
                    entry.Status = "generated";
                    Log.Information("  Upload complete");
                    report.ThumbnailsGenerated++;
                }
            }
            finally
            {
                // Cleanup temp files
                generator.CleanupFile(videoPath);
                generator.CleanupFile(thumbPath);
            }
        }
    }
}
