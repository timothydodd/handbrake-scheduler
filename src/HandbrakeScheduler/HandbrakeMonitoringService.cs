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
        private readonly NetworkCredential? _networkCredential;

        private readonly TempFileManager _tempFileManager;

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


        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HandBrake Monitoring Service started");

            if (!_handBrakeSettings.MonitoringEnabled)
            {
                _logger.LogInformation("Monitoring is disabled. Running single scan...");
                ScanAndQueueJobs();
                await ProcessJobs(stoppingToken);
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Starting monitoring cycle");

                    // Scan for new files every 10 minutes
                    ScanAndQueueJobs();

                    // Check if we're in the allowed time window
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

                // Wait 10 minutes before next scan
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }

        private void ScanAndQueueJobs()
        {
            foreach (var folder in _handBrakeSettings.Folders)
            {
                try
                {
                    using var networkConnection = ConnectToNetworkPath(folder.InputPath);

                    if (!Directory.Exists(folder.InputPath))
                    {
                        _logger.LogWarning("Folder {InputPath} does not exist", folder.InputPath);
                        continue;
                    }

                    var files = FindVideos(folder.InputPath, folder.FileExtensions);

                    foreach (var file in files)
                    {
                        string inputNestedPath = Path.GetDirectoryName(file)
                            ?.Replace(folder.InputPath, "", StringComparison.InvariantCultureIgnoreCase) ?? "";
                        string outputDirectory = Path.Combine(folder.OutputPath, inputNestedPath);

                        var fileInfo = new FileInfo(file);
                        bool isRemote = folder.CopyInputToTempFolder || IsNetworkPath(file);

                        var job = new TranscodeJob
                        {
                            InputPath = file,
                            OutputDirectory = outputDirectory,
                            Preset = folder.Preset,
                            DeleteSource = folder.DeleteSource,
                            IsRemoteSource = isRemote,
                            FileSizeBytes = fileInfo.Length
                        };

                        _jobQueue.EnqueueJob(job);
                        _logger.LogInformation("Queued job for: {FileName} ({FileSize} MB) - Remote: {IsRemote}",
                            job.FileName, job.FileSizeBytes / 1024 / 1024, isRemote);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning folder {InputPath}", folder.InputPath);
                }
            }
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
            if (startTime <= endTime)
            {
                return currentTime >= startTime && currentTime <= endTime;
            }
            else
            {
                return currentTime >= startTime || currentTime <= endTime;
            }
        }

        private IEnumerable<string> FindVideos(string folder, string[] extensions)
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(s => extensions.Contains(Path.GetExtension(s), StringComparer.InvariantCultureIgnoreCase));
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
            // Check if Network path for mac



            // Check for UNC paths (Windows) or network paths (Linux/Unix)


            return path.StartsWith(@"\\") || path.StartsWith("//");

        }
    }
}
