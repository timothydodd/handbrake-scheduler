# HandBrake Encoding Scheduler

This .NET 8 application is designed to schedule and automate video encoding using HandBrakeCLI. It allows you to specify the input folders, output locations, encoding presets, and schedule encoding during specific parts of the day.

## Features

- **Multiple Folder Support:** Configure multiple folders for encoding with individual settings.
- **Custom Presets:** Use any HandBrake preset for your encoding tasks.
- **Scheduled Encoding:** Set specific times for the encoding process to run.
- **Automated Cleanup:** Option to delete source files after encoding.
- **Folder Structure Preservation:** Retain the original folder structure in the output directory.
- **Logging:** Detailed logging for monitoring and troubleshooting.

## Configuration

Below is an example of the configuration file (`appsettings.json`):

```json
{
  "HandBrake": {
    "HandBrakeCliPath": "C:\\ProgramData\\chocolatey\\bin\\HandBrakeCLI.exe",
    "Folders": [
      {
        "InputPath": "E:\\Videos\\TV Shows\\",
        "Preset": "Fast 1080p30",
        "PreserveFolderStructure": true,
        "DeleteSource": true,
        "OutputPath": "G:\\videos\\TV Shows\\",
        "FileExtensions": [
          ".mkv",
          ".mp4"
        ]
      },
      {
        "InputPath": "E:\\Videos\\Movies\\",
        "Preset": "HQ 1080p30 Surround",
        "PreserveFolderStructure": true,
        "DeleteSource": true,
        "OutputPath": "G:\\videos\\Movies\\",
        "FileExtensions": [
          ".mkv",
          ".mp4"
        ]
      }
    ]
  },
  "Schedule": {
    "Enabled": false,
    "StartTime": "18:05:00",
    "EndTime": "18:10:00",
    "User": null,
    "Password": null
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}

```

## Key Configuration Settings
HandBrakeCliPath: Path to the HandBrakeCLI executable.

- Folders: An array of folder configurations where you can define:

-- InputPath: Path to the folder containing videos to encode.
-- Preset: The HandBrake encoding preset to use.
-- PreserveFolderStructure: Set to true to maintain the folder structure in the output.
-- DeleteSource: Set to true to delete the original files after encoding.
-- OutputPath: Destination folder for encoded videos.
-- FileExtensions: File types to encode (e.g., .mkv, .mp4).
- Schedule:

-- Enabled: Set to true to activate scheduled encoding.
-- StartTime and EndTime: Define the time window for encoding tasks.
-- User and Password: Optional credentials for secure environments.
- Logging:
- Adjust the log level (Default) to control the verbosity of logs.

## Usage
1. Setup HandBrakeCLI:
Ensure that HandBrakeCLI is installed on your system. You can use Chocolatey to install it:
``` bash
choco install handbrake-cli
```
2. Configure the Application:
Edit the appsettings.json file to match your environment and encoding needs.

3. Run the Application:
Launch the .NET application to start encoding based on the provided schedule and configuration.
``` bash
dotnet run
```

4. Check Logs:
Logs are stored in the application directory and can be used to troubleshoot or verify the encoding process.

Contributing
Contributions are welcome! Please submit issues or pull requests to help improve this tool.

License
This project is licensed under the MIT License. See the LICENSE file for details.
