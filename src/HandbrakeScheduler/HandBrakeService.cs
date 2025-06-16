using ShellProgressBar;

namespace HandbrakeScheduler
{
    public class HandBrakeService
    {
        private readonly HandBrakeCli _cli;
        private readonly ILogger<HandBrakeService> _logger;

        public HandBrakeService(HandBrakeCli cli, ILogger<HandBrakeService> logger)
        {
            _cli = cli;
            _logger = logger;
        }

        public async Task ProcessSingleJob(TranscodeJob job, TempFileManager tempFileManager, CancellationToken cancellationToken = default)
        {
            string workingFilePath = job.InputPath;


            try
            {
                _logger.LogInformation("Processing job: {FileName}", job.FileName);
                // Ensure output directory exists
                Directory.CreateDirectory(job.OutputDirectory);

                var outputDirectory = job.OutputDirectory;
                // Handle remote file copying if needed
                if (job.UseTempFolder)
                {
                    var tempResult = await HandleRemoteFile(job, tempFileManager, cancellationToken);
                    workingFilePath = tempResult.FilePath;
                    outputDirectory = Path.GetTempPath();
                }

                // Perform transcoding
                var resultFile = await PerformTranscoding(job, workingFilePath, outputDirectory, cancellationToken);
                if (job.UseTempFolder)
                {
                    // Copy result to output directory
                    await CopyResultToOutput(job, resultFile, tempFileManager, cancellationToken);
                    // Clean up temporary result file
                    DeleteFileIfExists(resultFile);
                }



                // Delete original source if requested and we used a temp file
                if (job.DeleteSource)
                {
                    DeleteOriginalFile(job);
                }

                _logger.LogInformation("Successfully processed job: {FileName}", job.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing job: {FileName}", job.FileName);
                throw;
            }
            finally
            {
                // Clean up temp file if we used one
                if (job.UseTempFolder && !string.IsNullOrEmpty(job.TempFilePath))
                {
                    tempFileManager.CleanupTempFile(job.TempFilePath);
                }
            }
        }

        private async Task<(string FilePath, bool UsedTemp)> HandleRemoteFile(
            TranscodeJob job,
            TempFileManager tempFileManager,
            CancellationToken cancellationToken)
        {
            if (tempFileManager.HasSufficientSpace(job.FileSizeBytes))
            {
                _logger.LogInformation("Copying remote file to temp location: {FileName}", job.FileName);

                using var progressBar = CreateProgressBar($"Copying {job.FileName}", ConsoleColor.Magenta);
                var copyProgress = CreateProgressReporter(progressBar, job.FileName, "Copy progress");

                var tempPath = await tempFileManager.CopyToTempAsync(job.InputPath, copyProgress, cancellationToken);
                job.TempFilePath = tempPath;

                return (tempPath, true);
            }
            else
            {
                _logger.LogWarning("Insufficient disk space for temp copy of {FileName}. Processing directly from network.", job.FileName);
                return (job.InputPath, false);
            }
        }

        private async Task<string> PerformTranscoding(
            TranscodeJob job,
            string workingFilePath,
            string outputDirectory,
            CancellationToken cancellationToken)
        {
            using var progressBar = CreateProgressBar($"Transcoding {job.FileName}", ConsoleColor.Yellow);

            var resultFile = await _cli.Transcode(
                workingFilePath,
                outputDirectory,
                job.Preset,
                status => UpdateTranscodingProgress(progressBar, status, job, cancellationToken),
                true);

            if (resultFile == null)
            {
                throw new InvalidOperationException($"Transcoding failed for {job.FileName}. Result file is null.");
            }

            return resultFile;
        }

        private async Task CopyResultToOutput(
            TranscodeJob job,
            string resultFile,
            TempFileManager tempFileManager,
            CancellationToken cancellationToken)
        {
            using var progressBar = CreateProgressBar($"Copying result for {job.FileName}", ConsoleColor.Magenta);
            var copyProgress = CreateProgressReporter(progressBar, job.FileName, "Copy progress");

            var resultFileName = FileUtil.GetFileNameWithNewExtension(job.FileName, ".mp4");
            var outputPath = Path.Combine(job.OutputDirectory, resultFileName);

            await tempFileManager.CopyFileAsync(resultFile, outputPath, copyProgress, cancellationToken);




            _logger.LogInformation("Copied result file to output directory: {OutputDirectory}", job.OutputDirectory);
        }

        private void DeleteOriginalFile(TranscodeJob job)
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

        private static void DeleteFileIfExists(string filePath)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        private static ProgressBar CreateProgressBar(string message, ConsoleColor color)
        {
            return new ProgressBar(100, message, new ProgressBarOptions
            {
                ForegroundColor = color,
                BackgroundColor = ConsoleColor.DarkGray,
                ProgressCharacter = '─'
            });
        }

        private static Progress<double> CreateProgressReporter(ProgressBar progressBar, string fileName, string operation)
        {
            return new Progress<double>(percent =>
            {
                progressBar.Tick((int)percent, $"{operation} for {fileName}");
            });
        }

        private static void UpdateTranscodingProgress(
            ProgressBar progressBar,
            dynamic status,
            TranscodeJob job,
            CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                progressBar.Tick((int)status.Percentage, status.Estimated,
                    $"{job.FileName} - AverageFps: {status.AverageFps}");
            }
        }
    }
}
