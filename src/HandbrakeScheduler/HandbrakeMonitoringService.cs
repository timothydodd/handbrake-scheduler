using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HandbrakeScheduler
{
    internal class HandBrakeMonitoringService : BackgroundService
    {
        private readonly HandBrakeService _handBrakeService;
        private readonly HandBrakeSettings _handBrakeSettings;
        private readonly JobQueue _jobQueue;
        private readonly ILogger<HandBrakeMonitoringService> _logger;
        private readonly TempFileManager _tempFileManager;
        private readonly NetworkCredential? _networkCredential;

        public HandBrakeMonitoringService(
            HandBrakeService handBrakeService,
            HandBrakeSettings handBrakeSettings,
            JobQueue jobQueue,
            TempFileManager tempFileManager,
            ILogger<HandBrakeMonitoringService> logger)
        {
            _handBrakeService = handBrakeService;
            _handBrakeSettings = handBrakeSettings;
            _jobQueue = jobQueue;
            _tempFileManager = tempFileManager;
            _logger = logger;

            // Initialize network credentials if provided
            if (!string.IsNullOrEmpty(handBrakeSettings.Username) &&
                !string.IsNullOrEmpty(handBrakeSettings.Password))
            {
                _networkCredential = new NetworkCredential(handBrakeSettings.Username, handBrakeSettings.Password);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HandBrake Monitoring Service started");

            if (!_handBrakeSettings.MonitoringEnabled)
            {
                _logger.LogInformation("Monitoring is disabled. Running single scan...");
                await RunSingleScan(stoppingToken);
                return;
            }

            await RunMonitoringLoop(stoppingToken);
        }

        private async Task RunSingleScan(CancellationToken stoppingToken)
        {
            ScanAndQueueJobs();
            await ProcessJobs(stoppingToken);
        }

        private async Task RunMonitoringLoop(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Starting monitoring cycle");

                    ScanAndQueueJobs();

                    if (IsInAllowedTimeWindow())
                    {
                        _logger.LogInformation("In allowed time window. Processing jobs...");
                        await ProcessJobs(stoppingToken);
                    }
                    else
                    {
                        _logger.LogInformation("Outside allowed time window. Jobs queued: {JobCount}", _jobQueue.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in monitoring cycle");
                }

                await Task.Delay(TimeSpan.FromMinutes(_handBrakeSettings.ScanIntervalMinutes), stoppingToken);
            }
        }

        private void ScanAndQueueJobs()
        {
            foreach (var folder in _handBrakeSettings.Folders)
            {
                try
                {
                    ScanFolder(folder);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning folder {InputPath}", folder.InputPath);
                }
            }
        }

        private void ScanFolder(FolderSetting folder)
        {
            using var networkConnection = ConnectToNetworkPath(folder.InputPath);

            if (!Directory.Exists(folder.InputPath))
            {
                _logger.LogWarning("Folder {InputPath} does not exist", folder.InputPath);
                return;
            }

            var files = Directory.EnumerateFiles(folder.InputPath, "*.*",
                    folder.RecursiveSearch ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(file =>
                {
                    var fileInfo = new FileInfo(file);
                    return folder.ShouldProcessFile(fileInfo);
                });

            foreach (var file in files)
            {
                var job = CreateTranscodeJob(file, folder);
                _jobQueue.EnqueueJob(job);

                _logger.LogInformation("Queued job for: {FileName} ({FileSize} MB) - Remote: {IsRemote}",
                    job.FileName, job.FileSizeBytes / 1024 / 1024, job.IsRemoteSource);
            }
        }

        private TranscodeJob CreateTranscodeJob(string filePath, FolderSetting folder)
        {
            var relativePath = Path.GetDirectoryName(Path.GetRelativePath(folder.InputPath, filePath));
            var outputDirectory = Path.Combine(folder.OutputPath, relativePath ?? string.Empty);
            var fileInfo = new FileInfo(filePath);
            var isRemote = folder.CopyInputToTempFolder || IsNetworkPath(filePath);

            return new TranscodeJob
            {
                InputPath = filePath,
                OutputDirectory = outputDirectory,
                Preset = folder.Preset,
                DeleteSource = folder.DeleteSource,
                IsRemoteSource = isRemote,
                FileSizeBytes = fileInfo.Length
            };
        }

        private async Task ProcessJobs(CancellationToken stoppingToken)
        {
            if (_jobQueue.IsProcessing)
            {
                _logger.LogInformation("Job processing already in progress");
                return;
            }

            _jobQueue.IsProcessing = true;

            try
            {
                while (!_jobQueue.IsEmpty && !stoppingToken.IsCancellationRequested)
                {
                    if (!IsInAllowedTimeWindow() && _handBrakeSettings.StartTime.HasValue)
                    {
                        _logger.LogInformation("Outside allowed time window. Pausing job processing.");
                        break;
                    }

                    if (_jobQueue.TryDequeueJob(out var job) && job != null)
                    {
                        _logger.LogInformation("Processing job: {FileName}", job.FileName);
                        await _handBrakeService.ProcessSingleJob(job, _tempFileManager, stoppingToken);
                    }
                }
            }
            finally
            {
                _jobQueue.IsProcessing = false;
            }
        }

        private bool IsInAllowedTimeWindow()
        {
            if (!_handBrakeSettings.StartTime.HasValue || !_handBrakeSettings.EndTime.HasValue)
            {
                return true; // No time restrictions
            }

            var currentTime = TimeOnly.FromDateTime(DateTime.Now);
            var startTime = _handBrakeSettings.StartTime.Value;
            var endTime = _handBrakeSettings.EndTime.Value;

            // Handle time windows that span midnight
            return startTime <= endTime
                ? currentTime >= startTime && currentTime <= endTime
                : currentTime >= startTime || currentTime <= endTime;
        }

        private IDisposable? ConnectToNetworkPath(string path)
        {
            if (_networkCredential == null || !IsNetworkPath(path))
                return null;

            // For network paths, you might need to implement network drive mounting
            // This is a placeholder - actual implementation depends on OS and requirements
            return null;
        }

        private static bool IsNetworkPath(string path)
        {
            return path.StartsWith(@"\\") || path.StartsWith("//");
        }
    }
}
