# video-thumbnails-audit

Audit the S3 bucket to identify videos that are missing their corresponding thumbnail images. This is a **read-only** operation that never downloads, generates, uploads, or deletes any objects.

## Usage

```powershell
.\S3FileConverter.exe video-thumbnails-audit [options]
```

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `--prefix <path>` | *(none)* | Filter by S3 key prefix (e.g., a specific GUID folder) |
| `--output <file>` | *(none)* | Save detailed report to JSON file |

## Video Detection

Videos are identified by the following criteria:

1. **Key contains `/video`** (case-insensitive)
2. **Excludes thumbnails**: keys containing `/thumb_` are ignored

## Thumbnail Key Pattern

For a video at:
```
{GUID}/video20240115120530-abc123.mov
```

The expected thumbnail key is:
```
{GUID}/thumb_video20240115120530-abc123.jpg
```

**Pattern**: `{directory}/thumb_{filenameWithoutExtension}.jpg`

## Examples

### Basic audit

```powershell
.\S3FileConverter.exe video-thumbnails-audit
```

### Audit specific folder

```powershell
.\S3FileConverter.exe video-thumbnails-audit --prefix "62FAAEF8-C0B0-4584-B84B-EC81EDBB2CD6/"
```

### Save full report to JSON

```powershell
.\S3FileConverter.exe video-thumbnails-audit --output missing-thumbs.json
```

## Output

### Console Output

```
[15:31:00 INF] === Video Thumbnails Audit (Read-Only) ===
[15:31:00 INF] Bucket: my-bucket
[15:31:00 INF] Region: eu-west-1
[15:31:00 INF]
[15:31:00 INF] This audit is READ-ONLY. No files will be downloaded, generated, uploaded, or deleted.
[15:31:00 INF]
[15:31:01 INF] Starting video thumbnail audit of bucket my-bucket
[15:31:01 INF] Pass 1: Collecting all keys and identifying videos...
[15:31:05 INF] Pass 1 complete. Scanned 23087 objects, found 15 videos.
[15:31:05 INF] Pass 2: Checking thumbnail existence for 15 videos...
[15:31:05 INF] Audit complete. 13 videos have thumbnails, 2 are missing thumbnails.
[15:31:05 INF]
[15:31:05 INF] === Thumbnail Audit Report ===
[15:31:05 INF] Generated at: 01/15/2024 15:31:01
[15:31:05 INF]
[15:31:05 INF] Total objects scanned: 23,087
[15:31:05 INF] Pages scanned: 24
[15:31:05 INF] Scan completed: Yes
[15:31:05 INF]
[15:31:05 INF] Total videos found: 15
[15:31:05 INF]   - With thumbnail: 13
[15:31:05 INF]   - Missing thumbnail: 2
[15:31:05 INF]
[15:31:05 INF] Sample of videos missing thumbnails (first 2):
[15:31:05 INF]   Video: 62FAAEF8-.../video20240911054952-d14b04f3.mov
[15:31:05 INF]     Expected thumb: 62FAAEF8-.../thumb_video20240911054952-d14b04f3.jpg
[15:31:05 INF]     Size: 7.1 MB, Modified: 2024-09-11 12:49:56
```

### JSON Report Structure

```json
{
  "GeneratedAt": "2024-01-15T15:31:01Z",
  "BucketName": "my-bucket",
  "PrefixFilter": null,
  "TotalObjectsScanned": 23087,
  "PagesScanned": 24,
  "ScanCompleted": true,
  "ScanError": null,
  "TotalVideosFound": 15,
  "VideosWithThumbnail": 13,
  "VideosMissingThumbnail": 2,
  "MissingThumbnails": [
    {
      "VideoKey": "62FAAEF8-.../video20240911054952-d14b04f3.mov",
      "ExpectedThumbnailKey": "62FAAEF8-.../thumb_video20240911054952-d14b04f3.jpg",
      "VideoSizeBytes": 7445760,
      "VideoLastModified": "2024-09-11T12:49:56Z",
      "VideoSizeFormatted": "7.1 MB"
    }
  ],
  "DetectionCriteria": "Video detection: key contains '/video'...",
  "Summary": "Scanned 23,087 objects across 24 pages. Found 15 videos: 13 with thumbnail, 2 missing thumbnail."
}
```

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success (scan completed fully) |
| `1` | Partial/incomplete scan or error |

## Workflow

This command is typically used before `thumbnail-generate`:

```powershell
# Step 1: Audit to see what's missing
.\S3FileConverter.exe video-thumbnails-audit --output audit.json

# Step 2: Generate missing thumbnails (dry-run first)
.\S3FileConverter.exe thumbnail-generate

# Step 3: Generate missing thumbnails (apply)
.\S3FileConverter.exe thumbnail-generate --apply

# Step 4: Verify all thumbnails exist
.\S3FileConverter.exe video-thumbnails-audit
```

## Notes

- The audit uses a two-pass algorithm:
  1. **Pass 1**: Collect all S3 keys into memory and identify videos
  2. **Pass 2**: Check each video's expected thumbnail against the key set
- This approach is faster than making individual HEAD requests for each thumbnail
- If the scan is interrupted or fails, `ScanCompleted` will be `false`
- Use `--output` to save the full list when there are many missing thumbnails
