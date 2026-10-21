# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Development Commands

### Build and Run
```bash
# Build the project
dotnet build --configuration Release

# Run in development mode
dotnet run

# Run the compiled application
dotnet HandbrakeScheduler.dll
```

### Project Structure
- **Solution**: `src/HandbrakeScheduler.sln`
- **Main Project**: `src/HandbrakeScheduler/HandbrakeScheduler.csproj`
- **Target Framework**: .NET 9.0 with latest C# language features

## Architecture Overview

This is a .NET video transcoding scheduler that combines file system monitoring with a web API for file uploads. The application processes video files using HandBrake CLI with configurable scheduling and network support.

### Core Components

#### Background Services
- **HandBrakeMonitoringService**: Main orchestrator that scans folders and processes jobs
- **HandBrakeService**: Handles individual transcode job execution with progress tracking
- **JobQueue**: Thread-safe job management with persistence and retry logic (3 attempts max)

#### File Processing
- **FileReceiver**: Web API handler for multipart file uploads at `/upload` endpoint
- **HandBrakeFileProcessor**: Processes uploads and creates jobs based on media metadata
- **TempFileManager**: Manages local copies of remote files for faster processing
- **HandBrakeCli**: Low-level wrapper for HandBrake CLI with timeout monitoring

#### Configuration
- **HandBrakeSettings**: Main configuration with folder monitoring, time windows, and network credentials
- **FolderSetting**: Per-folder configuration (input/output paths, presets, file filters)
- **FileTransferHostSettings**: Web API configuration for file uploads

### Key Architecture Patterns
- **Dependency Injection**: All services use constructor injection
- **Background Service Pattern**: Continuous monitoring via .NET hosted services  
- **Queue-based Processing**: Persistent job queue with error tracking
- **Event-driven**: Progress reporting and lifecycle notifications
- **Cross-platform**: Path normalization for Windows/Unix compatibility

### Processing Flow
1. Files discovered via folder scanning or web upload
2. Jobs queued with metadata-based routing (TV/Movie/Default)
3. Processing respects time window restrictions if configured
4. Remote files copied to temp storage when needed
5. HandBrake CLI execution with progress monitoring
6. Output files moved to configured destinations
7. Source deletion and cleanup based on settings

### Web API Endpoints
- `POST /upload` - File upload with metadata
- `GET /files` - List received files
- `GET /jobs` - Job queue status
- `POST /stop` - Stop current processing
- `GET /health` - Health check

### Configuration Files
- **appsettings.json**: Main configuration with HandBrake settings and folder monitoring
- **Environment Variables**: Use `HANDBRAKE_` prefix to override settings
- **User Secrets**: Supported for sensitive data in development

### Time Window Processing
- Optional `StartTime`/`EndTime` in configuration restricts processing to specific hours
- Uses 24-hour format (e.g., "22:00:00" to "06:00:00" for overnight processing)
- Monitoring continues but transcoding only occurs within windows

### Network Path Support  
- Supports UNC paths with credential configuration
- Uses `CopyInputToTempFolder` setting for remote file performance
- Cross-platform path normalization handles different path separators

### Error Handling
- Failed jobs retry up to 3 times with error tracking
- Queue state persists across application restarts  
- Timeout monitoring prevents stuck HandBrake processes
- Graceful shutdown with proper cleanup of temporary files