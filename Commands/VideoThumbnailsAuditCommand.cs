using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using S3FileConverter.Configuration;
using S3FileConverter.Services;
using Newtonsoft.Json;
using Serilog;

namespace S3FileConverter.Commands
{
    /// <summary>
    /// Video thumbnails audit command - identifies videos missing their thumbnails.
    /// Read-only: does not download, generate, upload, or delete any objects.
    /// </summary>
    public static class VideoThumbnailsAuditCommand
    {
        public static Command Create()
        {
            var prefixOption = new Option<string>(
                "--prefix",
                description: "Filter by S3 key prefix (e.g., a specific GUID folder)");

            var outputOption = new Option<string>(
                "--output",
                description: "Save detailed report to JSON file");

            var command = new Command("video-thumbnails-audit", "Audit videos for missing thumbnails (read-only)")
            {
                prefixOption,
                outputOption
            };

            command.SetHandler(async (InvocationContext context) =>
            {
                var prefix = context.ParseResult.GetValueForOption(prefixOption);
                var output = context.ParseResult.GetValueForOption(outputOption);
                var cancellationToken = context.GetCancellationToken();

                context.ExitCode = await RunAsync(prefix, output, cancellationToken);
            });

            return command;
        }

        private static async Task<int> RunAsync(
            string prefix,
            string output,
            CancellationToken cancellationToken)
        {
            // Validate configuration at startup
            AppConfig.Validate();

            Log.Information("=== Video Thumbnails Audit (Read-Only) ===");
            Log.Information("Bucket: {Bucket}", AppConfig.BucketName);
            Log.Information("Region: {Region}", AppConfig.RegionName);

            if (!string.IsNullOrEmpty(prefix))
                Log.Information("Prefix filter: {Prefix}", prefix);

            Log.Information("");
            Log.Information("This audit is READ-ONLY. No files will be downloaded, generated, uploaded, or deleted.");
            Log.Information("");

            try
            {
                var credentials = new BasicAWSCredentials(AppConfig.AccessKeyId, AppConfig.SecretAccessKey);
                using (var s3Client = new AmazonS3Client(credentials, AppConfig.Region))
                {
                    var inventoryService = new S3InventoryService(s3Client, AppConfig.BucketName);

                    var report = await inventoryService.AuditVideoThumbnailsAsync(prefix, cancellationToken);

                    // Display results
                    Log.Information("");
                    Log.Information("=== Thumbnail Audit Report ===");
                    Log.Information("Generated at: {Time}", report.GeneratedAt);
                    Log.Information("");

                    // Scan completeness check
                    if (!report.ScanCompleted)
                    {
                        Log.Warning("*** SCAN INCOMPLETE ***");
                        if (!string.IsNullOrEmpty(report.ScanError))
                            Log.Warning("Error: {Error}", report.ScanError);
                        Log.Warning("Results below may not reflect the full bucket state.");
                        Log.Information("");
                    }

                    Log.Information("Total objects scanned: {Count:N0}", report.TotalObjectsScanned);
                    Log.Information("Pages scanned: {Pages}", report.PagesScanned);
                    Log.Information("Scan completed: {Completed}", report.ScanCompleted ? "Yes" : "NO - INCOMPLETE");
                    Log.Information("");

                    Log.Information("Total videos found: {Count}", report.TotalVideosFound);
                    Log.Information("  - With thumbnail: {Count}", report.VideosWithThumbnail);
                    Log.Information("  - Missing thumbnail: {Count}", report.VideosMissingThumbnail);
                    Log.Information("");

                    // Show sample of missing thumbnails
                    if (report.MissingThumbnails.Count > 0)
                    {
                        int sampleCount = Math.Min(10, report.MissingThumbnails.Count);
                        Log.Information("Sample of videos missing thumbnails (first {Count}):", sampleCount);

                        for (int i = 0; i < sampleCount; i++)
                        {
                            var entry = report.MissingThumbnails[i];
                            Log.Information("  Video: {VideoKey}", entry.VideoKey);
                            Log.Information("    Expected thumb: {ThumbKey}", entry.ExpectedThumbnailKey);
                            Log.Information("    Size: {Size}, Modified: {Date:yyyy-MM-dd HH:mm:ss}",
                                entry.VideoSizeFormatted, entry.VideoLastModified);
                            Log.Information("");
                        }

                        if (report.MissingThumbnails.Count > sampleCount)
                        {
                            Log.Information("  ... and {More} more. Use --output to save full list.",
                                report.MissingThumbnails.Count - sampleCount);
                        }
                    }
                    else if (report.TotalVideosFound > 0)
                    {
                        Log.Information("All videos have their thumbnails.");
                    }
                    else
                    {
                        Log.Information("No videos found in the bucket.");
                    }

                    Log.Information("");
                    Log.Information("Detection criteria:");
                    Log.Information(report.DetectionCriteria);

                    // Save JSON if requested
                    if (!string.IsNullOrEmpty(output))
                    {
                        var json = JsonConvert.SerializeObject(report, Formatting.Indented);
                        File.WriteAllText(output, json);
                        Log.Information("");
                        Log.Information("Full report saved to: {Path}", output);
                    }
                    else if (report.MissingThumbnails.Count > 10)
                    {
                        Log.Information("");
                        Log.Information("Tip: Use --output <file.json> to save the complete list of {Count} missing thumbnails.",
                            report.MissingThumbnails.Count);
                    }

                    return report.ScanCompleted ? 0 : 1;
                }
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Operation cancelled by user");
                return 1;
            }
            catch (AmazonS3Exception ex)
            {
                Log.Fatal("AWS S3 error: {Message}", ex.Message);
                Log.Fatal("Check credentials in S3FileConverter.exe.config");
                return 1;
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Unexpected error during thumbnail audit");
                return 1;
            }
        }
    }
}
