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
                _logger.LogInformation("Job details before processing:\n{Diagnostics}", BuildJobDiagnostics(job, workingFilePath));

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

                    // HandBrake transcodes into the staging folder first; make sure it exists,
                    // otherwise the encode fails with "avio_open2 errno -2".
                    Directory.CreateDirectory(outputDirectory);
                    _logger.LogInformation("Ensured staging directory exists: {StagingDir}", outputDirectory);
                }

                var plannedOutputFile = Path.Combine(
                    Path.GetFullPath(outputDirectory),
                    FileUtil.GetFileNameWithNewExtension(workingFilePath, ".mp4"));
                _logger.LogInformation(
                    "Transcoding {FileName}: input={Input}, transcode output dir={OutputDir} (staging={UseStaging}), planned output file={OutputFile}, final destination dir={FinalDir}",
                    job.FileName, workingFilePath, outputDirectory, useStaging, plannedOutputFile, job.OutputDirectory);

                // Pre-flight: probe whether we can actually create the output file before launching
                // HandBrake. HandBrake's "avio_open2 errno -2" gives no path detail, so we surface the
                // real reason here. We only WARN (not throw) so the encode still runs and you keep
                // seeing progress; if HandBrake then fails on output, this line explains why.
                var outDirExists = Directory.Exists(outputDirectory);
                var outDirWritable = IsDirectoryWritable(outputDirectory);
                if (!outDirExists || !outDirWritable)
                {
                    _logger.LogWarning(
                        "Output pre-flight FAILED for {FileName}: dir={OutputDir} exists={Exists} writable={Writable}. " +
                        "This is the likely cause of HandBrake's 'avio_open2 errno -2'. " +
                        "Check the volume is mounted, not read-only, and the process has write/Full Disk Access.",
                        job.FileName, outputDirectory, outDirExists, outDirWritable);
                }
                else
                {
                    _logger.LogInformation(
                        "Output pre-flight OK for {FileName}: dir={OutputDir} exists=True writable=True",
                        job.FileName, outputDirectory);
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
                _logger.LogError(ex,
                    "Error processing job: {FileName}. File will remain in _moved folder at: {MovedPath}\nFull job diagnostics:\n{Diagnostics}",
                    job.FileName, workingFilePath, BuildJobDiagnostics(job, workingFilePath));
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

        private static string BuildJobDiagnostics(TranscodeJob job, string workingFilePath)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine($"  Id:               {job.Id}");
            sb.AppendLine($"  FileName:         {job.FileName}");
            sb.AppendLine($"  Preset:           {job.Preset}");
            sb.AppendLine($"  Status:           {job.Status}");
            sb.AppendLine($"  RetryCount:       {job.RetryCount}");
            sb.AppendLine($"  FileSizeBytes:    {job.FileSizeBytes:N0} ({job.FileSizeBytes / 1024.0 / 1024.0:F1} MB)");
            sb.AppendLine($"  DeleteSource:     {job.DeleteSource}");
            sb.AppendLine($"  LastError:        {job.LastError}");

            // Input
            sb.AppendLine($"  Original InputPath: {job.InputPath}");
            DescribePath(sb, "Working input file", workingFilePath, expectFile: true);

            // Staging / temp
            sb.AppendLine($"  StagingPath:      {(string.IsNullOrEmpty(job.StagingPath) ? "(none)" : job.StagingPath)}");
            if (!string.IsNullOrEmpty(job.StagingPath))
                DescribePath(sb, "Staging dir", job.StagingPath, expectFile: false);
            sb.AppendLine($"  TempFilePath:     {job.TempFilePath ?? "(none)"}");

            // Output
            DescribePath(sb, "Output dir", job.OutputDirectory, expectFile: false);
            try
            {
                var plannedOutputFile = Path.Combine(
                    Path.GetFullPath(job.OutputDirectory),
                    FileUtil.GetFileNameWithNewExtension(workingFilePath, ".mp4"));
                sb.AppendLine($"  Planned output:   {plannedOutputFile}");
                sb.AppendLine($"    output exists already: {File.Exists(plannedOutputFile)}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  Planned output:   (could not compute: {ex.Message})");
            }

            return sb.ToString().TrimEnd();
        }

        private static void DescribePath(System.Text.StringBuilder sb, string label, string path, bool expectFile)
        {
            sb.AppendLine($"  {label}: {path}");
            if (string.IsNullOrEmpty(path))
            {
                sb.AppendLine("    (path is empty)");
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (fullPath != path)
                    sb.AppendLine($"    resolved: {fullPath}");

                if (expectFile)
                {
                    var exists = File.Exists(path);
                    sb.AppendLine($"    file exists: {exists}");
                    if (exists)
                        sb.AppendLine($"    size on disk: {new FileInfo(path).Length:N0} bytes");
                }

                var dir = expectFile ? Path.GetDirectoryName(path) : path;
                if (!string.IsNullOrEmpty(dir))
                {
                    sb.AppendLine($"    containing dir exists: {Directory.Exists(dir)}");
                    sb.AppendLine($"    containing dir writable: {IsDirectoryWritable(dir)}");

                    try
                    {
                        var root = Path.GetPathRoot(Path.GetFullPath(dir));
                        if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                        {
                            var drive = new DriveInfo(root);
                            sb.AppendLine($"    volume: {drive.Name} type={drive.DriveType} format={drive.DriveFormat} freeSpace={drive.AvailableFreeSpace:N0} bytes");
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"    volume info unavailable: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"    (error inspecting path: {ex.GetType().Name}: {ex.Message})");
            }
        }

        private static bool IsDirectoryWritable(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                    return false;

                var probe = Path.Combine(dir, $".hbsched_write_test_{Guid.NewGuid():N}");
                using (File.Create(probe)) { }
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
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
