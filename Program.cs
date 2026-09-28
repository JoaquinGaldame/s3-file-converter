using System.CommandLine;
using System.Threading.Tasks;
using S3FileConverter.Commands;
using Serilog;

namespace S3FileConverter
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.File(
                    "logs/s3-converter-.log",
                    rollingInterval: RollingInterval.Day,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            try
            {
                // Build CLI
                var rootCommand = new RootCommand(
                    "S3 File Converter - Convert files in S3 buckets between formats\n\n" +
                    "Supported conversions:\n" +
                    "  - HEIC/HEIF -> JPEG (images)\n" +
                    "  - More converters can be added\n\n" +
                    "Configuration:\n" +
                    "  Edit S3FileConverter.exe.config with your AWS credentials:\n" +
                    "    - AWS:AccessKeyId\n" +
                    "    - AWS:SecretAccessKey\n" +
                    "    - AWS:Region (default: eu-west-1)\n" +
                    "    - S3:BucketName\n\n" +
                    "Examples:\n" +
                    "  S3FileConverter inventory --types heic,heif\n" +
                    "  S3FileConverter inventory --list-converters\n" +
                    "  S3FileConverter convert --from heic --to jpg --dry-run\n" +
                    "  S3FileConverter convert --from heic,heif --to jpg --enable-writes --limit 10");

                rootCommand.AddCommand(InventoryCommand.Create());
                rootCommand.AddCommand(ConvertCommand.Create());
                rootCommand.AddCommand(VideoThumbnailsAuditCommand.Create());
                rootCommand.AddCommand(ThumbnailGenerateCommand.Create());

                return await rootCommand.InvokeAsync(args);
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }
    }
}
