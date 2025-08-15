using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HandbrakeScheduler
{
    public class VideoInfoService
    {
        private readonly ILogger<VideoInfoService> _logger;
        private readonly HandBrakeSettings _settings;

        public VideoInfoService(ILogger<VideoInfoService> logger, HandBrakeSettings settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public async Task<VideoInfo> GetVideoInfoAsync(string filePath)
        {
            try
            {
                // Use HandBrake CLI to scan the video file
                var scanOutput = await ScanVideoWithHandBrake(filePath);
                return ParseHandBrakeScanOutput(scanOutput);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to get video info for {FilePath}: {Error}", filePath, ex.Message);
                return new VideoInfo();
            }
        }

        private async Task<string> ScanVideoWithHandBrake(string filePath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _settings.HandBrakeCliPath,
                Arguments = $"--scan --input \"{filePath}\" --title 0",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            var output = new List<string>();
            var error = new List<string>();

            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    output.Add(e.Data);
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    error.Add(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            bool completed;
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                completed = true;
            }
            catch (TimeoutException)
            {
                completed = false;
            }
            
            if (!completed)
            {
                try
                {
                    process.Kill();
                }
                catch { }
                throw new TimeoutException("HandBrake scan timed out");
            }

            // HandBrake outputs scan results to stderr
            return string.Join("\n", error);
        }

        private VideoInfo ParseHandBrakeScanOutput(string scanOutput)
        {
            var videoInfo = new VideoInfo();

            // Parse resolution from HandBrake scan output
            // Looking for patterns like: "  + size: 3840x2160" or "  + dimensions: 3840 x 2160"
            var sizeMatch = Regex.Match(scanOutput, @"\+\s+size:\s+(\d+)x(\d+)", RegexOptions.IgnoreCase);
            if (!sizeMatch.Success)
            {
                // Try alternative format
                sizeMatch = Regex.Match(scanOutput, @"\+\s+dimensions:\s+(\d+)\s*x\s*(\d+)", RegexOptions.IgnoreCase);
            }

            if (sizeMatch.Success)
            {
                if (int.TryParse(sizeMatch.Groups[1].Value, out var width))
                    videoInfo.Width = width;
                if (int.TryParse(sizeMatch.Groups[2].Value, out var height))
                    videoInfo.Height = height;
            }

            // Parse duration
            var durationMatch = Regex.Match(scanOutput, @"\+\s+duration:\s+(\d+):(\d+):(\d+)", RegexOptions.IgnoreCase);
            if (durationMatch.Success)
            {
                if (int.TryParse(durationMatch.Groups[1].Value, out var hours) &&
                    int.TryParse(durationMatch.Groups[2].Value, out var minutes) &&
                    int.TryParse(durationMatch.Groups[3].Value, out var seconds))
                {
                    videoInfo.Duration = new TimeSpan(hours, minutes, seconds);
                }
            }

            // Parse frame rate
            var fpsMatch = Regex.Match(scanOutput, @"\+\s+rate:\s+([\d.]+)\s+fps", RegexOptions.IgnoreCase);
            if (fpsMatch.Success)
            {
                if (double.TryParse(fpsMatch.Groups[1].Value, out var fps))
                    videoInfo.FrameRate = fps;
            }

            // Determine resolution category
            if (videoInfo.Width.HasValue && videoInfo.Height.HasValue)
            {
                videoInfo.ResolutionCategory = DetermineResolutionCategory(videoInfo.Width.Value, videoInfo.Height.Value);
            }

            _logger.LogDebug("Detected video info - Resolution: {Width}x{Height}, Category: {Category}, FPS: {FPS}",
                videoInfo.Width, videoInfo.Height, videoInfo.ResolutionCategory, videoInfo.FrameRate);

            return videoInfo;
        }

        private string DetermineResolutionCategory(int width, int height)
        {
            // Determine based on height primarily
            if (height >= 2160)
                return "4K";
            else if (height >= 1440)
                return "1440p";
            else if (height >= 1080)
                return "1080p";
            else if (height >= 720)
                return "720p";
            else if (height >= 480)
                return "480p";
            else
                return "SD";
        }
    }

    public class VideoInfo
    {
        public int? Width { get; set; }
        public int? Height { get; set; }
        public TimeSpan? Duration { get; set; }
        public double? FrameRate { get; set; }
        public string ResolutionCategory { get; set; } = "Unknown";
    }
}