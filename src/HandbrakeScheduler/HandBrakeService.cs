using Microsoft.Extensions.Logging;
using ShellProgressBar;
using System.Diagnostics.CodeAnalysis;

namespace HandbrakeScheduler
{
    internal class HandBrakeService
    {
        private readonly HandBrakeCli _cli;
        private readonly HandBrakeSettings _settings;
        private readonly CommandOptions _commandOptions;
        private readonly SchedulerService _schedulerService;
        private readonly ScheduleSettings _scheduleSettings;
        private readonly ILogger<HandBrakeService> _logger;
        public HandBrakeService(

            HandBrakeCli cli,
            HandBrakeSettings settings, CommandOptions commandOptions, SchedulerService schedulerService, ScheduleSettings scheduleSettings, ILogger<HandBrakeService> logger)
        {
            _cli = cli;
            _settings = settings;
            _schedulerService = schedulerService;
            _scheduleSettings = scheduleSettings;
            _logger = logger;
            _commandOptions = commandOptions;
        }

        [RequiresAssemblyFiles("Calls HandbrakeScheduler.SchedulerService.ScheduleTasks()")]
        public async Task DoWork()
        {
            if (!_commandOptions.SchedulerMode)
            {
                _schedulerService.ScheduleTasks();
            }
            _logger.LogInformation("Started Work");
            DateTime startTime = DateTime.Now;
            foreach (FolderSetting folder in _settings.Folders)
            {
                try
                {
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

                        if (_commandOptions.SchedulerMode && _scheduleSettings.StartTime.HasValue && _scheduleSettings.EndTime.HasValue)
                        {
                            if (_scheduleSettings.EndTime.Value < _scheduleSettings.StartTime.Value)
                            {
                                DateTime endTime = startTime.Date.AddDays(1) + _scheduleSettings.EndTime.Value;

                                if (endTime < DateTime.Now)
                                {
                                    _logger.LogInformation("Scheduled EndTime Triggered is before current time, exiting");
                                    return;
                                }

                            }
                            else
                            {
                                DateTime endTime = startTime.Date + _scheduleSettings.EndTime.Value;

                                if (endTime < DateTime.Now)
                                {
                                    _logger.LogInformation("Scheduled EndTime Triggered is before current time, exiting");
                                    return;
                                }
                            }

                        }


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
