# convert

Convert files from one format to another in the S3 bucket. Supports batch conversion with safety features including dry-run mode, metadata tracking, and verification before deletion.

## Usage

```powershell
.\S3FileConverter.exe convert [options]
```

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `--from <ext,ext>` | `heic,heif` | Source file extensions to convert |
| `--to <ext>` | `jpg` | Target format extension |
| `--prefix <path>` | *(none)* | Filter by S3 key prefix (folder) |
| `--limit <n>` | *(none)* | Maximum number of files to process |
| `--enable-writes` | `false` | **Required** to perform actual conversions |
| `--dry-run` | `true` | Simulate without changes (default behavior) |
| `--output <file>` | `conversion-report.json` | Save report to JSON file |

## Supported Conversions

| Source | Target | Converter ID |
|--------|--------|--------------|
| HEIC, HEIF | JPEG | `heic-to-jpeg` |

## Examples

### Dry-run (simulation)

```powershell
.\S3FileConverter.exe convert --from heic,heif --to jpg
```

### Dry-run with limit

```powershell
.\S3FileConverter.exe convert --from heic --to jpg --limit 5
```

### Execute actual conversion

```powershell
.\S3FileConverter.exe convert --from heic,heif --to jpg --enable-writes
```

### Convert with prefix filter

```powershell
.\S3FileConverter.exe convert --from heic --to jpg --enable-writes --prefix "photos/2024/"
```

### Custom output report

```powershell
.\S3FileConverter.exe convert --from heic --to jpg --enable-writes --output my-report.json
```

## Safety Features

### 1. Dry-Run by Default

Without `--enable-writes`, the command simulates the conversion and shows what would happen without making any changes.

### 2. Metadata Tracking

Every converted file is tagged with S3 metadata:

| Metadata Key | Description |
|--------------|-------------|
| `x-amz-meta-migration-source` | Original file key |
| `x-amz-meta-migration-timestamp` | Conversion timestamp |
| `x-amz-meta-migration-version` | Tool version |
| `x-amz-meta-converter-type` | Converter ID used |

### 3. Verification Before Deletion

Source files are only deleted after:
- Output file is successfully uploaded
- Output file size is validated
- Migration metadata is verified
- Output file passes format validation

### 4. Interruption Recovery

If conversion is interrupted:
- Detects partially completed conversions via metadata
- Resumes safely without duplicating work
- Marks ambiguous cases for manual review

### 5. Manual Review

Files with ambiguous state are flagged for manual review and **never** automatically deleted.

## Output

### Console Output

```
[10:30:15 INF] === S3 File Converter ===
[10:30:15 INF] Mode: DRY RUN (no changes will be made)
[10:30:15 INF] Converting: heic, heif -> jpg
[10:30:15 INF]
[10:30:18 INF] Processing file 1 of 10: photos/image001.heic
[10:30:19 INF]   Would convert to: photos/image001.jpg
[10:30:19 INF]   Size: 2.5 MB -> estimated 800 KB
...
[10:30:25 INF]
[10:30:25 INF] === Summary ===
[10:30:25 INF] Files processed: 10
[10:30:25 INF] Would convert: 10
[10:30:25 INF] Already converted: 0
[10:30:25 INF] Errors: 0
```

### JSON Report Structure

```json
{
  "GeneratedAt": "2024-01-15T10:30:25Z",
  "Mode": "dry-run",
  "SourceExtensions": ["heic", "heif"],
  "TargetExtension": "jpg",
  "TotalProcessed": 10,
  "Converted": 10,
  "AlreadyConverted": 0,
  "Failed": 0,
  "Results": [
    {
      "SourceKey": "photos/image001.heic",
      "TargetKey": "photos/image001.jpg",
      "Status": "would-convert",
      "SourceSizeBytes": 2621440,
      "TargetSizeBytes": null
    }
  ]
}
```

## Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success (all conversions completed) |
| `1` | Error (some conversions failed) |

## Notes

- Always run without `--enable-writes` first to preview changes
- The tool uses ImageMagick (Magick.NET) for image conversion
- Original files are deleted only after successful conversion and verification
- Logs are written to `logs/s3-converter-{date}.log`
