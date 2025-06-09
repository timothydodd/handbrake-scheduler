using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ShellProgressBar;

namespace HandbrakeScheduler
{
    internal class HandBrakeService
    {
        private readonly HandBrakeCli _cli;
        private readonly HandBrakeSettings _settings;
        private readonly CommandOptions _commandOptions;
        private readonly ILogger<HandBrakeService> _logger;
        public HandBrakeService(

            HandBrakeCli cli,
            HandBrakeSettings settings, CommandOptions commandOptions, ILogger<HandBrakeService> logger)
        {
            _cli = cli;
            _logger = logger;
            _commandOptions = commandOptions;
            _settings = settings;
        }


        public async Task DoWork()
        {

            _logger.LogInformation("Started Work");
            string version = await _cli.GetVersionAsync();
            Console.WriteLine($"HandBrake Version: {version}");
            DateTime startTime = DateTime.Now;
            foreach (FolderSetting folder in _settings.Folders)
            {
                try
                {
                    if (Directory.Exists(folder.InputPath) == false)
                    {
                        _logger.LogWarning("Folder {folder} does not exist", folder.InputPath);
                        continue;
                    }
                    IEnumerable<string> files = FindVideos(folder.InputPath, folder.FileExtensions);
                    foreach (string f in files)
                    {
                        string inputNestedPath = Path.GetDirectoryName(f).Replace(folder.InputPath, "", StringComparison.InvariantCultureIgnoreCase);

                        string outputDirectory = Path.Combine(folder.OutputPath, inputNestedPath);
                        string fileName = Path.GetFileName(f);
                        ProgressBar bar = new(100, "Transcoding " + f, new ProgressBarOptions
                        {
                            ForegroundColor =
                            ConsoleColor.Yellow,
                            BackgroundColor = ConsoleColor.DarkGray,
                            ProgressCharacter = '─'
                        });
                        await _cli.Transcode(f, outputDirectory, folder.Preset, (s) =>
                        {
                            bar.Tick((int)s.Percentage, s.Estimated, $"{fileName} - AverageFps: {s.AverageFps}");
                        }, true, folder.DeleteSource);



                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error Processing Folder {folder}", folder.InputPath);
                }



            }
            _logger.LogInformation("Finished Work");


        }

        private IEnumerable<string> FindVideos(string folder, string[] extensions)
        {

            return
                Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(s => extensions.Contains(Path.GetExtension(s), StringComparer.InvariantCultureIgnoreCase));
        }
     
    }
}
