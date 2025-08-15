using System.ComponentModel.DataAnnotations;

namespace HandbrakeScheduler
{
    public class HandBrakeSettings
    {
        [Required]
        public string HandBrakeCliPath { get; set; } = string.Empty;
        public string DefaultPreset { get; set; } = "HQ 1080p30 Surround";

        public FolderSetting[] Folders { get; set; } = Array.Empty<FolderSetting>();

        // Monitoring settings
        public bool MonitoringEnabled { get; set; } = true;

        public TimeOnly? StartTime { get; set; }

        public TimeOnly? EndTime { get; set; }

        // Network credentials for accessing remote folders
        public string? Username { get; set; }

        public string? Password { get; set; }

        // Additional settings that might be useful
        public int ScanIntervalMinutes { get; set; } = 1;

        public bool EnableLogging { get; set; } = true;

        public string? LogFilePath { get; set; }

        public string PersistencePath { get; set; } = "data";

        // Validation method
        public bool IsValid(out List<string> errors)
        {
            errors = new List<string>();

            if (string.IsNullOrWhiteSpace(HandBrakeCliPath))
            {
                errors.Add("HandBrakeCliPath is required");
            }
            else if (!File.Exists(HandBrakeCliPath))
            {
                errors.Add($"HandBrakeCLI not found at: {HandBrakeCliPath}");
            }

            if (!Folders.Any())
            {
                errors.Add("At least one folder configuration is required");
            }

            for (int i = 0; i < Folders.Length; i++)
            {
                var folder = Folders[i];
                var folderErrors = new List<string>();

                if (!folder.IsValid(out folderErrors))
                {
                    errors.AddRange(folderErrors.Select(e => $"Folder[{i}]: {e}"));
                }
            }

            if (StartTime.HasValue && EndTime.HasValue && StartTime == EndTime)
            {
                errors.Add("Start time and end time cannot be the same");
            }

            if (ScanIntervalMinutes <= 0)
            {
                errors.Add("Scan interval must be greater than 0 minutes");
            }

            // Validate network credentials
            if (!string.IsNullOrEmpty(Username) && string.IsNullOrEmpty(Password))
            {
                errors.Add("Password is required when Username is provided");
            }

            if (string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password))
            {
                errors.Add("Username is required when Password is provided");
            }

            return !errors.Any();
        }

        public bool HasTimeRestrictions => StartTime.HasValue && EndTime.HasValue;

        public bool HasNetworkCredentials => !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);
    }

    public class FolderSetting
    {
        [Required]
        public string InputPath { get; set; } = string.Empty;

        public bool UseTempFolder { get; set; }

        [Required]
        public string OutputPath { get; set; } = string.Empty;

        [Required]
        public string Preset { get; set; } = string.Empty;

        public string[]? FileExtensions { get; set; }

        public bool DeleteSource { get; set; }

        // Additional useful properties
        public bool RecursiveSearch { get; set; } = true;

        public long MaxFileSizeBytes { get; set; } = long.MaxValue;

        public long MinFileSizeBytes { get; set; } = 0;

        // Resolution-based preset mapping
        public ResolutionPresetMapping[]? ResolutionPresets { get; set; }

        // Validation method
        public bool IsValid(out List<string> errors)
        {
            errors = new List<string>();

            if (string.IsNullOrWhiteSpace(InputPath))
            {
                errors.Add("InputPath is required");
            }

            if (string.IsNullOrWhiteSpace(OutputPath))
            {
                errors.Add("OutputPath is required");
            }

            if (string.IsNullOrWhiteSpace(Preset))
            {
                errors.Add("Preset is required");
            }

            if (!FileExtensions.Any())
            {
                errors.Add("At least one file extension must be specified");
            }

            // Validate file extensions format
            foreach (var ext in FileExtensions)
            {
                if (string.IsNullOrWhiteSpace(ext))
                {
                    errors.Add("File extensions cannot be empty");
                    break;
                }

                if (!ext.StartsWith("."))
                {
                    errors.Add($"File extension '{ext}' must start with a dot");
                }
            }

            if (MaxFileSizeBytes <= MinFileSizeBytes)
            {
                errors.Add("MaxFileSizeBytes must be greater than MinFileSizeBytes");
            }

            return !errors.Any();
        }

        public bool IsNetworkPath => InputPath.StartsWith(@"\\") || InputPath.StartsWith("//");

        public bool ShouldProcessFile(FileInfo fileInfo)
        {
            return fileInfo.Length >= MinFileSizeBytes &&
                   fileInfo.Length <= MaxFileSizeBytes &&
                   (FileExtensions == null || FileExtensions.Contains(fileInfo.Extension, StringComparer.InvariantCultureIgnoreCase));
        }

        public string GetPresetForResolution(int? width, int? height)
        {
            // If no resolution-based presets are configured, use the default preset
            if (ResolutionPresets == null || ResolutionPresets.Length == 0)
            {
                return Preset;
            }

            // If we couldn't detect resolution, use the default preset
            if (!width.HasValue || !height.HasValue)
            {
                return Preset;
            }

            // Find the best matching preset based on resolution
            // Sort by minimum height descending to find the highest resolution that matches
            var matchingPreset = ResolutionPresets
                .Where(rp => height.Value >= rp.MinHeight && (rp.MaxHeight == null || height.Value <= rp.MaxHeight))
                .Where(rp => width.Value >= rp.MinWidth && (rp.MaxWidth == null || width.Value <= rp.MaxWidth))
                .OrderByDescending(rp => rp.MinHeight)
                .ThenByDescending(rp => rp.MinWidth)
                .FirstOrDefault();

            return matchingPreset?.Preset ?? Preset;
        }
    }

    public class ResolutionPresetMapping
    {
        public int MinWidth { get; set; }
        public int? MaxWidth { get; set; }
        public int MinHeight { get; set; }
        public int? MaxHeight { get; set; }
        public string Preset { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
