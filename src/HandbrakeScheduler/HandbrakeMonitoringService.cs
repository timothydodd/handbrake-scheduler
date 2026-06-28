using System.Net;
using System.Runtime.InteropServices;


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
        private readonly FileTransferHostSettings _fileTransferHostSettings;
        private readonly VideoInfoService _videoInfoService;

        public HandBrakeMonitoringService(
            HandBrakeService handBrakeService,
            HandBrakeSettings handBrakeSettings,
            JobQueue jobQueue,
            TempFileManager tempFileManager,
            ILogger<HandBrakeMonitoringService> logger,
            FileTransferHostSettings fileTransferHostSettings,
            VideoInfoService videoInfoService)
        {
            _handBrakeService = handBrakeService;
            _handBrakeSettings = handBrakeSettings;
            _jobQueue = jobQueue;
            _tempFileManager = tempFileManager;
            _logger = logger;
            _videoInfoService = videoInfoService;

            // Initialize network credentials if provided
            if (!string.IsNullOrEmpty(handBrakeSettings.Username) &&
                !string.IsNullOrEmpty(handBrakeSettings.Password))
            {
                _networkCredential = new NetworkCredential(handBrakeSettings.Username, handBrakeSettings.Password);
            }

            _fileTransferHostSettings = fileTransferHostSettings;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HandBrake Monitoring Service started on {Platform}",
                RuntimeInformation.OSDescription);


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
            await ScanAndQueueJobsAsync();
            await ProcessJobs(stoppingToken);
        }

        private async Task RunMonitoringLoop(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Starting monitoring cycle");

                    await ScanAndQueueJobsAsync();

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

        private async Task ScanAndQueueJobsAsync()
        {
            foreach (var folder in _handBrakeSettings.Folders)
            {
                try
                {
                    await ScanFolderAsync(folder);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error scanning folder {InputPath}", folder.InputPath);
                }
            }
        }

        private async Task ScanFolderAsync(FolderSetting folder)
        {
            using var networkConnection = ConnectToNetworkPath(folder.InputPath);

            var normalizedPath = PathConverter.NormalizePath(folder.InputPath);

            if (!Directory.Exists(normalizedPath))
            {
                _logger.LogWarning("Folder {InputPath} does not exist", normalizedPath);
                return;
            }

            _logger.LogDebug("Scanning folder: {Path}", normalizedPath);

            try
            {
                var files = SafeEnumerateFiles(normalizedPath, folder.RecursiveSearch)
                    .Where(file =>
                    {
                        try
                        {
                            // Skip files in _moved directories
                            if (file.Contains(Path.DirectorySeparatorChar + "_moved" + Path.DirectorySeparatorChar) ||
                                file.Contains(Path.AltDirectorySeparatorChar + "_moved" + Path.AltDirectorySeparatorChar))
                            {
                                return false;
                            }
                            
                            var fileInfo = new FileInfo(file);
                            return folder.ShouldProcessFile(fileInfo);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Error checking file {File}: {Error}", file, ex.Message);
                            return false;
                        }
                    });

                foreach (var file in files)
                {
                    try
                    {
                        var job = await CreateTranscodeJobAsync(file, folder);
                        _jobQueue.EnqueueJob(job);

                        _logger.LogInformation("Queued job for: {FileName} ({FileSize} MB) - Preset: {Preset} - Remote: {IsRemote}",
                            job.FileName, job.FileSizeBytes / 1024 / 1024, job.Preset, !string.IsNullOrEmpty(job.StagingPath));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Error creating job for file {File}: {Error}", file, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enumerating files in folder {Path}", normalizedPath);
            }
        }

        private IEnumerable<string> SafeEnumerateFiles(string rootPath, bool recursive)
        {
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var enumerationOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = recursive,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
            };

            // Use a queue-based approach for better error handling with recursive searches
            if (recursive)
            {
                return SafeEnumerateFilesRecursive(rootPath);
            }
            else
            {
                return SafeEnumerateFilesInDirectory(rootPath);
            }
        }

        private IEnumerable<string> SafeEnumerateFilesRecursive(string rootPath)
        {
            var directoriesToProcess = new Queue<string>();
            directoriesToProcess.Enqueue(rootPath);

            while (directoriesToProcess.Count > 0)
            {
                var currentDirectory = directoriesToProcess.Dequeue();

                // Enumerate files in current directory with individual error handling
                foreach (var file in SafeEnumerateFilesInDirectory(currentDirectory))
                {
                    yield return file;
                }

                // Add subdirectories to queue with individual error handling
                foreach (var subDir in SafeEnumerateDirectories(currentDirectory))
                {
                    directoriesToProcess.Enqueue(subDir);
                }
            }
        }

        private IEnumerable<string> SafeEnumerateFilesInDirectory(string directoryPath)
        {
            var files = new List<string>();

            try
            {
                var normalizedPath = PathConverter.NormalizePath(directoryPath);
                _logger.LogDebug("Enumerating files in: {Directory}", normalizedPath);

                // Use GetFiles instead of EnumerateFiles for better error handling
                var fileArray = Directory.GetFiles(normalizedPath, "*.*", SearchOption.TopDirectoryOnly);

                foreach (var file in fileArray)
                {
                    try
                    {
                        var normalizedFile = PathConverter.NormalizePath(file);
                        if (File.Exists(normalizedFile))
                        {
                            files.Add(normalizedFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Skipping inaccessible file {File}: {Error}", file, ex.Message);
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("Access denied to directory {Directory}: {Error}", directoryPath, ex.Message);
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.LogWarning("Directory not found {Directory}: {Error}", directoryPath, ex.Message);
            }
            catch (IOException ex) when (ex.Message.Contains("Invalid argument"))
            {
                _logger.LogWarning("Invalid path or filesystem issue with directory {Directory}: {Error}",
                    directoryPath, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error enumerating files in directory {Directory}", directoryPath);
            }

            return files;
        }

        private IEnumerable<string> SafeEnumerateDirectories(string directoryPath)
        {
            var directories = new List<string>();

            try
            {
                var normalizedPath = PathConverter.NormalizePath(directoryPath);
                _logger.LogDebug("Enumerating directories in: {Directory}", normalizedPath);

                // Use GetDirectories instead of EnumerateDirectories for better error handling
                var dirArray = Directory.GetDirectories(normalizedPath, "*", SearchOption.TopDirectoryOnly);

                foreach (var dir in dirArray)
                {
                    try
                    {
                        var normalizedDir = PathConverter.NormalizePath(dir);
                        
                        // Skip _moved directories
                        if (Path.GetFileName(normalizedDir) == "_moved")
                        {
                            _logger.LogDebug("Skipping _moved directory: {Directory}", normalizedDir);
                            continue;
                        }
                        
                        if (Directory.Exists(normalizedDir))
                        {
                            directories.Add(normalizedDir);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Skipping inaccessible directory {Directory}: {Error}", dir, ex.Message);
                    }
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning("Access denied to directory {Directory}: {Error}", directoryPath, ex.Message);
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.LogWarning("Directory not found {Directory}: {Error}", directoryPath, ex.Message);
            }
            catch (IOException ex) when (ex.Message.Contains("Invalid argument"))
            {
                _logger.LogWarning("Invalid path or filesystem issue with directory {Directory}: {Error}",
                    directoryPath, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error enumerating directories in {Directory}", directoryPath);
            }

            return directories;
        }

        private async Task<TranscodeJob> CreateTranscodeJobAsync(string filePath, FolderSetting folder)
        {
            var normalizedInputPath = PathConverter.NormalizePath(folder.InputPath);
            var normalizedFilePath = PathConverter.NormalizePath(filePath);

            var relativePath = Path.GetDirectoryName(Path.GetRelativePath(normalizedInputPath, normalizedFilePath));
            var outputDirectory = Path.Combine(folder.OutputPath, relativePath ?? string.Empty);

            var fileInfo = new FileInfo(normalizedFilePath);
            var stagingPath = !string.IsNullOrEmpty(folder.StagingPath) || IsNetworkPath(normalizedFilePath) || IsNetworkPath(outputDirectory)
                ? (string.IsNullOrEmpty(folder.StagingPath) ? Path.GetTempPath() : folder.StagingPath)
                : string.Empty;

            // Detect video resolution to determine the appropriate preset
            var videoInfo = await _videoInfoService.GetVideoInfoAsync(normalizedFilePath);
            var selectedPreset = folder.GetPresetForResolution(videoInfo.Width, videoInfo.Height);

            _logger.LogDebug("Video {File} detected as {Width}x{Height}, using preset: {Preset}",
                Path.GetFileName(normalizedFilePath), videoInfo.Width, videoInfo.Height, selectedPreset);

            var normalizedOutputDirectory = PathConverter.NormalizePath(outputDirectory);
            _logger.LogInformation(
                "Created job for {File}: inputFolder={InputFolder}, relativePath='{RelativePath}', outputDir={OutputDir}, preset={Preset}, staging={Staging}, deleteSource={DeleteSource}, size={SizeMb:F1}MB",
                Path.GetFileName(normalizedFilePath), normalizedInputPath, relativePath ?? string.Empty,
                normalizedOutputDirectory, selectedPreset,
                string.IsNullOrEmpty(stagingPath) ? "(none)" : stagingPath,
                folder.DeleteSource, fileInfo.Length / 1024.0 / 1024.0);

            return new TranscodeJob
            {
                InputPath = normalizedFilePath,
                OutputDirectory = normalizedOutputDirectory,
                Preset = selectedPreset,
                DeleteSource = folder.DeleteSource,
                StagingPath = stagingPath,
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
                        if (!await _handBrakeService.ProcessSingleJob(job, _tempFileManager, stoppingToken))
                        {
                            var errorMsg = $"HandBrake processing failed. File remains in _moved folder.";
                            _logger.LogError("Failed to process {FileName}. File will remain in _moved folder at: {Path}", 
                                job.FileName, job.InputPath);
                            _jobQueue.MarkJobFailed(job, errorMsg);
                        }
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
            if (string.IsNullOrEmpty(path))
                return false;

            // Windows UNC paths
            if (path.StartsWith(@"\\") || path.StartsWith("//"))
                return true;

            // Network drive letters on Windows (basic check)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                path.Length >= 2 &&
                char.IsLetter(path[0]) &&
                path[1] == ':')
            {
                // This is a basic check - you might want to enhance this
                // to actually verify if the drive is a network drive
                return false;
            }

            // Unix/Linux/macOS network paths (basic patterns)
            if (path.StartsWith("/mnt/") ||
                path.StartsWith("/media/") ||
                path.StartsWith("/Volumes/"))
            {
                // These could be network mounts, but not necessarily
                // You might want to enhance this logic
                return false;
            }

            return false;
        }
    }
}
