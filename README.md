# S3 File Converter

Command-line toolkit for managing and converting files in Amazon S3 buckets. Features batch file conversion, video thumbnail auditing, and thumbnail generation.

## Features

- **File Conversion**: Batch convert files between formats (HEIC/HEIF to JPEG)
- **Video Thumbnail Audit**: Identify videos missing their thumbnail images
- **Thumbnail Generation**: Generate missing thumbnails using ffmpeg
- **Safe Operations**: Dry-run mode by default, metadata tracking, verification before deletion
- **Extensible Architecture**: Strategy Pattern allows easy addition of new converters

## Requirements

- .NET Framework 4.8
- AWS credentials with S3 read/write access
- ffmpeg/ffprobe (for thumbnail generation only)

## Quick Start

```powershell
# Build
dotnet build

# Navigate to output
cd bin\Debug\net48

# View help
.\S3FileConverter.exe --help
```

## Configuration

Edit `S3FileConverter.exe.config`:

```xml
<appSettings>
  <add key="AWS:AccessKeyId" value="YOUR_ACCESS_KEY" />
  <add key="AWS:SecretAccessKey" value="YOUR_SECRET_KEY" />
  <add key="AWS:Region" value="eu-west-1" />
  <add key="S3:BucketName" value="your-bucket-name" />
</appSettings>
```

## Commands

| Command | Description | Documentation |
|---------|-------------|---------------|
| `inventory` | Scan bucket for files by type | [docs/inventory.md](docs/inventory.md) |
| `convert` | Convert files between formats | [docs/convert.md](docs/convert.md) |
| `video-thumbnails-audit` | Find videos missing thumbnails | [docs/video-thumbnails-audit.md](docs/video-thumbnails-audit.md) |
| `thumbnail-generate` | Generate missing video thumbnails | [docs/thumbnail-generate.md](docs/thumbnail-generate.md) |

## Quick Examples

```powershell
# Inventory: scan for HEIC files
.\S3FileConverter.exe inventory --types heic,heif

# Convert: HEIC to JPEG (dry-run)
.\S3FileConverter.exe convert --from heic --to jpg

# Audit: find videos without thumbnails
.\S3FileConverter.exe video-thumbnails-audit

# Generate: create missing thumbnails (dry-run)
.\S3FileConverter.exe thumbnail-generate

# Generate: create missing thumbnails (apply)
.\S3FileConverter.exe thumbnail-generate --apply
```

## Project Structure

```
S3FileConverter/
├── Commands/           # CLI command implementations
├── Configuration/      # AWS/S3 configuration
├── Converters/         # File format converters (Strategy Pattern)
├── Models/             # Data models and reports
├── Services/           # Business logic services
├── docs/               # Command documentation
└── logs/               # Runtime logs (rolling daily)
```

## Logs

- Console: real-time output
- File: `logs/s3-converter-{date}.log`

## FFmpeg

  The `thumbnail-generate` command requires ffmpeg and ffprobe. Pre-built binaries are included in `ffmpeg.zip` at the repository root. Extract `ffmpeg.exe` and `ffprobe.exe` to `bin\Debug\net48\` (or `bin\Release\net48\` for release builds) alongside `S3FileConverter.exe`, or add them to your system PATH.

## License

Private and proprietary software. Copyright © 2026 Joaquin Galdame. All rights reserved. See [LICENSE](LICENSE).