# HandBrake Scheduler

A .NET service that automatically monitors directories for video files and transcodes them using HandBrakeCLI with configurable scheduling, time windows, and network support.

## Features

-  **Automatic Monitoring**: Continuously monitors configured directories for new video files
-  **Time-Based Scheduling**: Optional time windows for processing (e.g., only during off-hours)
-  **Network Share Support**: Works with UNC paths and network drives with credential management
-  **Multi-Directory Support**: Monitor multiple input/output directory pairs simultaneously
-  **Flexible File Filtering**: Configurable file extensions, size limits, and recursive scanning
-  **Smart Temp File Management**: Copies remote files locally for faster processing when space allows
-  **Progress Tracking**: Real-time progress bars for file copying and transcoding operations
-  **Source File Management**: Optional deletion of source files after successful transcoding
-  **Comprehensive Logging**: Console and file logging with configurable levels
-  **Graceful Shutdown**: Proper cleanup of processes and temporary files on exit

## Prerequisites

- .NET 6.0 or later
- HandBrakeCLI executable
- Sufficient disk space for temporary files (when processing remote files)

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
    "Folders": [
      {
        "InputPath": "/input/videos",
        "OutputPath": "/output/transcoded",
        "Preset": "Fast 1080p30",
        "FileExtensions": [".mkv", ".avi", ".mp4", ".mov"],
        "DeleteSource": false,
        "CopyInputToTempFolder": true,
        "RecursiveSearch": true,
        "MaxFileSizeBytes": 21474836480,
        "MinFileSizeBytes": 104857600
      }
    ]
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

#### Folder Settings
- **`InputPath`**: Directory to monitor for video files (required)
- **`OutputPath`**: Directory for transcoded files (required)
- **`Preset`**: HandBrake preset name (required)
- **`FileExtensions`**: Array of file extensions to process
- **`DeleteSource`**: Delete original files after successful transcoding
- **`CopyInputToTempFolder`**: Copy remote files to local temp for processing
- **`RecursiveSearch`**: Search subdirectories (default: true)
- **`MaxFileSizeBytes`**: Maximum file size to process
- **`MinFileSizeBytes`**: Minimum file size to process

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
```

## Logging

The application provides comprehensive logging:

- **Console Logging**: Real-time status and progress information
- **File Logging**: Detailed logs saved to configured file path
- **Progress Bars**: Visual progress indicators for file operations
- **Structured Logging**: JSON-formatted logs for easy parsing

Log levels can be configured per namespace in `appsettings.json`.

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
- Disable `CopyInputToTempFolder` to process files directly
- Adjust `MaxFileSizeBytes` to filter large files

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

## Acknowledgments

- [HandBrake](https://handbrake.fr/) for the excellent video transcoding engine
- [ShellProgressBar](https://github.com/Mpdreamz/shellprogressbar) for progress visualization
- Microsoft Extensions for hosting and configuration framework

