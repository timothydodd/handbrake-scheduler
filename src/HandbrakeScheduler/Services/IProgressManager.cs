using System;
using System.Threading;
using System.Threading.Tasks;

namespace HandbrakeScheduler.Services;

public interface IProgressManager : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    bool IsActive { get; }
    Task PauseAsync();
    Task ResumeAsync(CancellationToken cancellationToken = default);

    Guid CreateProgressTask(string description, double maxValue = 100, string? category = null);
    void UpdateProgress(Guid taskId, double value, string? description = null);
    void IncrementProgress(Guid taskId, double increment);
    void UpdateProgressBytes(Guid taskId, long bytesTransferred, long totalBytes,
        double? transferRateMBps = null, TimeSpan? timeRemaining = null);
    void CompleteProgressTask(Guid taskId);
    void RemoveProgressTask(Guid taskId);

    void Log(string message, ProgressLogLevel level = ProgressLogLevel.Info);
    void LogMarkup(string markup, ProgressLogLevel level = ProgressLogLevel.Info);

    void SetStatus(string status);
    void ClearStatus();
}
