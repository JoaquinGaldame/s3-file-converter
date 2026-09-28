using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using S3FileConverter.Models;
using Serilog;

namespace S3FileConverter.Services
{
    /// <summary>
    /// Scans S3 bucket for files matching specified extensions. Read-only operations only.
    /// Generalized from original HEIC-only implementation.
    /// </summary>
    public class S3InventoryService
    {
        private readonly IAmazonS3 _s3Client;
        private readonly string _bucketName;

        // ISO-BMFF brand codes that indicate HEIC/HEIF/AVIF (for magic byte detection)
        private static readonly HashSet<string> HeicBrands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "heic", "heix", "hevc", "hevx", "heim", "heis",
            "hevm", "hevs", "mif1", "msf1", "avif", "avis"
        };

        public S3InventoryService(IAmazonS3 s3Client, string bucketName)
        {
            _s3Client = s3Client ?? throw new ArgumentNullException(nameof(s3Client));
            _bucketName = bucketName ?? throw new ArgumentNullException(nameof(bucketName));
        }

        /// <summary>
        /// Scan bucket for files matching specified extensions.
        /// </summary>
        /// <param name="targetExtensions">Extensions to look for (lowercase, without dot). Example: ["heic", "heif", "png"]</param>
        /// <param name="prefix">Optional prefix to filter (e.g., a specific folder)</param>
        /// <param name="limit">Optional limit on number of files to return</param>
        /// <param name="checkMagicBytes">If true, also check files without matching extension by reading magic bytes (slower, HEIC only for now)</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<InventoryReport> ScanForFilesAsync(
            IEnumerable<string> targetExtensions,
            string prefix = null,
            int? limit = null,
            bool checkMagicBytes = false,
            CancellationToken cancellationToken = default)
        {
            var extensionSet = new HashSet<string>(
                targetExtensions.Select(e => e.ToLowerInvariant().TrimStart('.')),
                StringComparer.OrdinalIgnoreCase);

            var report = new InventoryReport
            {
                BucketName = _bucketName,
                PrefixFilter = prefix,
                LimitApplied = limit,
                TargetExtensions = extensionSet.ToList()
            };

            var files = new List<SourceFileInfo>();
            string continuationToken = null;
            int pagesScanned = 0;

            Log.Information("Starting inventory scan of bucket {Bucket} with prefix '{Prefix}' for extensions: {Extensions}",
                _bucketName, prefix ?? "(none)", string.Join(", ", extensionSet));

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new ListObjectsV2Request
                {
                    BucketName = _bucketName,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                };

                var response = await _s3Client.ListObjectsV2Async(request, cancellationToken);
                pagesScanned++;

                foreach (var obj in response.S3Objects)
                {
                    // Skip thumbnails
                    if (obj.Key.Contains("/thumb_"))
                        continue;

                    // Skip directories (keys ending with /)
                    if (obj.Key.EndsWith("/"))
                        continue;

                    SourceFileInfo fileInfo = null;
                    string extension = SourceFileInfo.GetExtension(obj.Key);

                    // Method 1: Check by extension (fast)
                    if (extension != null && extensionSet.Contains(extension))
                    {
                        fileInfo = new SourceFileInfo
                        {
                            Key = obj.Key,
                            SizeBytes = obj.Size,
                            LastModified = obj.LastModified,
                            Extension = extension,
                            DetectionMethod = "extension"
                        };
                        report.DetectedByExtension++;
                    }
                    // Method 2: Check by magic bytes (slow, optional, HEIC only for now)
                    else if (checkMagicBytes && extensionSet.Any(e => e == "heic" || e == "heif"))
                    {
                        if (await IsHeicByMagicBytesAsync(obj.Key, cancellationToken))
                        {
                            fileInfo = new SourceFileInfo
                            {
                                Key = obj.Key,
                                SizeBytes = obj.Size,
                                LastModified = obj.LastModified,
                                Extension = "heic",
                                DetectionMethod = "magic-bytes"
                            };
                            report.DetectedByMagicBytes++;
                        }
                    }

                    if (fileInfo != null)
                    {
                        files.Add(fileInfo);
                        report.TotalSizeBytes += fileInfo.SizeBytes;

                        if (report.SampleKeys.Count < 10)
                        {
                            report.SampleKeys.Add(fileInfo.Key);
                        }

                        // Check limit
                        if (limit.HasValue && files.Count >= limit.Value)
                        {
                            Log.Information("Reached limit of {Limit} files", limit.Value);
                            report.TotalFiles = files.Count;
                            report.PagesScanned = pagesScanned;
                            report.ScanCompleted = false;
                            return report;
                        }
                    }
                }

                continuationToken = response.NextContinuationToken;

                if (pagesScanned % 10 == 0)
                {
                    Log.Information("Scanned {Pages} pages, found {Count} matching files so far...",
                        pagesScanned, files.Count);
                }

            } while (!string.IsNullOrEmpty(continuationToken));

            report.TotalFiles = files.Count;
            report.PagesScanned = pagesScanned;
            report.ScanCompleted = true;

            Log.Information("Inventory scan complete. Found {Count} files across {Pages} pages",
                files.Count, pagesScanned);

            return report;
        }

        /// <summary>
        /// Get list of files matching extensions for conversion processing.
        /// </summary>
        public async Task<List<SourceFileInfo>> GetFilesAsync(
            IEnumerable<string> targetExtensions,
            string prefix = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            var extensionSet = new HashSet<string>(
                targetExtensions.Select(e => e.ToLowerInvariant().TrimStart('.')),
                StringComparer.OrdinalIgnoreCase);

            var files = new List<SourceFileInfo>();
            string continuationToken = null;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new ListObjectsV2Request
                {
                    BucketName = _bucketName,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                };

                var response = await _s3Client.ListObjectsV2Async(request, cancellationToken);

                foreach (var obj in response.S3Objects)
                {
                    if (obj.Key.Contains("/thumb_") || obj.Key.EndsWith("/"))
                        continue;

                    string extension = SourceFileInfo.GetExtension(obj.Key);
                    if (extension != null && extensionSet.Contains(extension))
                    {
                        files.Add(new SourceFileInfo
                        {
                            Key = obj.Key,
                            SizeBytes = obj.Size,
                            LastModified = obj.LastModified,
                            Extension = extension,
                            DetectionMethod = "extension"
                        });

                        if (limit.HasValue && files.Count >= limit.Value)
                            return files;
                    }
                }

                continuationToken = response.NextContinuationToken;

            } while (!string.IsNullOrEmpty(continuationToken));

            return files;
        }

        /// <summary>
        /// Check if a specific key exists in S3.
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
        /// Get metadata for a specific key.
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

        private async Task<bool> IsHeicByMagicBytesAsync(string key, CancellationToken cancellationToken)
        {
            try
            {
                // Only read first 12 bytes to check ISO-BMFF header
                var request = new GetObjectRequest
                {
                    BucketName = _bucketName,
                    Key = key,
                    ByteRange = new ByteRange(0, 11)
                };

                using (var response = await _s3Client.GetObjectAsync(request, cancellationToken))
                using (var stream = response.ResponseStream)
                {
                    var header = new byte[12];
                    int bytesRead = await stream.ReadAsync(header, 0, 12, cancellationToken);

                    if (bytesRead < 12)
                        return false;

                    // ISO-BMFF signature: bytes 4-7 must be "ftyp"
                    string ftyp = Encoding.ASCII.GetString(header, 4, 4);
                    if (ftyp != "ftyp")
                        return false;

                    // Bytes 8-11 contain the brand
                    string brand = Encoding.ASCII.GetString(header, 8, 4);
                    return HeicBrands.Contains(brand);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Failed to check magic bytes for {Key}: {Error}", key, ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Audit videos for missing thumbnails.
        /// Replicates the API's video detection ("/video" in key) and BuildThumbKey logic.
        /// </summary>
        /// <param name="prefix">Optional prefix to filter</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<ThumbnailAuditReport> AuditVideoThumbnailsAsync(
            string prefix = null,
            CancellationToken cancellationToken = default)
        {
            var report = new ThumbnailAuditReport
            {
                BucketName = _bucketName,
                PrefixFilter = prefix,
                DetectionCriteria = "Video detection: key contains '/video' (case-insensitive), excludes keys with '/thumb_'.\n" +
                    "Thumbnail key: {directory}/thumb_{filenameWithoutExtension}.jpg"
            };

            // First pass: collect all keys into memory for cross-referencing
            var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var videoKeys = new List<(string Key, long Size, DateTime LastModified)>();

            string continuationToken = null;
            int pagesScanned = 0;

            Log.Information("Starting video thumbnail audit of bucket {Bucket}", _bucketName);
            Log.Information("Pass 1: Collecting all keys and identifying videos...");

            try
            {
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var request = new ListObjectsV2Request
                    {
                        BucketName = _bucketName,
                        Prefix = prefix,
                        ContinuationToken = continuationToken
                    };

                    var response = await _s3Client.ListObjectsV2Async(request, cancellationToken);
                    pagesScanned++;

                    foreach (var obj in response.S3Objects)
                    {
                        report.TotalObjectsScanned++;

                        // Skip directories
                        if (obj.Key.EndsWith("/"))
                            continue;

                        // Add to all keys set for thumbnail lookup
                        allKeys.Add(obj.Key);

                        string lowerKey = obj.Key.ToLowerInvariant();

                        // Skip thumbnails - we're looking for source videos
                        if (lowerKey.Contains("/thumb_"))
                            continue;

                        // Video detection: key contains "/video" (matching API logic)
                        if (lowerKey.Contains("/video"))
                        {
                            videoKeys.Add((obj.Key, obj.Size, obj.LastModified));
                        }
                    }

                    continuationToken = response.NextContinuationToken;

                    if (pagesScanned % 10 == 0)
                    {
                        Log.Information("Scanned {Pages} pages, {Total} objects, found {Videos} videos so far...",
                            pagesScanned, report.TotalObjectsScanned, videoKeys.Count);
                    }

                } while (!string.IsNullOrEmpty(continuationToken));

                report.PagesScanned = pagesScanned;
                report.TotalVideosFound = videoKeys.Count;
                report.ScanCompleted = true;

                Log.Information("Pass 1 complete. Scanned {Total} objects, found {Videos} videos.",
                    report.TotalObjectsScanned, videoKeys.Count);

                // Second pass: check each video for its thumbnail
                Log.Information("Pass 2: Checking thumbnail existence for {Count} videos...", videoKeys.Count);

                foreach (var video in videoKeys)
                {
                    string expectedThumbKey = BuildThumbKey(video.Key);

                    if (allKeys.Contains(expectedThumbKey))
                    {
                        report.VideosWithThumbnail++;
                    }
                    else
                    {
                        report.VideosMissingThumbnail++;
                        report.MissingThumbnails.Add(new MissingThumbnailEntry
                        {
                            VideoKey = video.Key,
                            ExpectedThumbnailKey = expectedThumbKey,
                            VideoSizeBytes = video.Size,
                            VideoLastModified = video.LastModified
                        });
                    }
                }

                Log.Information("Audit complete. {With} videos have thumbnails, {Without} are missing thumbnails.",
                    report.VideosWithThumbnail, report.VideosMissingThumbnail);

                return report;
            }
            catch (OperationCanceledException)
            {
                report.ScanCompleted = false;
                report.ScanError = "Operation cancelled by user";
                throw;
            }
            catch (AmazonS3Exception ex)
            {
                report.ScanCompleted = false;
                report.ScanError = $"AWS S3 error: {ex.Message}";
                report.PagesScanned = pagesScanned;
                throw;
            }
            catch (Exception ex)
            {
                report.ScanCompleted = false;
                report.ScanError = $"Unexpected error: {ex.Message}";
                report.PagesScanned = pagesScanned;
                throw;
            }
        }

        /// <summary>
        /// Build the expected thumbnail key for a video, replicating API logic.
        /// Example: "GUID/video20250928120000-xyz.mp4" -> "GUID/thumb_video20250928120000-xyz.jpg"
        /// </summary>
        private static string BuildThumbKey(string originalKey)
        {
            if (string.IsNullOrEmpty(originalKey))
                return null;

            // Separate directory and filename
            int lastSlash = originalKey.LastIndexOf('/');
            if (lastSlash < 0)
                return $"thumb_{RemoveExtension(originalKey)}.jpg";

            string directory = originalKey.Substring(0, lastSlash + 1);
            string filename = originalKey.Substring(lastSlash + 1);

            // Remove extension and build thumb key
            string nameWithoutExt = RemoveExtension(filename);
            return $"{directory}thumb_{nameWithoutExt}.jpg";
        }

        private static string RemoveExtension(string filename)
        {
            int lastDot = filename.LastIndexOf('.');
            return lastDot > 0 ? filename.Substring(0, lastDot) : filename;
        }

        /// <summary>
        /// Scan bucket for videos by key pattern (e.g., "/video" in key).
        /// This is for diagnostics when extension-based detection misses files.
        /// Excludes thumbnails (thumb_video*).
        /// </summary>
        /// <param name="pattern">Pattern to search for in key (e.g., "/video")</param>
        /// <param name="prefix">Optional prefix to filter</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public async Task<VideoPatternReport> ScanByPatternAsync(
            string pattern,
            string prefix = null,
            CancellationToken cancellationToken = default)
        {
            var report = new VideoPatternReport
            {
                BucketName = _bucketName,
                PrefixFilter = prefix,
                PatternUsed = pattern,
                DetectionCriteria = $"Files are identified by:\n" +
                    $"1. Key contains pattern: '{pattern}' (case-insensitive)\n" +
                    $"2. Excludes thumbnails: keys containing 'thumb_video'\n" +
                    $"3. Excludes directories: keys ending with '/'"
            };

            string continuationToken = null;
            int pagesScanned = 0;

            Log.Information("Starting pattern-based scan of bucket {Bucket} for pattern '{Pattern}'",
                _bucketName, pattern);

            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new ListObjectsV2Request
                {
                    BucketName = _bucketName,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                };

                var response = await _s3Client.ListObjectsV2Async(request, cancellationToken);
                pagesScanned++;

                int videosThisPage = 0;

                foreach (var obj in response.S3Objects)
                {
                    report.TotalObjectsScanned++;

                    // Skip directories
                    if (obj.Key.EndsWith("/"))
                        continue;

                    string lowerKey = obj.Key.ToLowerInvariant();

                    // Skip thumbnails (thumb_video*)
                    if (lowerKey.Contains("/thumb_video") || lowerKey.Contains("\\thumb_video"))
                    {
                        report.ThumbnailsExcluded++;
                        continue;
                    }

                    // Check if key matches pattern
                    if (lowerKey.Contains(pattern.ToLowerInvariant()))
                    {
                        videosThisPage++;
                        report.TotalVideosDetected++;
                        report.TotalSizeBytes += obj.Size;

                        // Track extension distribution
                        string extension = SourceFileInfo.GetExtension(obj.Key) ?? "(none)";
                        if (report.ExtensionDistribution.ContainsKey(extension))
                            report.ExtensionDistribution[extension]++;
                        else
                            report.ExtensionDistribution[extension] = 1;

                        // Collect sample keys (first 20)
                        if (report.SampleKeys.Count < 20)
                        {
                            report.SampleKeys.Add(obj.Key);
                        }
                    }
                }

                report.VideosPerPage.Add(videosThisPage);
                continuationToken = response.NextContinuationToken;

                if (pagesScanned % 10 == 0)
                {
                    Log.Information("Scanned {Pages} pages, {Total} objects, found {Videos} videos so far...",
                        pagesScanned, report.TotalObjectsScanned, report.TotalVideosDetected);
                }

            } while (!string.IsNullOrEmpty(continuationToken));

            report.PagesScanned = pagesScanned;
            report.ScanCompleted = true;

            Log.Information("Pattern scan complete. Scanned {Total} objects, found {Videos} videos across {Pages} pages",
                report.TotalObjectsScanned, report.TotalVideosDetected, pagesScanned);

            return report;
        }
    }
}
