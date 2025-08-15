# HandBrake Scheduler

A .NET service that automatically monitors directories for video files and transcodes them using HandBrakeCLI with configurable scheduling, time windows, network support, and REST API for file uploads. Features both directory monitoring and web-based file upload capabilities with advanced job management.

## Integration with AutoMk

HandbrakeScheduler integrates seamlessly with [AutoMk](https://github.com/timothydodd/auto-mk) to create an automated disc-to-library pipeline:

1. **AutoMk** automatically rips Blu-ray/DVD discs using MakeMKV
2. **HandbrakeScheduler** receives and transcodes the ripped MKV files

This integration enables hands-free processing from physical media to optimized video files ready for your Plex/media server.

## Features

### Core Processing
-  **Automatic Monitoring**: Continuously monitors configured directories for new video files
-  **Web API Upload**: REST API endpoints for file uploads with rich metadata support
-  **Time-Based Scheduling**: Optional time windows for processing (e.g., only during off-hours)
-  **Advanced Job Queue**: Persistent job queue with retry logic (up to 3 attempts) and status tracking
-  **Smart Temp File Management**: Copies remote files locally for faster processing when space allows
-  **Progress Tracking**: Real-time progress bars for file copying and transcoding operations

### File Management
-  **Multi-Directory Support**: Monitor multiple input/output directory pairs simultaneously
-  **Flexible File Filtering**: Configurable file extensions, size limits, and recursive scanning
-  **Network Share Support**: Works with UNC paths and network drives with credential management
-  **Source File Management**: Optional deletion of source files after successful transcoding
-  **Cross-Platform Path Handling**: Intelligent path normalization for Windows/Unix compatibility

### API & Integration
-  **REST API Endpoints**: File upload, job monitoring, and system control via HTTP
-  **Rich Media Metadata**: Support for series/movie information, IMDB IDs, genres, and custom fields
-  **Health Checks**: Built-in health monitoring endpoints
-  **Large File Support**: Handles files up to 100GB via web upload

### Monitoring & Logging
-  **Comprehensive Logging**: Console and file logging with configurable levels
-  **Job Persistence**: Jobs survive application restarts with automatic recovery
-  **Graceful Shutdown**: Proper cleanup of processes and temporary files on exit

## Prerequisites

- .NET 9.0 or later
- HandBrakeCLI executable
- Sufficient disk space for temporary files (when processing remote files)
- Network access for file upload functionality (if using web API)

## Installation

1. **Clone the repository**
   ```bash
   git clone https://github.com/timothydodd/handbrake-scheduler.git
   cd handbrake-scheduler
   ```

2. **Build the project**
   ```bash
   dotnet build --configuration Release
   ```

3. **Download HandBrakeCLI**
   - Download from [HandBrake's official website](https://handbrake.fr/downloads2.php)
   - Extract and note the path to `HandBrakeCLI` executable

## Configuration

Create an `appsettings.json` file in the application directory:

```json
{
  "HandBrake": {
    "HandBrakeCliPath": "/path/to/HandBrakeCLI",
    "MonitoringEnabled": true,
    "ScanIntervalMinutes": 10,
    "EnableLogging": true,
    "LogFilePath": "logs/handbrake.log",
    "StartTime": "22:00:00",
    "EndTime": "06:00:00",
    "Username": "domain\\username",
    "Password": "password",
    "PersistencePath": "data",
    "DefaultPreset": "HQ 1080p30 Surround",
    "Folders": [
      {
        "InputPath": "/input/videos",
        "OutputPath": "/output/transcoded",
        "Preset": "Fast 1080p30",
        "FileExtensions": [".mkv", ".avi", ".mp4", ".mov"],
        "DeleteSource": false,
        "UseTempFolder": true,
        "RecursiveSearch": true,
        "MaxFileSizeBytes": 21474836480,
        "MinFileSizeBytes": 104857600
      }
    ]
  },
  "FileTransfer": {
    "IncomingDirectory": "incoming",
    "MaxFileSizeBytes": 107374182400,
    "MaxConcurrentProcessing": 2,
    "ListenUrl": "http://localhost:5000"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "HandbrakeScheduler": "Debug"
    }
  }
}
```

### Configuration Options

#### HandBrake Settings
- **`HandBrakeCliPath`**: Path to HandBrakeCLI executable (required)
- **`MonitoringEnabled`**: Enable continuous monitoring (default: true)
- **`ScanIntervalMinutes`**: How often to scan for new files (default: 10)
- **`StartTime`/`EndTime`**: Optional time window for processing (24-hour format)
- **`Username`/`Password`**: Credentials for network shares
- **`EnableLogging`**: Enable file logging (default: true)
- **`LogFilePath`**: Custom log file path
- **`PersistencePath`**: Directory for job queue persistence (default: "data")
- **`DefaultPreset`**: Default HandBrake preset for uploaded files

#### Folder Settings
- **`InputPath`**: Directory to monitor for video files (required)
- **`OutputPath`**: Directory for transcoded files (required)
- **`Preset`**: HandBrake preset name (required)
- **`FileExtensions`**: Array of file extensions to process
- **`DeleteSource`**: Delete original files after successful transcoding
- **`UseTempFolder`**: Copy remote files to local temp for processing
- **`RecursiveSearch`**: Search subdirectories (default: true)
- **`MaxFileSizeBytes`**: Maximum file size to process
- **`MinFileSizeBytes`**: Minimum file size to process

#### FileTransfer Settings
- **`IncomingDirectory`**: Directory for uploaded files (default: "incoming")
- **`MaxFileSizeBytes`**: Maximum upload file size in bytes (default: 100GB)
- **`MaxConcurrentProcessing`**: Number of concurrent processing jobs (default: 2)
- **`ListenUrl`**: Web API listening URL (default: "http://localhost:5000")

## Usage

### Running the Application

**Development:**
```bash
dotnet run
```

**Production:**
```bash
dotnet HandbrakeScheduler.dll
```

### Integration with AutoMk

When receiving files from AutoMk, HandbrakeScheduler:

1. **Preserves Folder Structure**: Uses the relative file path from AutoMk to maintain organization
2. **Routes by Media Type**: Automatically selects appropriate HandBrake presets based on content type (Movie/TV)
3. **Maintains Metadata**: Preserves movie titles, series information, and episode details

To receive files from AutoMk:

1. Ensure HandbrakeScheduler is running on the configured port (default: 5000)
2. Configure folder settings in `appsettings.json` with appropriate presets for Movies and TV content
3. AutoMk will automatically send completed rips via the `/upload` endpoint

Example folder configuration for AutoMk integration:
```json
{
  "HandBrake": {
    "Folders": [
      {
        "InputPath": "incoming/Movies",
        "OutputPath": "/output/Movies",
        "Preset": "HQ 1080p30 Surround"
      },
      {
        "InputPath": "incoming/TV Shows",
        "OutputPath": "/output/TV Shows",
        "Preset": "HQ 720p30 Surround"
      }
    ]
  }
}
```

### Web API Usage

The application provides REST API endpoints for file upload and monitoring:

#### Upload Files
```bash
# Upload a video file with metadata
curl -X POST http://localhost:5000/upload \
  -F "metadata={\"OriginalFileName\":\"video.mkv\",\"FileSizeBytes\":1000000,\"MediaInfo\":{\"MediaType\":\"Movie\",\"MovieTitle\":\"Example Movie\"},\"TransferTimestamp\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}" \
  -F "file=@/path/to/video.mkv"
```

#### Monitor Jobs
```bash
# Check job queue status
curl http://localhost:5000/jobs

# List uploaded files
curl http://localhost:5000/files

# Health check
curl http://localhost:5000/health

# Stop processing (maintenance)
curl -X POST http://localhost:5000/stop
```

### Running as a Service

**Windows Service:**
```bash
# Install as Windows Service
sc create "HandBrake Scheduler" binPath="C:\path\to\HandbrakeScheduler.exe"
sc start "HandBrake Scheduler"
```

**Linux Systemd Service:**
Create `/etc/systemd/system/handbrake-scheduler.service`:
```ini
[Unit]
Description=HandBrake Scheduler Service
After=network.target

[Service]
Type=notify
ExecStart=/usr/local/bin/dotnet /path/to/HandbrakeScheduler.dll
Restart=always
RestartSec=10
User=handbrake
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
```

Enable and start:
```bash
sudo systemctl enable handbrake-scheduler
sudo systemctl start handbrake-scheduler
```

### Docker Support

**Dockerfile:**
```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:6.0
WORKDIR /app
COPY . .
RUN apt-get update && apt-get install -y handbrake-cli
ENTRYPOINT ["dotnet", "HandbrakeScheduler.dll"]
```

**Docker Compose:**
```yaml
version: '3.8'
services:
  handbrake-scheduler:
    build: .
    volumes:
      - ./config:/app/config
      - ./input:/input
      - ./output:/output
      - ./logs:/app/logs
    environment:
      - HANDBRAKE__HandBrakeCliPath=/usr/bin/HandBrakeCLI
```

## HandBrake Presets

Common presets you can use:
- `Fast 1080p30` - Quick encoding for 1080p content
- `H.264 MKV 1080p30` - High quality 1080p MKV
- `H.265 MKV 1080p30` - HEVC encoding for smaller files
- `Android 1080p30` - Mobile-optimized encoding

To see all available presets, run:
```bash
HandBrakeCLI --preset-list
```

## Environment Variables

You can override configuration using environment variables with the `HANDBRAKE_` prefix:

```bash
export HANDBRAKE__HandBrakeCliPath="/usr/local/bin/HandBrakeCLI"
export HANDBRAKE__MonitoringEnabled="true"
export HANDBRAKE__StartTime="23:00:00"
export HANDBRAKE__EndTime="07:00:00"
export HANDBRAKE__FileTransfer__MaxFileSizeBytes="107374182400"
export HANDBRAKE__FileTransfer__ListenUrl="http://0.0.0.0:5000"
```

## Logging

The application provides comprehensive logging:

- **Console Logging**: Real-time status and progress information with color-coded levels
- **File Logging**: Detailed logs saved to configured file path
- **Progress Bars**: Visual progress indicators for file operations
- **Structured Logging**: JSON-formatted logs for easy parsing
- **Custom Formatting**: Timestamp, log level, and message formatting

Log levels can be configured per namespace in `appsettings.json`.

## Job Management

The application includes advanced job management features:

- **Persistent Queue**: Jobs are saved to disk and survive application restarts
- **Retry Logic**: Failed jobs are automatically retried up to 3 times
- **Status Tracking**: Jobs track their status (Pending, Processing, Completed, Failed)
- **Time Window Respect**: Processing only occurs during configured time windows
- **Concurrent Limiting**: Configurable number of concurrent processing jobs

## Media Metadata Support

The file upload system supports rich media metadata:

- **Movie Information**: Title, year, IMDB ID, genre, runtime
- **TV Series Information**: Series title, season, episode, episode title
- **Custom Metadata**: Additional key-value pairs for specialized use cases
- **Automatic Organization**: Files organized based on media type and metadata

## Troubleshooting

### Common Issues

**HandBrakeCLI Not Found**
```
Error: HandBrakeCLI not found at: /path/to/HandBrakeCLI
```
- Verify the path in `HandBrakeCliPath` is correct
- Ensure HandBrakeCLI has execute permissions
- Check if HandBrakeCLI is in your system PATH

**Network Access Issues**
```
Error: Access denied to network path
```
- Verify network credentials are correct
- Ensure the service account has network access
- Test network path access manually

**Insufficient Disk Space**
```
Warning: Insufficient disk space for temp copy
```
- Free up space in the temp directory
- Disable `UseTempFolder` to process files directly
- Adjust `MaxFileSizeBytes` to filter large files

**File Upload Issues**
```
Error: File size exceeds maximum
```
- Check `FileTransfer.MaxFileSizeBytes` setting (default: 100GB)
- Verify network connectivity and timeout settings
- Ensure sufficient disk space in `IncomingDirectory`

**Permission Errors**
```
Error: Access denied to output directory
```
- Ensure write permissions to output directories
- Check if output directories exist
- Verify service account permissions

### Debug Mode

Run with debug logging:
```bash
dotnet run --environment Development
```

Or set the log level in configuration:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug"
    }
  }
}
```

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the [MIT License](/LICENSE) file for details.

## Complete Media Pipeline Example

When used together with AutoMk, you can create a fully automated media processing pipeline:

1. **Insert Disc**: Place a Blu-ray or DVD in your drive
2. **AutoMk Rips**: Automatically detects disc, rips with MakeMKV, identifies content via OMDB
3. **File Transfer**: AutoMk sends the ripped MKV file to HandbrakeScheduler via HTTP
4. **Transcoding**: HandbrakeScheduler queues and processes the file with appropriate presets
5. **Final Output**: Transcoded file is saved to your media library with proper naming

This creates a hands-free workflow from physical disc to optimized media file, perfect for building or maintaining a Plex library.

## Acknowledgments

- [HandBrake](https://handbrake.fr/) for the excellent video transcoding engine
- [ShellProgressBar](https://github.com/Mpdreamz/shellprogressbar) for progress visualization
- Microsoft Extensions for hosting and configuration framework
- [AutoMk](https://github.com/timothydodd/auto-mk) for seamless disc ripping integration

