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
    /// Convert command - batch conversion of files in S3.
    /// Requires --enable-writes to perform actual modifications.
    /// </summary>
    public static class ConvertCommand
    {
        public static Command Create()
        {
            var fromOption = new Option<string>(
                "--from",
                getDefaultValue: () => "heic,heif",
                description: "Source file extensions to convert (comma-separated, e.g., 'heic,heif')");

            var toOption = new Option<string>(
                "--to",
                getDefaultValue: () => "jpg",
                description: "Target format extension (e.g., 'jpg')");

            var prefixOption = new Option<string>(
                "--prefix",
                description: "Filter by S3 key prefix (e.g., a specific folder like 'images/')");

            var limitOption = new Option<int?>(
                "--limit",
                description: "Maximum number of files to process (recommended for initial testing)");

            var enableWritesOption = new Option<bool>(
                "--enable-writes",
                getDefaultValue: () => false,
                description: "REQUIRED to perform actual conversions. Without this flag, runs in dry-run mode.");

            var dryRunOption = new Option<bool>(
                "--dry-run",
                getDefaultValue: () => false,
                description: "Simulate conversion without making changes (same as omitting --enable-writes)");

            var outputOption = new Option<string>(
                "--output",
                getDefaultValue: () => "conversion-report.json",
                description: "Save report to JSON file");

            var command = new Command("convert", "Convert files from one format to another")
            {
                fromOption,
                toOption,
                prefixOption,
                limitOption,
                enableWritesOption,
                dryRunOption,
                outputOption
            };

            command.SetHandler(async (InvocationContext context) =>
            {
                var from = context.ParseResult.GetValueForOption(fromOption);
                var to = context.ParseResult.GetValueForOption(toOption);
                var prefix = context.ParseResult.GetValueForOption(prefixOption);
                var limit = context.ParseResult.GetValueForOption(limitOption);
                var enableWrites = context.ParseResult.GetValueForOption(enableWritesOption);
                var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
                var output = context.ParseResult.GetValueForOption(outputOption);
                var cancellationToken = context.GetCancellationToken();

                context.ExitCode = await RunAsync(from, to, prefix, limit, enableWrites, dryRun, output, cancellationToken);
            });

            return command;
        }

        private static async Task<int> RunAsync(
            string from,
            string to,
            string prefix,
            int? limit,
            bool enableWrites,
            bool dryRun,
            string output,
            CancellationToken cancellationToken)
        {
            // Validate configuration at startup
            AppConfig.Validate();

            // Parse source extensions
            var sourceExtensions = from.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(t => t.Trim().ToLowerInvariant().TrimStart('.'))
                                       .Where(t => !string.IsNullOrEmpty(t))
                                       .ToList();

            if (sourceExtensions.Count == 0)
            {
                Log.Error("No valid source formats specified. Use --from with comma-separated extensions.");
                return 1;
            }

            var targetExtension = to.Trim().ToLowerInvariant().TrimStart('.');
            if (string.IsNullOrEmpty(targetExtension))
            {
                Log.Error("No valid target format specified. Use --to with the output extension.");
                return 1;
            }

            // Find a converter that can handle this conversion
            var registry = ConverterRegistry.CreateDefault();
            IFileConverter converter = null;

            foreach (var ext in sourceExtensions)
            {
                converter = registry.Find(ext, targetExtension);
                if (converter != null)
                    break;
            }

            if (converter == null)
            {
                Log.Error("No converter found for {From} -> {To}", from, to);
                Log.Error("");
                Log.Error("Available converters:");
                foreach (var c in registry.GetAll())
                {
                    Log.Error("  - {Id}: {Input} -> {Output}",
                        c.ConverterId,
                        string.Join(", ", c.SupportedInputExtensions),
                        c.OutputExtension);
                }
                return 1;
            }

            // Filter source extensions to only those supported by the converter
            var supportedSources = sourceExtensions
                .Where(ext => converter.CanConvert(ext))
                .ToList();

            bool isDryRun = dryRun || !enableWrites;

            Log.Information("=== S3 File Converter ===");
            Log.Information("Bucket: {Bucket}", AppConfig.BucketName);
            Log.Information("Region: {Region}", AppConfig.RegionName);
            Log.Information("Converter: {Id}", converter.ConverterId);
            Log.Information("Source formats: {From}", string.Join(", ", supportedSources));
            Log.Information("Target format: {To}", targetExtension);
            Log.Information("");

            if (isDryRun)
            {
                Log.Warning("*** DRY-RUN MODE ***");
                Log.Warning("No files will be modified. Use --enable-writes to perform actual conversions.");
                Log.Information("");
            }
            else
            {
                Log.Warning("*** WRITE MODE ENABLED ***");
                Log.Warning("This will CONVERT files and DELETE originals after successful conversion.");
                Log.Information("");
            }

            if (!string.IsNullOrEmpty(prefix))
                Log.Information("Prefix filter: {Prefix}", prefix);

            if (limit.HasValue)
                Log.Information("Limit: {Limit} files", limit.Value);

            try
            {
                // Configure converter (e.g., ImageMagick resource limits)
                if (converter is HeicToJpegConverter)
                {
                    HeicToJpegConverter.ConfigureResourceLimits();
                }

                var credentials = new BasicAWSCredentials(AppConfig.AccessKeyId, AppConfig.SecretAccessKey);
                using (var s3Client = new AmazonS3Client(credentials, AppConfig.Region))
                {
                    var inventoryService = new S3InventoryService(s3Client, AppConfig.BucketName);

                    // Step 1: Scan for source files
                    Log.Information("");
                    Log.Information("Scanning for {Types} files...", string.Join(", ", supportedSources));

                    var sourceFiles = await inventoryService.GetFilesAsync(
                        supportedSources,
                        prefix,
                        limit,
                        cancellationToken);

                    if (sourceFiles.Count == 0)
                    {
                        Log.Information("No files found matching criteria.");
                        return 0;
                    }

                    Log.Information("Found {Count} files to process", sourceFiles.Count);
                    Log.Information("");

                    // Step 2: Confirm if in write mode
                    if (!isDryRun)
                    {
                        Log.Warning("You are about to convert {Count} files.", sourceFiles.Count);
                        Log.Warning("Each file will be:");
                        Log.Warning("  1. Downloaded from S3");
                        Log.Warning("  2. Converted using {Converter}", converter.ConverterId);
                        Log.Warning("  3. Validated to ensure conversion succeeded");
                        Log.Warning("  4. Uploaded as .{Extension} with migration metadata", targetExtension);
                        Log.Warning("  5. Original file DELETED after successful upload verification");
                        Log.Information("");
                        Log.Warning("Press ENTER to continue or Ctrl+C to abort...");

                        Console.ReadLine();
                    }

                    // Step 3: Process files
                    var orchestrator = new ConversionOrchestrator(s3Client, AppConfig.BucketName, converter);

                    var report = await orchestrator.ProcessBatchAsync(
                        sourceFiles,
                        isDryRun,
                        cancellationToken);

                    // Update report metadata
                    report.PrefixFilter = prefix;
                    report.LimitApplied = limit;
                    report.SourceExtensions = supportedSources;

                    // Step 4: Display results
                    Log.Information("");
                    Log.Information("=== Conversion Report ===");
                    Log.Information("Duration: {Duration}", report.Duration);
                    Log.Information("Converter: {Converter}", report.ConverterId);
                    Log.Information("");
                    Log.Information("Total processed: {Total}", report.TotalProcessed);
                    Log.Information("  - Successful: {Count}", report.Successful);
                    Log.Information("  - Skipped (already done): {Count}", report.Skipped);
                    Log.Information("  - Manual review required: {Count}", report.ManualReviewRequired);
                    Log.Information("  - Failed: {Count}", report.Failed);

                    if (!isDryRun && report.Successful > 0)
                    {
                        Log.Information("");
                        Log.Information("Size impact:");
                        Log.Information("  - Original total: {Size}", FormatBytes(report.TotalOriginalBytes));
                        Log.Information("  - Converted total: {Size}", FormatBytes(report.TotalConvertedBytes));
                        Log.Information("  - Bytes saved: {Size}", FormatBytes(report.BytesSaved));
                    }

                    if (report.ManualReviewKeys.Count > 0)
                    {
                        Log.Warning("");
                        Log.Warning("*** MANUAL REVIEW REQUIRED ({Count} files) ***", report.ManualReviewKeys.Count);
                        Log.Warning("The following files have ambiguous state (output exists but relationship cannot be verified):");
                        Log.Warning("NO source file was deleted for these. Review manually before proceeding.");
                        Log.Warning("");
                        foreach (var key in report.ManualReviewKeys)
                        {
                            Log.Warning("  - {Key}", key);
                        }
                    }

                    if (report.FailedKeys.Count > 0)
                    {
                        Log.Warning("");
                        Log.Warning("Failed keys ({Count}):", report.FailedKeys.Count);
                        foreach (var key in report.FailedKeys)
                        {
                            Log.Warning("  - {Key}", key);
                        }
                    }

                    if (!string.IsNullOrEmpty(output))
                    {
                        var json = JsonConvert.SerializeObject(report, Formatting.Indented);
                        File.WriteAllText(output, json);
                        Log.Information("");
                        Log.Information("Report saved to: {Path}", output);
                    }

                    return (report.Failed > 0 || report.ManualReviewRequired > 0) ? 1 : 0;
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
                Log.Fatal(ex, "Unexpected error during conversion");
                return 1;
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
