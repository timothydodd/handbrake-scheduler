using System;

namespace HandbrakeScheduler.Services;

public enum ProgressLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Success
}

public class ProgressItemState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Description { get; set; } = string.Empty;
    public string? Category { get; set; }
    public double Value { get; set; }
    public double MaxValue { get; set; } = 100;
    public bool IsComplete { get; set; }
    public bool IsIndeterminate { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedTime { get; set; }

    public long? BytesTransferred { get; set; }
    public long? TotalBytes { get; set; }
    public double? TransferRateMBps { get; set; }
    public TimeSpan? TimeRemaining { get; set; }

    public double Percentage => MaxValue > 0 ? (Value / MaxValue) * 100 : 0;
    public TimeSpan Elapsed => DateTime.UtcNow - StartTime;
}

public class ProgressLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Message { get; set; } = string.Empty;
    public ProgressLogLevel Level { get; set; } = ProgressLogLevel.Info;
    public bool IsMarkup { get; set; }
}

public class ProgressManagerOptions
{
    public int MaxLogEntries { get; set; } = 50;
    public int VisibleLogLines { get; set; } = 10;
    public int RefreshIntervalMs { get; set; } = 100;
    public bool ShowTimestamps { get; set; } = false;
    public bool AutoRemoveCompleted { get; set; } = true;
    public int AutoRemoveDelayMs { get; set; } = 2000;
}
