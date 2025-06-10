using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HandbrakeScheduler
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            KillAllHandBrakeCli();

            IConfigurationBuilder builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory());

            IConfiguration configuration = builder.Build();

            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.AddConfiguration(configuration);
                    config.AddEnvironmentVariables();
                    // Optionally add command line arguments if needed
                    if (args != null && args.Length > 0)
                    {
                        config.AddCommandLine(args);
                    }
                    // Add user secrets in development mode
#if DEBUG
                    config.AddUserSecrets<Program>();
#endif
                })
                .ConfigureServices((context, services) =>
                {
                    // Parse command line arguments


                    // Bind HandBrake settings
                    var handBrakeSettings = new HandBrakeSettings();
                    context.Configuration.GetSection("HandBrake").Bind(handBrakeSettings);
                    services.AddSingleton(handBrakeSettings);



                    // Register HandBrakeCli
                    services.AddSingleton(provider =>
                    {
                        var settings = provider.GetService<HandBrakeSettings>();
                        return new HandBrakeCli(settings.HandBrakeCliPath);
                    });

                    // Register services
                    services.AddSingleton<HandBrakeService>();
                    services.AddSingleton<JobQueue>();
                    services.AddSingleton<TempFileManager>();

                    // Register the hosted service
                    services.AddHostedService<HandBrakeMonitoringService>();
                })
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.AddFile(o => o.RootPath = AppContext.BaseDirectory);
                    logging.SetMinimumLevel(LogLevel.Information);
                })
                .Build();

            // Setup process exit handler
            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                try
                {
                    var cli = host.Services.GetService<HandBrakeCli>();
                    cli?.StopTranscoding();
                }
                catch
                {

                }
                KillAllHandBrakeCli();
            };

            Console.WriteLine("Starting HandBrake monitoring service...");
            Console.WriteLine("Press Ctrl+C to stop the service.");

            await host.RunAsync();
        }

        private static void KillAllHandBrakeCli()
        {
            foreach (Process node in Process.GetProcessesByName("HandBrakeCli"))
            {
                try
                {
                    node.Kill();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error killing HandBrakeCli process: {ex.Message}");
                }
            }
        }
    }
}
