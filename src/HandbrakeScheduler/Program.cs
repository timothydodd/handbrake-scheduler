using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace HandbrakeScheduler
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            try
            {
                // Clean up any existing HandBrakeCLI processes
                await CleanupExistingProcesses();

                // Build and run the web app
                var builder = CreateWebApplicationBuilder(args);
                var app = builder.Build();

                // Configure the web app
                ConfigureWebApplication(app);

                // Validate configuration before starting
                ValidateConfiguration(app.Services);

                // Setup graceful shutdown
                SetupProcessExitHandler(app.Services);

                var handBrakeSettings = app.Services.GetRequiredService<HandBrakeSettings>();
                var fileTransferSettings = app.Services.GetRequiredService<FileTransferHostSettings>();

                Console.WriteLine("Starting HandBrake Web Service...");
                Console.WriteLine($"Web API listening on: {fileTransferSettings.ListenUrl}");
                Console.WriteLine($"File upload endpoint: {fileTransferSettings.ListenUrl}/upload");
                Console.WriteLine($"Health check endpoint: {fileTransferSettings.ListenUrl}/health");
                Console.WriteLine("Press Ctrl+C to stop the service.");

                await app.RunAsync(fileTransferSettings.ListenUrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error starting application: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private static WebApplicationBuilder CreateWebApplicationBuilder(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Configure app configuration
            ConfigureAppConfiguration(builder.Configuration, builder.Environment);

            // Configure services
            ConfigureServices(builder);

            // Configure logging
            ConfigureLogging(builder.Logging, builder.Configuration);

            return builder;
        }

        private static void ConfigureAppConfiguration(IConfigurationBuilder config, IWebHostEnvironment environment)
        {
            config.SetBasePath(Directory.GetCurrentDirectory())
                  .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                  .AddJsonFile($"appsettings.{environment.EnvironmentName}.json",
                             optional: true, reloadOnChange: true)
                  .AddEnvironmentVariables("HANDBRAKE_");

#if DEBUG
            // Add user secrets in development mode
            config.AddUserSecrets<Program>();
#endif
        }

        private static void ConfigureServices(WebApplicationBuilder builder)
        {

            IServiceCollection services = builder.Services;
            IConfiguration configuration = builder.Configuration;
            IWebHostEnvironment environment = builder.Environment;
            // Bind and validate HandBrake settings
            var handBrakeSettings = new HandBrakeSettings();
            configuration.GetSection("HandBrake").Bind(handBrakeSettings);

            // Validate settings at startup
            if (!handBrakeSettings.IsValid(out var errors))
            {
                var errorMessage = string.Join(Environment.NewLine, errors);
                throw new InvalidOperationException($"Invalid HandBrake configuration:{Environment.NewLine}{errorMessage}");
            }

            // Bind file transfer settings
            var fileTransferSettings = new FileTransferHostSettings();
            configuration.GetSection("FileTransfer").Bind(fileTransferSettings);

            services.AddSingleton(handBrakeSettings);
            services.AddSingleton(fileTransferSettings);

            // Configure request size limits for large files
            services.Configure<FormOptions>(options =>
            {
                options.MultipartBodyLengthLimit = fileTransferSettings.MaxFileSizeBytes;
                options.ValueLengthLimit = int.MaxValue;
                options.MultipartHeadersLengthLimit = int.MaxValue;
            });

            services.Configure<IISServerOptions>(options =>
            {
                options.MaxRequestBodySize = fileTransferSettings.MaxFileSizeBytes;
            });
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = fileTransferSettings.MaxFileSizeBytes;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
                options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
            });


            // Register HandBrakeCli with proper error handling
            services.AddSingleton<HandBrakeCli>();

            // Register application services
            services.AddSingleton<HandBrakeService>();
            services.AddSingleton<JobQueue>();
            services.AddSingleton<TempFileManager>();

            // Register file transfer services
            services.AddScoped<IFileReceiver, FileReceiver>();
            services.AddScoped<IHandBrakeFileProcessor, HandBrakeFileProcessor>();

            // Register the background monitoring service
            services.AddHostedService<HandBrakeMonitoringService>();

            // Add controllers for API endpoints
            services.AddControllers();

            // Add health checks
            services.AddHealthChecks();
        }

        private static void ConfigureLogging(ILoggingBuilder logging, IConfiguration configuration)
        {
            var handBrakeSettings = new HandBrakeSettings();
            configuration.GetSection("HandBrake").Bind(handBrakeSettings);

            logging.ClearProviders();
            logging.AddConsole(options =>
            {
                options.FormatterName = "custom";
            });
            logging.AddConsoleFormatter<CustomConsoleFormatter, CustomConsoleFormatterOptions>();

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

        private static void ConfigureWebApplication(WebApplication app)
        {
            // Configure middleware pipeline
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            // Health check endpoint
            app.MapHealthChecks("/health");

            // File upload endpoints
            app.MapPost("/upload", async (HttpContext context, IFileReceiver fileReceiver, ILogger<Program> logger) =>
            {
                try
                {
                    var result = await fileReceiver.ReceiveFileAsync(context.Request);

                    if (result.Success)
                    {
                        return Results.Ok(new
                        {
                            Message = "File received successfully",
                            FileName = result.FileName,
                            FilePath = result.FilePath,
                            ProcessingQueued = result.ProcessingQueued
                        });
                    }
                    else
                    {
                        return Results.BadRequest(new { Error = result.ErrorMessage });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error receiving file");
                    return Results.StatusCode(500);
                }
            });

            // List received files endpoint
            app.MapGet("/files", (IFileReceiver fileReceiver) =>
            {
                return Results.Ok(fileReceiver.GetReceivedFiles());
            });

            // List processing jobs endpoint
            app.MapGet("/jobs", (JobQueue jobQueue) =>
            {
                return Results.Ok(new
                {
                    QueueCount = jobQueue.Count,
                    IsProcessing = jobQueue.IsProcessing,
                    IsEmpty = jobQueue.IsEmpty,
                    Status = jobQueue.IsProcessing ? "Processing" : (jobQueue.IsEmpty ? "Idle" : "Queued")
                });
            });

            // Stop processing endpoint (useful for maintenance)
            app.MapPost("/stop", (HandBrakeCli handBrakeCli, ILogger<Program> logger) =>
            {
                try
                {
                    handBrakeCli.StopTranscoding();
                    logger.LogInformation("HandBrake processing stopped via API");
                    return Results.Ok(new { Message = "Processing stopped" });
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error stopping HandBrake processing");
                    return Results.StatusCode(500);
                }
            });

            // Use controllers for more complex endpoints
            app.MapControllers();
        }

        private static void ValidateConfiguration(IServiceProvider services)
        {
            try
            {
                var settings = services.GetRequiredService<HandBrakeSettings>();
                var fileTransferSettings = services.GetRequiredService<FileTransferHostSettings>();
                var logger = services.GetRequiredService<ILogger<Program>>();

                logger.LogInformation("Validating configuration...");

                // Check if HandBrakeCLI exists and is executable
                if (!File.Exists(settings.HandBrakeCliPath))
                {
                    throw new FileNotFoundException($"HandBrakeCLI not found at: {settings.HandBrakeCliPath}");
                }

                // Ensure directories exist
                Directory.CreateDirectory(fileTransferSettings.IncomingDirectory);

                // Log configuration summary
                logger.LogInformation("Configuration validated successfully");
                logger.LogInformation("HandBrakeCLI Path: {HandBrakeCliPath}", settings.HandBrakeCliPath);
                logger.LogInformation("Monitoring Enabled: {MonitoringEnabled}", settings.MonitoringEnabled);
                logger.LogInformation("Configured Folders: {FolderCount}", settings.Folders.Length);
                logger.LogInformation("File Transfer Incoming: {IncomingDirectory}", fileTransferSettings.IncomingDirectory);
                logger.LogInformation("Max File Size: {MaxFileSizeGB} GB", fileTransferSettings.MaxFileSizeBytes / (1024.0 * 1024.0 * 1024.0));

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

        private static void SetupProcessExitHandler(IServiceProvider services)
        {
            AppDomain.CurrentDomain.ProcessExit += async (sender, e) =>
            {
                await PerformGracefulShutdown(services);
            };

            Console.CancelKeyPress += async (sender, e) =>
            {
                e.Cancel = true;
                await PerformGracefulShutdown(services);
                Environment.Exit(0);
            };
        }

        private static async Task PerformGracefulShutdown(IServiceProvider services)
        {
            try
            {
                Console.WriteLine("Shutting down gracefully...");

                // Stop the HandBrakeCLI if running
                var cli = services.GetService<HandBrakeCli>();
                cli?.StopTranscoding();

                // Give time for current operations to complete
                await Task.Delay(5000);
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

    // File transfer integration services
    public interface IFileReceiver
    {
        Task<FileReceiveResult> ReceiveFileAsync(HttpRequest request);
        List<ReceivedFileInfo> GetReceivedFiles();
    }

    public class FileReceiver : IFileReceiver
    {
        private readonly FileTransferHostSettings _settings;
        private readonly ILogger<FileReceiver> _logger;
        private readonly IHandBrakeFileProcessor _fileProcessor;
        private readonly List<ReceivedFileInfo> _receivedFiles = new();

        public FileReceiver(
            FileTransferHostSettings settings,
            ILogger<FileReceiver> logger,
            IHandBrakeFileProcessor fileProcessor)
        {
            _settings = settings;
            _logger = logger;
            _fileProcessor = fileProcessor;

            // Ensure incoming directory exists
            Directory.CreateDirectory(_settings.IncomingDirectory);
        }

        public async Task<FileReceiveResult> ReceiveFileAsync(HttpRequest request)
        {
            if (!request.HasFormContentType)
            {
                return FileReceiveResult.FailureResult("Request must be multipart/form-data");
            }

            var form = await request.ReadFormAsync();

            // Get metadata
            if (!form.TryGetValue("metadata", out var metadataValue))
            {
                return FileReceiveResult.FailureResult("Missing metadata");
            }

            FileTransferRequest? transferRequest;
            try
            {
                transferRequest = JsonSerializer.Deserialize<FileTransferRequest>(metadataValue.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize metadata");
                return FileReceiveResult.FailureResult("Invalid metadata format");
            }

            if (transferRequest == null)
            {
                return FileReceiveResult.FailureResult("Null metadata");
            }

            var file = form.Files.FirstOrDefault();
            if (file == null)
            {
                return FileReceiveResult.FailureResult("No file uploaded");
            }
            // Validate file size
            if (file?.Length > _settings.MaxFileSizeBytes)
            {
                return FileReceiveResult.FailureResult($"File size {file.Length} exceeds maximum {_settings.MaxFileSizeBytes}");
            }

            // Generate unique filename to avoid conflicts
            var fileExtension = Path.GetExtension(transferRequest.OriginalFileName);
            var uniqueFileName = $"{Path.GetFileNameWithoutExtension(transferRequest.OriginalFileName)}_{Guid.NewGuid().ToString()}{fileExtension}";
            var filePath = Path.Combine(_settings.IncomingDirectory, uniqueFileName);

            try
            {
                // Save the file
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 64 * 1024, useAsync: true);

                await file.CopyToAsync(fileStream);
                await fileStream.FlushAsync();

                _logger.LogInformation($"Received file: {uniqueFileName} ({file.Length} bytes)");

                // Create file info record
                var receivedFileInfo = new ReceivedFileInfo
                {
                    OriginalFileName = transferRequest.OriginalFileName,
                    StoredFileName = uniqueFileName,
                    FilePath = filePath,
                    FileSizeBytes = file.Length,
                    ReceivedTimestamp = DateTime.UtcNow,
                    TransferRequest = transferRequest,
                    RelativeFilePath = transferRequest.RelativeFilePath
                };

                _receivedFiles.Add(receivedFileInfo);

                // Queue for processing if enabled

                _fileProcessor.QueueForProcessing(receivedFileInfo);


                return FileReceiveResult.SuccessResult(transferRequest.OriginalFileName, filePath, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to save file: {uniqueFileName}");

                // Clean up partial file
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }
                }
                catch { }

                return FileReceiveResult.FailureResult($"Failed to save file: {ex.Message}");
            }
        }

        public List<ReceivedFileInfo> GetReceivedFiles()
        {
            return new List<ReceivedFileInfo>(_receivedFiles);
        }
    }


    // Settings and data models
    public class FileTransferHostSettings
    {
        public string IncomingDirectory { get; set; } = "incoming";
        public long MaxFileSizeBytes { get; set; } = 50L * 1024 * 1024 * 1024; // 50GB
        public int MaxConcurrentProcessing { get; set; } = 2;
        public string ListenUrl { get; set; } = "http://localhost:5000";
    }

    // Shared data models (should match the client side)
    public class FileTransferRequest
    {
        public string OriginalFileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public MediaFileInfo MediaInfo { get; set; } = new();
        public DateTime TransferTimestamp { get; set; }
        public string? RelativeFilePath { get; set; } = string.Empty;
    }

    public class MediaFileInfo
    {
        public string SeriesTitle { get; set; } = string.Empty;
        public string MovieTitle { get; set; } = string.Empty;
        public int Season { get; set; }
        public int Episode { get; set; }
        public string? Year { get; set; }
        public string MediaType { get; set; } = "Unknown";
        public string? EpisodeTitle { get; set; }
        public string? ImdbId { get; set; }
        public string OriginalDiscName { get; set; } = string.Empty;
        public string? Genre { get; set; }
        public string? Runtime { get; set; }
        public Dictionary<string, string> AdditionalMetadata { get; set; } = new();
    }

    public class FileReceiveResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? FileName { get; set; }
        public string? FilePath { get; set; }
        public bool ProcessingQueued { get; set; }

        public static FileReceiveResult SuccessResult(string fileName, string filePath, bool processingQueued) =>
            new() { Success = true, FileName = fileName, FilePath = filePath, ProcessingQueued = processingQueued };

        public static FileReceiveResult FailureResult(string error) =>
            new() { Success = false, ErrorMessage = error };
    }

    public class ReceivedFileInfo
    {
        public string OriginalFileName { get; set; } = string.Empty;
        public string StoredFileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public DateTime ReceivedTimestamp { get; set; }
        public required string RelativeFilePath { get; set; }
        public FileTransferRequest TransferRequest { get; set; } = new();
    }
}

// Keep your existing custom console formatter
public sealed class CustomConsoleFormatter : ConsoleFormatter
{
    public CustomConsoleFormatter() : base("custom") { }

    public override void Write<TState>(
       in LogEntry<TState> logEntry,
       IExternalScopeProvider? scopeProvider,
       TextWriter textWriter)
    {
        var (logLevel, levelColor) = logEntry.LogLevel switch
        {
            LogLevel.Trace => ("TRACE", ConsoleColor.DarkGray),
            LogLevel.Debug => ("DEBUG", ConsoleColor.Gray),
            LogLevel.Information => ("INFO", ConsoleColor.Green),
            LogLevel.Warning => ("WARN", ConsoleColor.Yellow),
            LogLevel.Error => ("ERROR", ConsoleColor.Red),
            LogLevel.Critical => ("CRITICAL", ConsoleColor.Magenta),
            _ => ("UNKNOWN", ConsoleColor.White)
        };

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);

        // Timestamp in dark gray
        Console.ForegroundColor = ConsoleColor.DarkGray;
        textWriter.Write($"{timestamp} ");

        // Log level in its specific color
        Console.ForegroundColor = levelColor;
        textWriter.Write($"{logLevel,-8} "); // Left-aligned with padding

        // Message in white (or appropriate color based on level)
        Console.ForegroundColor = logEntry.LogLevel >= LogLevel.Warning ? levelColor : ConsoleColor.White;
        textWriter.WriteLine(message);

        Console.ResetColor();

        // Exception output in red with indentation
        if (logEntry.Exception != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            var exceptionLines = logEntry.Exception.ToString().Split('\n');
            foreach (var line in exceptionLines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    textWriter.WriteLine($"    {line.Trim()}");
                }
            }
            Console.ResetColor();
        }
    }
}

public class CustomConsoleFormatterOptions : ConsoleFormatterOptions
{
}
