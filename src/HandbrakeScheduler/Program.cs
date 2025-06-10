using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HandbrakeScheduler
{
    internal class Program
    {
        [Obsolete]
        private static async Task Main(string[] args)
        {
            try
            {
                // Clean up any existing HandBrakeCLI processes
                await CleanupExistingProcesses();

                // Build and run the host
                var host = CreateHostBuilder(args).Build();

                // Validate configuration before starting
                ValidateConfiguration(host);

                // Setup graceful shutdown
                SetupProcessExitHandler(host);

                Console.WriteLine("Starting HandBrake monitoring service...");
                Console.WriteLine("Press Ctrl+C to stop the service.");

                await host.RunAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error starting application: {ex.Message}");
                Environment.Exit(1);
            }
        }

        [Obsolete]
        private static IHostBuilder CreateHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration(ConfigureAppConfiguration)
                .ConfigureServices(ConfigureServices)
                .ConfigureLogging(ConfigureLogging);
        }

        private static void ConfigureAppConfiguration(HostBuilderContext context, IConfigurationBuilder config)
        {
            config.SetBasePath(Directory.GetCurrentDirectory())
                  .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                  .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json",
                             optional: true, reloadOnChange: true)
                  .AddEnvironmentVariables("HANDBRAKE_");


#if DEBUG
            // Add user secrets in development mode
            config.AddUserSecrets<Program>();
#endif
        }

        private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
        {
            // Bind and validate HandBrake settings
            var handBrakeSettings = new HandBrakeSettings();
            context.Configuration.GetSection("HandBrake").Bind(handBrakeSettings);

            // Validate settings at startup
            if (!handBrakeSettings.IsValid(out var errors))
            {
                var errorMessage = string.Join(Environment.NewLine, errors);
                throw new InvalidOperationException($"Invalid HandBrake configuration:{Environment.NewLine}{errorMessage}");
            }

            services.AddSingleton(handBrakeSettings);

            // Register HandBrakeCli with proper error handling
            services.AddSingleton<HandBrakeCli>(provider =>
            {
                var settings = provider.GetRequiredService<HandBrakeSettings>();
                return new HandBrakeCli(settings.HandBrakeCliPath);
            });

            // Register application services
            services.AddSingleton<HandBrakeService>();
            services.AddSingleton<JobQueue>();
            services.AddSingleton<TempFileManager>();

            // Register the main hosted service
            services.AddHostedService<HandBrakeMonitoringService>();
        }


        private static void ConfigureLogging(HostBuilderContext context, ILoggingBuilder logging)
        {
            var handBrakeSettings = new HandBrakeSettings();
            context.Configuration.GetSection("HandBrake").Bind(handBrakeSettings);

            logging.ClearProviders();
            logging.AddConsole();
            // Add file logging if enabled and path is specified
            if (handBrakeSettings.EnableLogging && !string.IsNullOrEmpty(handBrakeSettings.LogFilePath))
            {
                try
                {
                    logging.AddFile(options =>
                    {
                        options.RootPath = Path.GetDirectoryName(handBrakeSettings.LogFilePath) ?? AppContext.BaseDirectory;

                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not configure file logging: {ex.Message}");
                    // Fall back to default file logging
                    logging.AddFile(options => options.RootPath = AppContext.BaseDirectory);
                }
            }
            else
            {
                // Default file logging
                logging.AddFile(options => options.RootPath = AppContext.BaseDirectory);
            }

#if DEBUG
            logging.SetMinimumLevel(LogLevel.Debug);
#else
            logging.SetMinimumLevel(LogLevel.Information);
#endif
        }

        private static void ValidateConfiguration(IHost host)
        {
            try
            {
                var settings = host.Services.GetRequiredService<HandBrakeSettings>();
                var logger = host.Services.GetRequiredService<ILogger<Program>>();

                logger.LogInformation("Validating configuration...");

                // Check if HandBrakeCLI exists and is executable
                if (!File.Exists(settings.HandBrakeCliPath))
                {
                    throw new FileNotFoundException($"HandBrakeCLI not found at: {settings.HandBrakeCliPath}");
                }

                // Log configuration summary
                logger.LogInformation("Configuration validated successfully");
                logger.LogInformation("HandBrakeCLI Path: {HandBrakeCliPath}", settings.HandBrakeCliPath);
                logger.LogInformation("Monitoring Enabled: {MonitoringEnabled}", settings.MonitoringEnabled);
                logger.LogInformation("Configured Folders: {FolderCount}", settings.Folders.Length);

                if (settings.HasTimeRestrictions)
                {
                    logger.LogInformation("Time Restrictions: {StartTime} - {EndTime}",
                        settings.StartTime, settings.EndTime);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Configuration validation failed: {ex.Message}");
                throw;
            }
        }

        private static void SetupProcessExitHandler(IHost host)
        {
            AppDomain.CurrentDomain.ProcessExit += async (sender, e) =>
            {
                await PerformGracefulShutdown(host);
            };

            Console.CancelKeyPress += async (sender, e) =>
            {
                e.Cancel = true;
                await PerformGracefulShutdown(host);
                Environment.Exit(0);
            };
        }

        private static async Task PerformGracefulShutdown(IHost host)
        {
            try
            {
                Console.WriteLine("Shutting down gracefully...");

                // Stop the HandBrakeCLI if running
                var cli = host.Services.GetService<HandBrakeCli>();
                cli?.StopTranscoding();

                // Stop the host
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await host.StopAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during graceful shutdown: {ex.Message}");
            }
            finally
            {
                // Force cleanup of any remaining processes
                await CleanupExistingProcesses();
            }
        }

        private static async Task CleanupExistingProcesses()
        {
            try
            {
                var processes = Process.GetProcessesByName("HandBrakeCli");
                if (processes.Length > 0)
                {
                    Console.WriteLine($"Found {processes.Length} existing HandBrakeCLI process(es). Cleaning up...");

                    foreach (var process in processes)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                                await process.WaitForExitAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Warning: Could not kill HandBrakeCLI process {process.Id}: {ex.Message}");
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during process cleanup: {ex.Message}");
            }
        }
    }
}
