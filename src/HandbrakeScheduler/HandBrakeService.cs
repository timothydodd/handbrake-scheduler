using HandbrakeScheduler.Services;

namespace HandbrakeScheduler
{
    public class HandBrakeService
    {
        private readonly HandBrakeCli _cli;
        private readonly ILogger<HandBrakeService> _logger;
        private readonly IProgressManager _progress;

        public HandBrakeService(HandBrakeCli cli, ILogger<HandBrakeService> logger, IProgressManager progress)
        {
            _cli = cli;
            _logger = logger;
            _progress = progress;
        }

        public async Task<bool> ProcessSingleJob(TranscodeJob job, TempFileManager tempFileManager, CancellationToken cancellationToken = default)
        {
            string workingFilePath = job.InputPath;

            bool success = true;
            try
            {
                _logger.LogInformation("Processing job: {FileName}", job.FileName);

                workingFilePath = MoveSourceToMovedFolder(job);
                Directory.CreateDirectory(job.OutputDirectory);
                job.InputPath = workingFilePath;

                var outputDirectory = job.OutputDirectory;
                var useStaging = !string.IsNullOrEmpty(job.StagingPath);
                if (useStaging)
                {
                    var tempResult = await HandleRemoteFile(job, tempFileManager, cancellationToken);
                    workingFilePath = tempResult.FilePath;
                    outputDirectory = job.StagingPath;
                }

                var resultFile = await PerformTranscoding(job, workingFilePath, outputDirectory, cancellationToken);
                if (useStaging)
                {
                    await CopyResultToOutput(job, resultFile, tempFileManager, cancellationToken);
                    DeleteFileIfExists(resultFile);
                }

                _logger.LogInformation("Successfully processed job: {FileName}", job.FileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing job: {FileName}. File will remain in _moved folder at: {MovedPath}",
                    job.FileName, workingFilePath);
                success = false;
            }
            finally
            {
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

                var taskId = _progress.CreateProgressTask($"Copy → temp: {job.FileName}", category: "Copy");
                try
                {
                    var copyProgress = new Progress<double>(percent =>
                        _progress.UpdateProgress(taskId, percent));

                    var tempPath = await tempFileManager.CopyToTempAsync(job.InputPath, copyProgress, cancellationToken);
                    job.TempFilePath = tempPath;
                    return (tempPath, true);
                }
                finally
                {
                    _progress.CompleteProgressTask(taskId);
                }
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
            var taskId = _progress.CreateProgressTask($"Transcode: {job.FileName}", category: "Transcode");

            try
            {
                var resultFile = await _cli.Transcode(
                    workingFilePath,
                    outputDirectory,
                    job.Preset,
                    status =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        var description = status.AverageFps > 0
                            ? $"Transcode: {job.FileName} [{status.AverageFps:F1} fps avg]"
                            : $"Transcode: {job.FileName}";

                        var eta = status.Estimated > TimeSpan.Zero ? status.Estimated : (TimeSpan?)null;
                        _progress.UpdateProgress(taskId, status.Percentage, description);
                        _progress.UpdateProgressBytes(taskId, 0, 0, null, eta);
                    },
                    true);

                if (resultFile == null)
                {
                    throw new InvalidOperationException($"Transcoding failed for {job.FileName}. Result file is null.");
                }

                return resultFile;
            }
            finally
            {
                _progress.CompleteProgressTask(taskId);
            }
        }

        private async Task CopyResultToOutput(
            TranscodeJob job,
            string resultFile,
            TempFileManager tempFileManager,
            CancellationToken cancellationToken)
        {
            var taskId = _progress.CreateProgressTask($"Copy → output: {job.FileName}", category: "Copy");
            try
            {
                var copyProgress = new Progress<double>(percent =>
                    _progress.UpdateProgress(taskId, percent));

                var resultFileName = FileUtil.GetFileNameWithNewExtension(job.FileName, ".mp4");
                var outputPath = Path.Combine(job.OutputDirectory, resultFileName);

                await tempFileManager.CopyFileAsync(resultFile, outputPath, copyProgress, cancellationToken);

                _logger.LogInformation("Copied result file to output directory: {OutputDirectory}", job.OutputDirectory);
            }
            finally
            {
                _progress.CompleteProgressTask(taskId);
            }
        }

        private string MoveSourceToMovedFolder(TranscodeJob job)
        {
            try
            {
                var inputDirectory = Path.GetDirectoryName(job.InputPath);
                if (string.IsNullOrEmpty(inputDirectory))
                {
                    _logger.LogWarning("Could not determine input directory for: {FileName}", job.FileName);
                    return job.InputPath;
                }

                var movedFolderPath = Path.Combine(inputDirectory, "_moved");
                Directory.CreateDirectory(movedFolderPath);

                var movedFilePath = Path.Combine(movedFolderPath, job.FileName);

                if (File.Exists(movedFilePath))
                {
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(job.FileName);
                    var extension = Path.GetExtension(job.FileName);
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    movedFilePath = Path.Combine(movedFolderPath, $"{fileNameWithoutExt}_{timestamp}{extension}");
                }

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
    }
}
