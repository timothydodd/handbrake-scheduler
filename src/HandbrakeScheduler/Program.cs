using CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace HandbrakeScheduler
{
    internal class Program
    {
        private static ServiceProvider? _serviceProvider = null;

        private static IConfigurationRoot? _configuration;

        [RequiresUnreferencedCode("Calls HandbrakeScheduler.Program.Configure()")]
        [RequiresDynamicCode("Calls HandbrakeScheduler.Program.Configure()")]
        [RequiresAssemblyFiles("Calls HandbrakeScheduler.HandBrakeService.DoWork()")]
        private static async Task Main(string[] args)
        {
            KillAllHandBrakeCli();
            Configure(args);
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(CurrentDomain_ProcessExit);

            Console.WriteLine("..enter Ctrl+C or Ctrl+Break to exit..");

            HandBrakeService? importer = _serviceProvider.GetService<HandBrakeService>();

            ILogger<Program>? logger = _serviceProvider.GetService<ILogger<Program>>();

            Console.CancelKeyPress +=
                new ConsoleCancelEventHandler((a, b) =>
                {

                    logger.LogInformation("HandbrakeScheduler Shutting Down");
                    Environment.Exit(0);
                });



            await importer.DoWork();

        }
        [RequiresUnreferencedCode("Calls Microsoft.Extensions.Configuration.ConfigurationBinder.Get<T>()")]
        [RequiresDynamicCode("Calls Microsoft.Extensions.Configuration.ConfigurationBinder.Get<T>()")]
        private static void Configure(string[] args)
        {
            IConfigurationBuilder builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();


#if DEBUG
            builder = builder.AddUserSecrets<Program>();
#endif
            _configuration = builder.Build();



            IServiceCollection services = new ServiceCollection()
                .AddSingleton<IServiceProvider>(c => _serviceProvider);
            ParserResult<CommandOptions> results = Parser.Default.ParseArguments<CommandOptions>(args);
            CommandOptions commandOptions = new();
            if (results.Tag == ParserResultType.Parsed)
            {
                CommandOptions? options = (results as Parsed<CommandOptions>)?.Value;
                if (options != null)
                {
                    commandOptions = options;
                    Console.WriteLine("Scheduler Mode: " + commandOptions.SchedulerMode.ToString());
                }
            }
            ScheduleSettings? scheduleSettings = _configuration.GetSection("Schedule").Get<ScheduleSettings>();
            services.AddSingleton(x => scheduleSettings);
            services.AddSingleton(x => commandOptions);
            services.AddSingleton<SchedulerService>();
            HandBrakeSettings? handBrakeSettings = _configuration.GetSection("HandBrake").Get<HandBrakeSettings>();
            services.AddSingleton(x => handBrakeSettings);
            services.AddSingleton(x => { return new HandBrakeCli(handBrakeSettings.HandBrakeCliPath); });
            services.AddLogging((loggingBuilder) =>
            {
                loggingBuilder.AddConfiguration(_configuration.GetSection("Logging"));
                loggingBuilder
                    .SetMinimumLevel(LogLevel.Trace)
                    .AddConsole()
                    .AddFile(o => o.RootPath = AppContext.BaseDirectory);
            });
            services.AddSingleton<HandBrakeService>();


            _serviceProvider = services.BuildServiceProvider();


        }
        private static void KillAllHandBrakeCli()
        {
            foreach (Process node in Process.GetProcessesByName("HandBrakeCli"))
            {
                try { node.Kill(); }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }

            }
        }
        private static void CurrentDomain_ProcessExit(object sender, EventArgs e)
        {
            HandBrakeCli? cli = _serviceProvider.GetService<HandBrakeCli>();
            cli.StopTranscoding();
            KillAllHandBrakeCli();
        }
    }
}
