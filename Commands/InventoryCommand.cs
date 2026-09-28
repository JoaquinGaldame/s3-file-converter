using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using Amazon.S3;
using S3FileConverter.Configuration;
using S3FileConverter.Converters;
using S3FileConverter.Models;
using S3FileConverter.Services;
using Newtonsoft.Json;
using Serilog;

namespace S3FileConverter.Commands
{
    /// <summary>
    /// Inventory command - read-only mode to count and list files by type.
    /// Does NOT modify, upload, or delete any objects.
    /// </summary>
    public static class InventoryCommand
    {
        public static Command Create()
        {
            var typesOption = new Option<string>(
                "--types",
                getDefaultValue: () => "heic,heif",
                description: "Comma-separated file extensions to scan for (e.g., 'heic,heif,png,webp')");

            var prefixOption = new Option<string>(
                "--prefix",
                description: "Filter by S3 key prefix (e.g., a specific folder like 'images/')");

            var limitOption = new Option<int?>(
                "--limit",
                description: "Maximum number of files to list");

            var checkMagicBytesOption = new Option<bool>(
                "--check-magic-bytes",
                getDefaultValue: () => false,
                description: "Also check files without matching extension by reading magic bytes (slower, HEIC only)");

            var outputOption = new Option<string>(
                "--output",
                description: "Save report to JSON file (optional)");

            var listConvertersOption = new Option<bool>(
                "--list-converters",
                getDefaultValue: () => false,
                description: "List all available converters and their supported formats");

            var detectByPatternOption = new Option<string>(
                "--detect-by-pattern",
                description: "Detect files by key pattern instead of extension (e.g., '/video' to find all video files regardless of extension)");

            var command = new Command("inventory", "Scan bucket for files by type (read-only mode)")
            {
                typesOption,
                prefixOption,
                limitOption,
                checkMagicBytesOption,
                outputOption,
                listConvertersOption,
                detectByPatternOption
            };

            command.SetHandler(async (InvocationContext context) =>
            {
                var types = context.ParseResult.GetValueForOption(typesOption);
                var prefix = context.ParseResult.GetValueForOption(prefixOption);
                var limit = context.ParseResult.GetValueForOption(limitOption);
                var checkMagicBytes = context.ParseResult.GetValueForOption(checkMagicBytesOption);
                var output = context.ParseResult.GetValueForOption(outputOption);
                var listConverters = context.ParseResult.GetValueForOption(listConvertersOption);
                var detectByPattern = context.ParseResult.GetValueForOption(detectByPatternOption);
                var cancellationToken = context.GetCancellationToken();

                context.ExitCode = await RunAsync(types, prefix, limit, checkMagicBytes, output, listConverters, detectByPattern, cancellationToken);
            });

            return command;
        }

        private static async Task<int> RunAsync(
            string types,
            string prefix,
            int? limit,
            bool checkMagicBytes,
            string output,
            bool listConverters,
            string detectByPattern,
            CancellationToken cancellationToken)
        {
            // Handle --list-converters
            if (listConverters)
            {
                ListAvailableConverters();
                return 0;
            }

            // Validate configuration at startup
            AppConfig.Validate();

            // Handle --detect-by-pattern (different code path)
            if (!string.IsNullOrEmpty(detectByPattern))
            {
                return await RunPatternScanAsync(detectByPattern, prefix, output, cancellationToken);
            }

            // Parse types
            var extensions = types.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(t => t.Trim().ToLowerInvariant().TrimStart('.'))
                                  .Where(t => !string.IsNullOrEmpty(t))
                                  .ToList();

            if (extensions.Count == 0)
            {
                Log.Error("No valid file types specified. Use --types with comma-separated extensions.");
                return 1;
            }

            Log.Information("=== S3 File Inventory (Read-Only) ===");
            Log.Information("Bucket: {Bucket}", AppConfig.BucketName);
            Log.Information("Region: {Region}", AppConfig.RegionName);
            Log.Information("File types: {Types}", string.Join(", ", extensions));

            if (!string.IsNullOrEmpty(prefix))
                Log.Information("Prefix filter: {Prefix}", prefix);

            if (limit.HasValue)
                Log.Information("Limit: {Limit}", limit.Value);

            if (checkMagicBytes)
                Log.Information("Magic bytes detection: ENABLED (slower scan)");

            Log.Information("");
            Log.Information("This mode is READ-ONLY. No files will be modified, uploaded, or deleted.");
            Log.Information("");

            try
            {
                var credentials = new BasicAWSCredentials(AppConfig.AccessKeyId, AppConfig.SecretAccessKey);
                using (var s3Client = new AmazonS3Client(credentials, AppConfig.Region))
                {
                    var inventoryService = new S3InventoryService(s3Client, AppConfig.BucketName);

                    var report = await inventoryService.ScanForFilesAsync(
                        extensions,
                        prefix,
                        limit,
                        checkMagicBytes,
                        cancellationToken);

                    // Display results
                    Log.Information("");
                    Log.Information("=== Inventory Report ===");
                    Log.Information("Generated at: {Time}", report.GeneratedAt);
                    Log.Information("");
                    Log.Information("Total files found: {Count}", report.TotalFiles);
                    Log.Information("  - Detected by extension: {Count}", report.DetectedByExtension);
                    Log.Information("  - Detected by magic bytes: {Count}", report.DetectedByMagicBytes);
                    Log.Information("");
                    Log.Information("Total size: {Size}", report.TotalSizeFormatted);
                    Log.Information("Pages scanned: {Pages}", report.PagesScanned);
                    Log.Information("Scan completed: {Completed}", report.ScanCompleted ? "Yes" : "No (limit reached)");

                    if (report.SampleKeys.Count > 0)
                    {
                        Log.Information("");
                        Log.Information("Sample keys (first {Count}):", report.SampleKeys.Count);
                        foreach (var key in report.SampleKeys)
                        {
                            Log.Information("  - {Key}", key);
                        }
                    }

                    Log.Information("");
                    Log.Information("Detection criteria:");
                    Log.Information(report.DetectionCriteria);

                    if (!string.IsNullOrEmpty(output))
                    {
                        var json = JsonConvert.SerializeObject(report, Formatting.Indented);
                        File.WriteAllText(output, json);
                        Log.Information("");
                        Log.Information("Report saved to: {Path}", output);
                    }

                    return 0;
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
                Log.Fatal(ex, "Unexpected error during inventory scan");
                return 1;
            }
        }

        private static async Task<int> RunPatternScanAsync(
            string pattern,
            string prefix,
            string output,
            CancellationToken cancellationToken)
        {
            Log.Information("=== S3 Video Pattern Inventory (Read-Only) ===");
            Log.Information("Bucket: {Bucket}", AppConfig.BucketName);
            Log.Information("Region: {Region}", AppConfig.RegionName);
            Log.Information("Detection pattern: '{Pattern}'", pattern);

            if (!string.IsNullOrEmpty(prefix))
                Log.Information("Prefix filter: {Prefix}", prefix);

            Log.Information("");
            Log.Information("This mode is READ-ONLY. No files will be modified, uploaded, or deleted.");
            Log.Information("Thumbnails (thumb_video*) will be excluded from results.");
            Log.Information("");

            try
            {
                var credentials = new BasicAWSCredentials(AppConfig.AccessKeyId, AppConfig.SecretAccessKey);
                using (var s3Client = new AmazonS3Client(credentials, AppConfig.Region))
                {
                    var inventoryService = new S3InventoryService(s3Client, AppConfig.BucketName);

                    var report = await inventoryService.ScanByPatternAsync(
                        pattern,
                        prefix,
                        cancellationToken);

                    // Display results
                    Log.Information("");
                    Log.Information("=== Video Pattern Report ===");
                    Log.Information("Generated at: {Time}", report.GeneratedAt);
                    Log.Information("");
                    Log.Information("Total objects scanned: {Count:N0}", report.TotalObjectsScanned);
                    Log.Information("Total videos detected: {Count}", report.TotalVideosDetected);
                    Log.Information("Thumbnails excluded: {Count}", report.ThumbnailsExcluded);
                    Log.Information("");
                    Log.Information("Total size: {Size}", report.TotalSizeFormatted);
                    Log.Information("Pages scanned: {Pages}", report.PagesScanned);
                    Log.Information("Scan completed: {Completed}", report.ScanCompleted ? "Yes" : "No");

                    // Extension distribution
                    if (report.ExtensionDistribution.Count > 0)
                    {
                        Log.Information("");
                        Log.Information("Extension distribution:");
                        foreach (var kvp in report.ExtensionDistribution.OrderByDescending(x => x.Value))
                        {
                            Log.Information("  - {Extension}: {Count}", kvp.Key, kvp.Value);
                        }
                    }

                    // Videos per page summary
                    if (report.VideosPerPage.Count > 0)
                    {
                        var nonZeroPages = report.VideosPerPage.Where(v => v > 0).ToList();
                        if (nonZeroPages.Count > 0)
                        {
                            Log.Information("");
                            Log.Information("Videos per page: min={Min}, max={Max}, avg={Avg:F1}",
                                nonZeroPages.Min(),
                                nonZeroPages.Max(),
                                nonZeroPages.Average());
                        }
                    }

                    // Sample keys
                    if (report.SampleKeys.Count > 0)
                    {
                        Log.Information("");
                        Log.Information("Sample keys (first {Count}):", report.SampleKeys.Count);
                        foreach (var key in report.SampleKeys)
                        {
                            Log.Information("  - {Key}", key);
                        }
                    }

                    Log.Information("");
                    Log.Information("Detection criteria:");
                    Log.Information(report.DetectionCriteria);

                    // Save report
                    string outputPath = output ?? "video-pattern-report.json";
                    var json = JsonConvert.SerializeObject(report, Formatting.Indented);
                    File.WriteAllText(outputPath, json);
                    Log.Information("");
                    Log.Information("Report saved to: {Path}", outputPath);

                    return 0;
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
                Log.Fatal(ex, "Unexpected error during pattern scan");
                return 1;
            }
        }

        private static void ListAvailableConverters()
        {
            var registry = ConverterRegistry.CreateDefault();

            Log.Information("=== Available Converters ===");
            Log.Information("");

            foreach (var converter in registry.GetAll())
            {
                Log.Information("Converter: {Id}", converter.ConverterId);
                Log.Information("  Input formats: {Formats}", string.Join(", ", converter.SupportedInputExtensions));
                Log.Information("  Output format: {Format} ({ContentType})", converter.OutputExtension, converter.OutputContentType);
                Log.Information("");
            }

            Log.Information("Supported input extensions: {Extensions}",
                string.Join(", ", registry.GetSupportedInputExtensions()));
            Log.Information("Supported output extensions: {Extensions}",
                string.Join(", ", registry.GetSupportedOutputExtensions()));
        }
    }
}
