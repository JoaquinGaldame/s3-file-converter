using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Serilog;

namespace S3FileConverter.Services
{
    /// <summary>
    /// Generates video thumbnails using ffmpeg/ffprobe.
    /// </summary>
    public class ThumbnailGeneratorService : IDisposable
    {
        private readonly IAmazonS3 _s3Client;
        private readonly string _bucketName;
        private readonly string _tempDirectory;

        // Thumbnail settings (matching API behavior)
        public const int ThumbnailMaxSide = 320;
        public const int JpegQuality = 70;
        public const double DefaultCaptureTimeSeconds = 1.5;

        public ThumbnailGeneratorService(IAmazonS3 s3Client, string bucketName)
        {
            _s3Client = s3Client ?? throw new ArgumentNullException(nameof(s3Client));
            _bucketName = bucketName ?? throw new ArgumentNullException(nameof(bucketName));

            // Create temp directory for processing
            _tempDirectory = Path.Combine(Path.GetTempPath(), "s3-thumb-generator");
            if (!Directory.Exists(_tempDirectory))
            {
                Directory.CreateDirectory(_tempDirectory);
            }
        }

        /// <summary>
        /// Verify ffmpeg and ffprobe are available.
        /// </summary>
        public async Task<(bool Success, string FfmpegVersion, string FfprobeVersion, string Error)> VerifyToolsAsync()
        {
            try
            {
                string ffmpegVersion = await GetToolVersionAsync("ffmpeg");
                string ffprobeVersion = await GetToolVersionAsync("ffprobe");

                if (string.IsNullOrEmpty(ffmpegVersion))
                    return (false, null, null, "ffmpeg not found. Ensure it's installed and in PATH.");

                if (string.IsNullOrEmpty(ffprobeVersion))
                    return (false, ffmpegVersion, null, "ffprobe not found. Ensure it's installed and in PATH.");

                return (true, ffmpegVersion, ffprobeVersion, null);
            }
            catch (Exception ex)
            {
                return (false, null, null, $"Error checking tools: {ex.Message}");
            }
        }

        private async Task<string> GetToolVersionAsync(string tool)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    string output = await process.StandardOutput.ReadLineAsync();
                    process.WaitForExit();

                    if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        // Extract version from first line
                        return output.Trim();
                    }
                }
            }
            catch
            {
                // Tool not found
            }

            return null;
        }

        /// <summary>
        /// Get video duration using ffprobe.
        /// </summary>
        public async Task<double?> GetVideoDurationAsync(string videoPath, CancellationToken cancellationToken = default)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffprobe",
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    string output = await process.StandardOutput.ReadToEndAsync();
                    process.WaitForExit();

                    if (process.ExitCode == 0 && double.TryParse(output.Trim(), out double duration))
                    {
                        return duration;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to get video duration: {Error}", ex.Message);
            }

            return null;
        }

        /// <summary>
        /// Generate thumbnail for a video.
        /// </summary>
        /// <param name="videoPath">Path to local video file</param>
        /// <param name="outputPath">Path for output thumbnail</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if successful</returns>
        public async Task<(bool Success, string Error)> GenerateThumbnailAsync(
            string videoPath,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Get video duration to determine capture time
                double? duration = await GetVideoDurationAsync(videoPath, cancellationToken);
                double captureTime;

                if (duration.HasValue && duration.Value >= 2.0)
                {
                    captureTime = DefaultCaptureTimeSeconds;
                }
                else if (duration.HasValue)
                {
                    captureTime = duration.Value / 2.0;
                }
                else
                {
                    // Fallback if we can't get duration
                    captureTime = DefaultCaptureTimeSeconds;
                }

                // Format capture time as HH:mm:ss.fff
                var captureTimeSpan = TimeSpan.FromSeconds(captureTime);
                string timeArg = captureTimeSpan.ToString(@"hh\:mm\:ss\.fff");

                // ffmpeg command to extract frame and scale
                // -ss before -i = fast seek
                // -vf scale: scale to max 320 while preserving aspect ratio
                // -vframes 1: extract only one frame
                // -q:v: quality (2 = high, maps to ~70 quality in ImageMagick scale)
                string arguments = $"-ss {timeArg} -i \"{videoPath}\" " +
                    $"-vf \"scale='min({ThumbnailMaxSide},iw)':min'({ThumbnailMaxSide},ih)':force_original_aspect_ratio=decrease\" " +
                    $"-vframes 1 -q:v 2 -y \"{outputPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                Log.Debug("Running ffmpeg: {Arguments}", arguments);

                using (var process = Process.Start(psi))
                {
                    string stderr = await process.StandardError.ReadToEndAsync();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        return (false, $"ffmpeg exited with code {process.ExitCode}: {stderr}");
                    }

                    if (!File.Exists(outputPath))
                    {
                        return (false, "ffmpeg completed but output file not found");
                    }

                    return (true, null);
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Download a video from S3 to local temp file.
        /// </summary>
        public async Task<string> DownloadVideoAsync(string key, CancellationToken cancellationToken = default)
        {
            string extension = Path.GetExtension(key);
            if (string.IsNullOrEmpty(extension))
                extension = ".mp4";

            string localPath = Path.Combine(_tempDirectory, $"{Guid.NewGuid()}{extension}");

            Log.Debug("Downloading {Key} to {Path}", key, localPath);

            var request = new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = key
            };

            using (var response = await _s3Client.GetObjectAsync(request, cancellationToken))
            {
                await response.WriteResponseStreamToFileAsync(localPath, false, cancellationToken);
            }

            return localPath;
        }

        /// <summary>
        /// Upload a thumbnail to S3.
        /// </summary>
        public async Task UploadThumbnailAsync(string localPath, string key, CancellationToken cancellationToken = default)
        {
            Log.Debug("Uploading thumbnail to {Key}", key);

            var request = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                FilePath = localPath,
                ContentType = "image/jpeg"
            };

            await _s3Client.PutObjectAsync(request, cancellationToken);
        }

        /// <summary>
        /// Check if a key exists in S3.
        /// </summary>
        public async Task<bool> KeyExistsAsync(string key, CancellationToken cancellationToken = default)
        {
            try
            {
                var request = new GetObjectMetadataRequest
                {
                    BucketName = _bucketName,
                    Key = key
                };
                await _s3Client.GetObjectMetadataAsync(request, cancellationToken);
                return true;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        /// <summary>
        /// Get metadata for a key.
        /// </summary>
        public async Task<GetObjectMetadataResponse> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
        {
            var request = new GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = key
            };
            return await _s3Client.GetObjectMetadataAsync(request, cancellationToken);
        }

        /// <summary>
        /// Build the expected thumbnail key for a video.
        /// </summary>
        public static string BuildThumbKey(string originalKey)
        {
            if (string.IsNullOrEmpty(originalKey))
                return null;

            int lastSlash = originalKey.LastIndexOf('/');
            if (lastSlash < 0)
                return $"thumb_{RemoveExtension(originalKey)}.jpg";

            string directory = originalKey.Substring(0, lastSlash + 1);
            string filename = originalKey.Substring(lastSlash + 1);
            string nameWithoutExt = RemoveExtension(filename);

            return $"{directory}thumb_{nameWithoutExt}.jpg";
        }

        private static string RemoveExtension(string filename)
        {
            int lastDot = filename.LastIndexOf('.');
            return lastDot > 0 ? filename.Substring(0, lastDot) : filename;
        }

        /// <summary>
        /// Clean up a local file safely.
        /// </summary>
        public void CleanupFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                    Log.Debug("Cleaned up temp file: {Path}", path);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("Failed to cleanup temp file {Path}: {Error}", path, ex.Message);
            }
        }

        public void Dispose()
        {
            // Clean up temp directory if empty
            try
            {
                if (Directory.Exists(_tempDirectory))
                {
                    var files = Directory.GetFiles(_tempDirectory);
                    foreach (var file in files)
                    {
                        try { File.Delete(file); } catch { }
                    }

                    if (Directory.GetFiles(_tempDirectory).Length == 0)
                    {
                        Directory.Delete(_tempDirectory);
                    }
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }
}
