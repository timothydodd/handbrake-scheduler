using Microsoft.Extensions.Logging;
using Microsoft.Win32.TaskScheduler;
using System.Diagnostics.CodeAnalysis;

namespace HandbrakeScheduler
{
    public class SchedulerService
    {
        private const string Name = "HandbrakeScheduler";
        private readonly ScheduleSettings _scheduleSettings;
        private readonly ILogger<SchedulerService> _logger;
        public SchedulerService(ScheduleSettings scheduleSettings, ILogger<SchedulerService> logger)
        {
            _scheduleSettings = scheduleSettings;
            _logger = logger;
        }
        [RequiresAssemblyFiles()]
        public void ScheduleTasks()
        {

            Microsoft.Win32.TaskScheduler.Task task = TaskService.Instance.GetTask(Name);

            if (task != null)
            {
                TaskService.Instance.RootFolder.DeleteTask(Name);
                _logger.LogInformation("Deleted Schedule Task");
            }
            if (_scheduleSettings.Enabled && _scheduleSettings.StartTime.HasValue)
            {
                TaskDefinition td = TaskService.Instance.NewTask();
                DailyTrigger tt = new();
                td.RegistrationInfo.Description = Name;
                tt.Enabled = true;
                tt.StartBoundary = DateTime.Today + _scheduleSettings.StartTime.Value;
                td.Settings.DisallowStartIfOnBatteries = true;
                td.Settings.WakeToRun = true;

                _ = td.Triggers.Add(tt);
                string path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string directory = Path.GetDirectoryName(path);

                string strExeFilePath = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".exe");
                _ = td.Actions.Add(strExeFilePath, "-s", directory);

                _ = TaskService.Instance.RootFolder.RegisterTaskDefinition(Name, td, TaskCreation.CreateOrUpdate, _scheduleSettings.User, _scheduleSettings.Password, TaskLogonType.Password);

                _logger.LogInformation("Created Schedule Task");
            }
        }
    }
    public class ScheduleSettings
    {
        public bool Enabled { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public required string User { get; set; }
        public required string Password { get; set; }
    }

}
