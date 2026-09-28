# inventory

Scan the S3 bucket for files matching specific extensions or patterns. This is a **read-only** operation that never modifies, uploads, or deletes objects.

## Usage

```powershell
.\S3FileConverter.exe inventory [options]
```

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `--types <ext,ext>` | `heic,heif` | Comma-separated file extensions to scan |
| `--prefix <path>` | *(none)* | Filter by S3 key prefix (folder) |
| `--limit <n>` | *(none)* | Maximum number of files to list |
| `--check-magic-bytes` | `false` | Detect HEIC files by reading magic bytes (slower) |
| `--output <file>` | *(none)* | Save report to JSON file |
| `--list-converters` | `false` | Display all available converters |
| `--detect-by-pattern <pattern>` | *(none)* | Detect files by key pattern instead of extension |

## Examples

### Basic scan for HEIC/HEIF files

```powershell
.\S3FileConverter.exe inventory
```

### Scan for specific file types

```powershell
.\S3FileConverter.exe inventory --types png,webp,gif
```

### Scan with prefix filter

```powershell
.\S3FileConverter.exe inventory --types heic --prefix "photos/2024/"
```

### Limited scan for testing

```powershell
.\S3FileConverter.exe inventory --types heic --limit 10
```

### Save report to JSON

```powershell
.\S3FileConverter.exe inventory --types heic --output inventory-report.json
```

### Magic bytes detection

For files that may not have the correct extension:

```powershell
.\S3FileConverter.exe inventory --types heic --check-magic-bytes
```

### List available converters

```powershell
.\S3FileConverter.exe inventory --list-converters
```

### Detect videos by pattern

Find files where the key contains `/video`:

```powershell
.\S3FileConverter.exe inventory --detect-by-pattern "/video"
```

## Output

### Console Output

```
[10:30:15 INF] === S3 File Inventory (Read-Only) ===
[10:30:15 INF] Bucket: my-bucket
[10:30:15 INF] Region: eu-west-1
[10:30:15 INF] File types: heic, heif
[10:30:15 INF]
[10:30:15 INF] This mode is READ-ONLY. No files will be modified, uploaded, or deleted.
[10:30:18 INF]
[10:30:18 INF] === Inventory Report ===
[10:30:18 INF] Total files found: 150
[10:30:18 INF]   - Detected by extension: 150
[10:30:18 INF]   - Detected by magic bytes: 0
[10:30:18 INF]
[10:30:18 INF] Total size: 1.2 GB
[10:30:18 INF] Pages scanned: 5
[10:30:18 INF] Scan completed: Yes
```

### JSON Report Structure

```json
{
  "GeneratedAt": "2024-01-15T10:30:18Z",
  "BucketName": "my-bucket",
  "PrefixFilter": null,
  "LimitApplied": null,
  "TargetExtensions": ["heic", "heif"],
  "TotalFiles": 150,
  "TotalSizeBytes": 1288490188,
  "TotalSizeFormatted": "1.2 GB",
  "DetectedByExtension": 150,
  "DetectedByMagicBytes": 0,
  "PagesScanned": 5,
  "ScanCompleted": true,
  "SampleKeys": [
    "photos/image001.heic",
    "photos/image002.heif"
  ],
  "DetectionCriteria": "Files detected by extension match..."
}
```

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | Error (invalid options, AWS error, etc.) |

## Notes

- Extensions are case-insensitive
- The `--check-magic-bytes` option only works for HEIC/HEIF files
- Pattern detection (`--detect-by-pattern`) excludes thumbnails automatically
