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

        public async Task<bool> ProcessSingleJob(TranscodeJob job, TempFileManager tempFileManager, CancellationToken cancellationToken = default)
        {
            string workingFilePath = job.InputPath;

            bool success = true;
            try
            {
                _logger.LogInformation("Processing job: {FileName}", job.FileName);
                
                // Move source file to _moved folder to prevent reprocessing
                workingFilePath = MoveSourceToMovedFolder(job);
                
                // Ensure output directory exists
                Directory.CreateDirectory(job.OutputDirectory);

                // Update job with new path after moving to _moved
                job.InputPath = workingFilePath;
                
                var outputDirectory = job.OutputDirectory;
                // Handle staging if a staging path is configured
                var useStaging = !string.IsNullOrEmpty(job.StagingPath);
                if (useStaging)
                {
                    var tempResult = await HandleRemoteFile(job, tempFileManager, cancellationToken);
                    workingFilePath = tempResult.FilePath;
                    outputDirectory = job.StagingPath;
                }

                // Perform transcoding
                var resultFile = await PerformTranscoding(job, workingFilePath, outputDirectory, cancellationToken);
                if (useStaging)
                {
                    // Copy result to output directory
                    await CopyResultToOutput(job, resultFile, tempFileManager, cancellationToken);
                    // Clean up temporary result file
                    DeleteFileIfExists(resultFile);
                }



                // Source file has already been moved to _moved folder at the beginning

                _logger.LogInformation("Successfully processed job: {FileName}", job.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing job: {FileName}. File will remain in _moved folder at: {MovedPath}", 
                    job.FileName, workingFilePath);
                success = false;
                // Don't delete the source file - leave it in _moved folder
            }
            finally
            {
                // Clean up temp file if we used one
                if (!string.IsNullOrEmpty(job.StagingPath) && !string.IsNullOrEmpty(job.TempFilePath))
                {
                    tempFileManager.CleanupTempFile(job.TempFilePath);
                }
            }
            return success;
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

        private string MoveSourceToMovedFolder(TranscodeJob job)
        {
            try
            {
                // Get the directory of the input file
                var inputDirectory = Path.GetDirectoryName(job.InputPath);
                if (string.IsNullOrEmpty(inputDirectory))
                {
                    _logger.LogWarning("Could not determine input directory for: {FileName}", job.FileName);
                    return job.InputPath;
                }

                // Create _moved folder path
                var movedFolderPath = Path.Combine(inputDirectory, "_moved");
                
                // Ensure _moved directory exists
                Directory.CreateDirectory(movedFolderPath);
                
                // Create new path in _moved folder
                var movedFilePath = Path.Combine(movedFolderPath, job.FileName);
                
                // Check if file already exists in _moved folder
                if (File.Exists(movedFilePath))
                {
                    // Add timestamp to filename to make it unique
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(job.FileName);
                    var extension = Path.GetExtension(job.FileName);
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    movedFilePath = Path.Combine(movedFolderPath, $"{fileNameWithoutExt}_{timestamp}{extension}");
                }
                
                // Move the file
                File.Move(job.InputPath, movedFilePath);
                _logger.LogInformation("Moved source file from {Source} to {Destination}", job.InputPath, movedFilePath);
                
                return movedFilePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not move source file to _moved folder: {FileName}. Processing from original location.", job.FileName);
                return job.InputPath;
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
