using HandbrakeScheduler;

public interface IHandBrakeFileProcessor
{
    void QueueForProcessing(ReceivedFileInfo fileInfo);
}

public class HandBrakeFileProcessor : IHandBrakeFileProcessor
{
    private readonly FileTransferHostSettings _settings;
    private readonly HandBrakeSettings _handBrakeSettings;
    private readonly JobQueue _jobQueue;
    private readonly ILogger<HandBrakeFileProcessor> _logger;
    private readonly VideoInfoService _videoInfoService;


    public HandBrakeFileProcessor(
        FileTransferHostSettings settings,
        HandBrakeSettings handBrakeSettings,
        JobQueue jobQueue,
        ILogger<HandBrakeFileProcessor> logger,
        VideoInfoService videoInfoService)
    {
        _settings = settings;
        _handBrakeSettings = handBrakeSettings;
        _jobQueue = jobQueue;
        _logger = logger;
        _videoInfoService = videoInfoService;

        // Ensure output directory exists


    }

    public void QueueForProcessing(ReceivedFileInfo fileInfo)
    {
        ProcessFileWithHandBrake(fileInfo);

    }


    private async void ProcessFileWithHandBrake(ReceivedFileInfo fileInfo)
    {
        _logger.LogInformation($"Starting HandBrake processing of: {fileInfo.StoredFileName}");

        try
        {
            // Create a HandBrake job based on the media info
            var transcodeJob = await CreateTranscodeJob(fileInfo);

            // Add to the existing job queue
            _jobQueue.EnqueueJob(transcodeJob);

            _logger.LogInformation($"HandBrake job created for: {fileInfo.StoredFileName} -> {Path.GetFileName(transcodeJob.OutputDirectory)}");
            _logger.LogInformation($"Job details - Input: {transcodeJob.InputPath}, Output: {transcodeJob.OutputDirectory}, Preset: {transcodeJob.Preset}");

            // The existing HandBrakeMonitoringService will process this job
            // We don't wait for completion here to avoid blocking
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to create HandBrake job for: {fileInfo.StoredFileName}");
            throw;
        }
    }

    private async Task<TranscodeJob> CreateTranscodeJob(ReceivedFileInfo fileInfo)
    {


        // Determine output directory based on media type

        FolderSetting? settings = null;
        if (fileInfo.RelativeFilePath.Contains("Movie", StringComparison.OrdinalIgnoreCase))
        {
            // For movies, create movie folder
            var movieFolder = SanitizeFileName(fileInfo.FilePath);
            settings = GetFolderSettings("Movie");
        }
        else if (fileInfo.RelativeFilePath.Contains("TV", StringComparison.OrdinalIgnoreCase) || fileInfo.RelativeFilePath.Contains("Series", StringComparison.OrdinalIgnoreCase))
        {
            // For TV shows, create series/season folder structure
            var seriesFolder = SanitizeFileName(fileInfo.RelativeFilePath);
            settings = GetFolderSettings("TV");
        }
        else
        {
            // For other media types, use default settings
            settings = GetFolderSettings("Default");
        }


        // Get file size
        var fileSize = 0L;
        try
        {
            if (File.Exists(fileInfo.FilePath))
            {
                fileSize = new FileInfo(fileInfo.FilePath).Length;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Could not get file size for {fileInfo.FilePath}");
        }
        if (settings == null)
        {
            _logger.LogWarning($"No folder settings found for media type: {fileInfo.RelativeFilePath}");
            throw new InvalidOperationException("No folder settings found for the specified media type.");
        }

        // Get video information to determine the best preset
        string preset = settings.Preset; // Default preset
        try
        {
            var videoInfo = await _videoInfoService.GetVideoInfoAsync(fileInfo.FilePath);
            preset = settings.GetPresetForResolution(videoInfo.Width, videoInfo.Height);
            _logger.LogInformation($"Video resolution detected: {videoInfo.Width}x{videoInfo.Height}, using preset: {preset}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to get video info for {fileInfo.FilePath}, using default preset: {preset}");
        }

        string normalizedPath = PathConverter.NormalizePath(fileInfo.RelativeFilePath);
        string? folder = Path.GetDirectoryName(normalizedPath);
        var outputDirectory = Path.Combine(settings.OutputPath, folder ?? "incoming");
        return new TranscodeJob
        {
            InputPath = fileInfo.FilePath,
            OutputDirectory = outputDirectory,
            Preset = preset,
            DeleteSource = true, // Don't delete source files from AutoMk by default
            CreatedAt = DateTime.Now,
            UseTempFolder = false, // Use temp folder for safety
            FileSizeBytes = fileSize
        };
    }

    private FolderSetting? GetFolderSettings(string mediaType)
    {
        // Try to find a folder configuration that matches the media type
        var matchingFolder = _handBrakeSettings.Folders?.FirstOrDefault(f =>
            f.InputPath.Contains(mediaType, StringComparison.OrdinalIgnoreCase));

        if (matchingFolder == null)
        {

            matchingFolder = _handBrakeSettings?.Folders?.FirstOrDefault();
        }
        return matchingFolder;
    }

    private string SanitizeFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return "Unknown";

        // Remove invalid characters for folder names
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(fileName.Select(c => invalidChars.Contains(c) ? '_' : c));

        // Remove multiple underscores and trim
        return System.Text.RegularExpressions.Regex.Replace(sanitized, @"_+", "_").Trim('_');
    }


}
