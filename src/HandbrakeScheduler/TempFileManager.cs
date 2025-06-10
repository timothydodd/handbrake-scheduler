using Microsoft.Extensions.Logging;

namespace HandbrakeScheduler
{
    public class TempFileManager : IDisposable
    {
        private readonly string _tempBasePath;
        private readonly ILogger<TempFileManager> _logger;
        private readonly HashSet<string> _tempFiles = new();
        private readonly object _lock = new();
        private bool _disposed = false;

        public TempFileManager(HandBrakeSettings handBrakeSettings, ILogger<TempFileManager> logger)
        {
            _logger = logger;
            _tempBasePath = Path.Combine(handBrakeSettings.TempFolder, "HandBrakeTemp");

            // Ensure temp directory exists
            if (!Directory.Exists(_tempBasePath))
            {
                Directory.CreateDirectory(_tempBasePath);
                _logger.LogInformation("Created temp directory: {TempPath}", _tempBasePath);
            }
        }

        public async Task<string> CopyToTempAsync(string sourcePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var fileName = Path.GetFileName(sourcePath);
                var tempFileName = $"{Guid.NewGuid()}_{fileName}";
                var tempPath = Path.Combine(_tempBasePath, tempFileName);

                _logger.LogInformation("Copying {FileName} to temp location for transcoding", fileName);

                var sourceInfo = new FileInfo(sourcePath);
                long totalBytes = sourceInfo.Length;
                long copiedBytes = 0;

                using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 8192);
                using var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 8192);

                byte[] buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await destStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    copiedBytes += bytesRead;

                    // Report progress
                    if (progress != null && totalBytes > 0)
                    {
                        double progressPercent = (double)copiedBytes / totalBytes * 100;
                        progress.Report(progressPercent);
                    }
                }

                // Track temp file for cleanup
                lock (_lock)
                {
                    if (!_disposed)
                    {
                        _tempFiles.Add(tempPath);
                    }
                }

                _logger.LogInformation("Successfully copied {FileName} to temp location", fileName);
                return tempPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error copying file to temp location: {SourcePath}", sourcePath);
                throw;
            }
        }

        public void CleanupTempFile(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                    _logger.LogInformation("Cleaned up temp file: {TempPath}", Path.GetFileName(tempPath));
                }

                lock (_lock)
                {
                    _tempFiles.Remove(tempPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up temp file: {TempPath}", tempPath);
            }
        }

        public void CleanupAllTempFiles()
        {
            lock (_lock)
            {
                foreach (var tempFile in _tempFiles.ToList())
                {
                    CleanupTempFile(tempFile);
                }
                _tempFiles.Clear();
            }
        }

        public long GetAvailableDiskSpace()
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(_tempBasePath) ?? _tempBasePath);
                return drive.AvailableFreeSpace;
            }
            catch
            {
                return long.MaxValue; // Assume enough space if we can't determine
            }
        }

        public bool HasSufficientSpace(long requiredBytes, double safetyMultiplier = 1.5)
        {
            long availableSpace = GetAvailableDiskSpace();
            long requiredSpace = (long)(requiredBytes * safetyMultiplier);

            _logger.LogDebug("Space check - Available: {Available} MB, Required: {Required} MB",
                availableSpace / 1024 / 1024, requiredSpace / 1024 / 1024);

            return availableSpace > requiredSpace;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                CleanupAllTempFiles();

                // Try to remove temp directory if empty
                try
                {
                    if (Directory.Exists(_tempBasePath) && !Directory.EnumerateFileSystemEntries(_tempBasePath).Any())
                    {
                        Directory.Delete(_tempBasePath);
                        _logger.LogInformation("Removed temp directory: {TempPath}", _tempBasePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove temp directory: {TempPath}", _tempBasePath);
                }

                _disposed = true;
            }
        }
    }
}
