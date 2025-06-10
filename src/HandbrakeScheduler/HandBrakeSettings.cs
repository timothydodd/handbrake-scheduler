namespace HandbrakeScheduler
{
    public class HandBrakeSettings
    {
        public string HandBrakeCliPath { get; set; } = string.Empty;
        public FolderSetting[] Folders { get; set; } = Array.Empty<FolderSetting>();

        // Monitoring settings
        public bool MonitoringEnabled { get; set; }
        public TimeOnly? StartTime { get; set; }
        public TimeOnly? EndTime { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string TempFolder { get; set; } = Path.GetTempPath();
        public bool UseTemp { get; set; } = true;
    }

    public class FolderSetting
    {
        public string InputPath { get; set; } = string.Empty;
        public bool CopyInputToTempFolder { get; set; }
        public string OutputPath { get; set; } = string.Empty;
        public string Preset { get; set; } = string.Empty;
        public string[] FileExtensions { get; set; } = Array.Empty<string>();
        public bool DeleteSource { get; set; }
    }
}
