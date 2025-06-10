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
                    ProgressBar bar2 = new(100, $"Copying remote file to temp location: {job.FileName}", new ProgressBarOptions
                    {
                        ForegroundColor = ConsoleColor.Magenta,
                        BackgroundColor = ConsoleColor.DarkGray,
                        ProgressCharacter = '─'
                    });

                    var copyProgress = new Progress<double>(percent =>
                    {

                        bar2.Tick((int)percent, $"Copy progress for {job.FileName}");

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

                ProgressBar bar = new(100, $"Transcoding {job.FileName}", new ProgressBarOptions
                {
                    ForegroundColor = ConsoleColor.Yellow,
                    BackgroundColor = ConsoleColor.DarkGray,
                    ProgressCharacter = '─'
                });

                try
                {


                    var resultFile = await _cli.Transcode(workingFilePath, Path.GetTempPath(), job.Preset, (s) =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            bar.Tick((int)s.Percentage, s.Estimated, $"{job.FileName} - AverageFps: {s.AverageFps}");
                        }
                    }, true, job.DeleteSource && !usedTempFile); // Only delete source if not using temp file

                    _logger.LogInformation("Successfully processed job: {FileName}", job.FileName);

                    if (resultFile == null)
                    {
                        _logger.LogError("Transcoding failed for {FileName}. Result file is null.", job.FileName);
                        throw new InvalidOperationException($"Transcoding failed for {job.FileName}. Result file is null.");
                    }
                    ProgressBar bar3 = new(100, $"Copying remote file to temp location: {job.FileName}", new ProgressBarOptions
                    {
                        ForegroundColor = ConsoleColor.Magenta,
                        BackgroundColor = ConsoleColor.DarkGray,
                        ProgressCharacter = '─'
                    });

                    var copyProgress = new Progress<double>(percent =>
                    {

                        bar3.Tick((int)percent, $"Copy progress for {job.FileName}");

                    });

                    var resultFileName = Path.GetFileName(resultFile);
                    await tempFileManager.CopyFileAsync(resultFile, Path.Combine(job.OutputDirectory, resultFileName), copyProgress, cancellationToken);

                    _logger.LogInformation("Copied result file to output directory: {OutputDirectory}", job.OutputDirectory);
                    //delete temp file
                    if (File.Exists(resultFile))
                        File.Delete(resultFile); // Delete the temp result file after copying

                    //copy the result file to the output directory


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
