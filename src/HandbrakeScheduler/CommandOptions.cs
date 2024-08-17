using CommandLine;

namespace HandbrakeScheduler
{
    internal class CommandOptions
    {
        [Option('s', "Scheduler Mode", Required = false)]
        public bool SchedulerMode { get; set; }


    }
}
