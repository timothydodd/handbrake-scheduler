using Microsoft.Extensions.Logging;
using ShellProgressBar;

namespace HandbrakeScheduler
{
    internal class HandBrakeService
    {
        private readonly HandBrakeCli _cli;
        private readonly ILogger<HandBrakeService> _logger;

        public HandBrakeService(
            HandBrakeCli cli,
            ILogger<HandBrakeService> logger)
        {
            _cli = cli;
            _logger = logger;
        }

        public async Task ProcessSingleJob(TranscodeJob job, TempFileManager tempFileManager, CancellationToken cancellationToken = default)
        {
            string workingFilePath = job.InputPath;
            bool usedTempFile = false;

            try
            {
                _logger.LogInformation("Processing job: {FileName}", job.FileName);

                // Check if we should use temp file for remote sources
                if (job.IsRemoteSource && tempFileManager.HasSufficientSpace(job.FileSizeBytes))
                {
                    _logger.LogInformation("Copying remote file to temp location: {FileName}", job.FileName);

                    var copyProgress = new Progress<double>(percent =>
                    {
                        if (percent % 10 < 1) // Log every 10%
                        {
                            _logger.LogInformation("Copy progress for {FileName}: {Percent:F1}%", job.FileName, percent);
                        }
                    });

                    workingFilePath = await tempFileManager.CopyToTempAsync(job.InputPath, copyProgress, cancellationToken);
                    job.TempFilePath = workingFilePath;
                    usedTempFile = true;
                }
                else if (job.IsRemoteSource)
                {
                    _logger.LogWarning("Insufficient disk space for temp copy of {FileName}. Processing directly from network.", job.FileName);
                }

                // Ensure output directory exists
                if (!Directory.Exists(job.OutputDirectory))
                {
                    Directory.CreateDirectory(job.OutputDirectory);
                }

                ProgressBar bar = new(100, "Transcoding " + job.FileName, new ProgressBarOptions
                {
                    ForegroundColor = ConsoleColor.Yellow,
                    BackgroundColor = ConsoleColor.DarkGray,
                    ProgressCharacter = '─'
                });

                try
                {
                    await _cli.Transcode(workingFilePath, job.OutputDirectory, job.Preset, (s) =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            bar.Tick((int)s.Percentage, s.Estimated, $"{job.FileName} - AverageFps: {s.AverageFps}");
                        }
                    }, true, job.DeleteSource && !usedTempFile); // Only delete source if not using temp file

                    _logger.LogInformation("Successfully processed job: {FileName}", job.FileName);

                    // If we used a temp file and original should be deleted, delete the original
                    if (usedTempFile && job.DeleteSource)
                    {
                        try
                        {
                            File.Delete(job.InputPath);
                            _logger.LogInformation("Deleted original file: {FileName}", job.FileName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Could not delete original file: {FileName}", job.FileName);
                        }
                    }
                }
                finally
                {
                    bar.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing job: {FileName}", job.FileName);
                throw;
            }
            finally
            {
                // Clean up temp file if we used one
                if (usedTempFile && !string.IsNullOrEmpty(job.TempFilePath))
                {
                    tempFileManager.CleanupTempFile(job.TempFilePath);
                }
            }
        }
    }
}
