# thumbnail-generate

Generate missing video thumbnails and upload them to S3. Supports two modes: **general** (process all missing) and **individual** (process single video).

## Requirements

- **ffmpeg** and **ffprobe** must be installed and accessible (in PATH or same folder as executable)

## Usage

```powershell
.\S3FileConverter.exe thumbnail-generate [options]
```

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `--key <s3-key>` | *(none)* | Process a single video (individual mode) |
| `--prefix <path>` | *(none)* | Filter by S3 key prefix (general mode only) |
| `--limit <n>` | *(none)* | Maximum videos to process (general mode only) |
| `--apply` | `false` | Actually upload thumbnails (default: dry-run) |
| `--output <file>` | *(none)* | Save detailed report to JSON file |

## Modes

### General Mode (default)

Without `--key`, the command scans the entire bucket for videos missing thumbnails and processes them all.

```powershell
# Dry-run: see what would be generated
.\S3FileConverter.exe thumbnail-generate

# Apply: actually generate and upload
.\S3FileConverter.exe thumbnail-generate --apply
```

### Individual Mode

With `--key`, the command processes only the specified video.

```powershell
# Dry-run single video
.\S3FileConverter.exe thumbnail-generate --key "GUID/video20240911054952-xyz.mov"

# Apply single video
.\S3FileConverter.exe thumbnail-generate --key "GUID/video20240911054952-xyz.mov" --apply
```

## Examples

### Dry-run all missing thumbnails

```powershell
.\S3FileConverter.exe thumbnail-generate
```

### Generate all missing thumbnails

```powershell
.\S3FileConverter.exe thumbnail-generate --apply
```

### Limit to first 5 videos

```powershell
.\S3FileConverter.exe thumbnail-generate --limit 5 --apply
```

### Filter by prefix

```powershell
.\S3FileConverter.exe thumbnail-generate --prefix "62FAAEF8-C0B0-4584-B84B-EC81EDBB2CD6/" --apply
```

### Process single video (dry-run)

```powershell
.\S3FileConverter.exe thumbnail-generate --key "62FAAEF8-C0B0-4584-B84B-EC81EDBB2CD6/video20240911054952-d14b04f3-88be-458f-bc26-7127b477af82.mov"
```

### Process single video (apply)

```powershell
.\S3FileConverter.exe thumbnail-generate --key "62FAAEF8-C0B0-4584-B84B-EC81EDBB2CD6/video20240911054952-d14b04f3-88be-458f-bc26-7127b477af82.mov" --apply
```

### Save report to JSON

```powershell
.\S3FileConverter.exe thumbnail-generate --apply --output generation-report.json
```

## Thumbnail Generation Specifications

| Setting | Value |
|---------|-------|
| **Format** | JPEG |
| **Max dimension** | 320px (longest side) |
| **Quality** | ~70 (ffmpeg `-q:v 2`) |
| **Frame capture** | 1.5 seconds (or 50% for videos < 2s) |
| **Aspect ratio** | Preserved |

### Thumbnail Key Pattern

```
Input:  {GUID}/video20240115120530-abc123.mov
Output: {GUID}/thumb_video20240115120530-abc123.jpg
```

## Output

### Console Output (Dry-Run)

```
[15:51:11 INF] === Video Thumbnail Generator ==="
[15:51:11 INF] Bucket: my-bucket
[15:51:11 INF] Region: eu-west-1
[15:51:11 INF] Mode: Individual (single video)
[15:51:11 INF] Operation: DRY RUN (simulation)
[15:51:11 INF]
[15:51:11 INF] Target video: GUID/video20240911054952-xyz.mov
[15:51:11 INF]
[15:51:12 INF] ffmpeg: ffmpeg version n6.0...
[15:51:12 INF] ffprobe: ffprobe version n6.0...
[15:51:12 INF]
[15:51:12 INF] Checking if video exists: GUID/video20240911054952-xyz.mov
[15:51:13 INF] Video size: 7.1 MB
[15:51:13 INF] Checking for existing thumbnail: GUID/thumb_video20240911054952-xyz.jpg
[15:51:13 INF] Thumbnail missing, will generate
[15:51:13 INF]   Downloading video...
[15:51:16 INF]   Duration: 12.5s
[15:51:16 INF]   Extracting frame...
[15:51:16 INF]   Generated thumbnail: 10248 bytes
[15:51:16 INF]   [DRY RUN] Would upload to: GUID/thumb_video20240911054952-xyz.jpg
[15:51:16 INF]
[15:51:16 INF] === Summary ===
[15:51:16 INF] [DRY RUN] Processed 1 videos: 1 generated, 0 already exist, 0 failed. Duration: 5.3s
```

### Console Output (Apply)

```
[15:52:00 INF] Operation: APPLY (will upload)
...
[15:52:10 INF]   Uploading to: GUID/thumb_video20240911054952-xyz.jpg
[15:52:11 INF]   Upload complete
...
[15:52:11 INF] Processed 1 videos: 1 generated, 0 already exist, 0 failed. Duration: 11.2s
```

### JSON Report Structure

```json
{
  "GeneratedAt": "2024-01-15T15:52:11Z",
  "BucketName": "my-bucket",
  "PrefixFilter": null,
  "DryRun": false,
  "Mode": "individual",
  "SingleVideoKey": "GUID/video20240911054952-xyz.mov",
  "TotalVideosProcessed": 1,
  "ThumbnailsGenerated": 1,
  "ThumbnailsAlreadyExist": 0,
  "ThumbnailsFailed": 0,
  "TotalDuration": "00:00:11.2",
  "Operations": [
    {
      "VideoKey": "GUID/video20240911054952-xyz.mov",
      "ThumbnailKey": "GUID/thumb_video20240911054952-xyz.jpg",
      "Status": "generated",
      "ErrorMessage": null,
      "VideoSizeBytes": 7445760,
      "VideoDurationSeconds": 12.5,
      "ProcessingTimeMs": 11200,
      "VideoSizeFormatted": "7.1 MB"
    }
  ],
  "Summary": "Processed 1 videos: 1 generated, 0 already exist, 0 failed. Duration: 11.2s"
}
```

## Operation Statuses

| Status | Description |
|--------|-------------|
| `generated` | Thumbnail created (and uploaded if `--apply`) |
| `exists` | Thumbnail already exists, skipped |
| `failed` | Error during processing |
| `skipped` | Video doesn't exist or other skip reason |

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success (no failures) |
| `1` | One or more thumbnails failed |

## Workflow

```powershell
# 1. Audit to see what's missing
.\S3FileConverter.exe video-thumbnails-audit

# 2. Test with dry-run
.\S3FileConverter.exe thumbnail-generate

# 3. Test with single video
.\S3FileConverter.exe thumbnail-generate --key "GUID/video.mov" --apply

# 4. Generate all missing
.\S3FileConverter.exe thumbnail-generate --apply

# 5. Verify
.\S3FileConverter.exe video-thumbnails-audit
```

## Notes

- **Dry-run is the default** - use `--apply` to actually upload
- Videos are processed in their original format (MOV, MP4, etc.) - never converted
- Existing thumbnails are **never overwritten**
- Temporary files are automatically cleaned up after processing
- If ffmpeg/ffprobe are not found, the command will fail with an error message
