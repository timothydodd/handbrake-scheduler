using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using HandbrakeScheduler.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Spectre.Console;

namespace HandbrakeScheduler
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            try
            {
                // Ensure unicode bar/spinner glyphs render correctly on Windows consoles.
                Console.OutputEncoding = System.Text.Encoding.UTF8;

                DisplayStartupBanner();

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

                var fileTransferSettings = app.Services.GetRequiredService<FileTransferHostSettings>();

                AnsiConsole.MarkupLine("[green]Starting HandBrake Web Service...[/]");
                AnsiConsole.MarkupLine($"[dim]Web API:[/] [cyan]{Markup.Escape(fileTransferSettings.ListenUrl)}[/]");
                AnsiConsole.MarkupLine($"[dim]Upload:[/]  [cyan]{Markup.Escape(fileTransferSettings.ListenUrl)}/upload[/]");
                AnsiConsole.MarkupLine($"[dim]Health:[/]  [cyan]{Markup.Escape(fileTransferSettings.ListenUrl)}/health[/]");
                AnsiConsole.MarkupLine("[dim]Press Ctrl+C to stop the service.[/]");

                await app.RunAsync(fileTransferSettings.ListenUrl);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Fatal error starting application:[/] {Markup.Escape(ex.Message)}");
                Environment.Exit(1);
            }
        }

        private static void DisplayStartupBanner()
        {
            var banner = new Panel(
                new Markup("[cyan]HandBrake Scheduler[/]\n\n" +
                           "[white]Automated video transcoding with HandBrakeCLI[/]\n\n" +
                           "[dim]•[/] Folder monitoring & web upload intake\n" +
                           "[dim]•[/] Scheduled time-window processing\n" +
                           "[dim]•[/] Resolution-aware preset selection"))
            {
                Border = BoxBorder.Double,
                BorderStyle = new Style(Color.Cyan1),
                Padding = new Padding(2, 0, 2, 0)
            };

            AnsiConsole.Write(banner);
            AnsiConsole.WriteLine();
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


            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = fileTransferSettings.MaxFileSizeBytes;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
                options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(10);
                // Disable slow-client termination. Default (240 B/s) can abort
                // legitimate multi-hour uploads of 60+ GB files over slow links.
                options.Limits.MinRequestBodyDataRate = null;
                options.Limits.MinResponseDataRate = null;
            });


            // Dashboard (Spectre.Console live region). Register the hosted starter BEFORE the
            // monitoring service so the live region is up before any transcode logs fire.
            services.AddSingleton(new ProgressManagerOptions());
            services.AddSingleton<DashboardRenderer>();
            services.AddSingleton<IProgressManager, ProgressManager>();
            services.AddHostedService<DashboardHostedService>();

            // Register HandBrakeCli with proper error handling
            services.AddSingleton<HandBrakeCli>();

            // Register application services
            services.AddSingleton<HandBrakeService>();
            services.AddSingleton<JobQueue>();
            services.AddSingleton<TempFileManager>();
            services.AddSingleton<VideoInfoService>();

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

            // Route console logs through the Spectre.Console dashboard. Writing directly to
            // Console.Out would corrupt the pinned progress panel, so no AddConsole here.
            logging.Services.AddSingleton<ILoggerProvider>(sp =>
                new DashboardLoggerProvider(sp.GetRequiredService<DashboardRenderer>()));

            // Resolve where the log file should live. Default to the application's base
            // directory (the bin folder) so logs are always written next to the executable.
            // If LogFilePath is set, honour its directory + filename instead.
            string rootPath = AppContext.BaseDirectory;
            string fileName = "handbrake-<date:yyyyMMdd>.log";

            if (handBrakeSettings.EnableLogging && !string.IsNullOrEmpty(handBrakeSettings.LogFilePath))
            {
                rootPath = Path.GetDirectoryName(handBrakeSettings.LogFilePath) ?? AppContext.BaseDirectory;
                var configuredName = Path.GetFileName(handBrakeSettings.LogFilePath);
                if (!string.IsNullOrEmpty(configuredName))
                    fileName = configuredName;
            }

            try
            {
                // Karambolo's AddFile only writes when it has an explicit file entry; RootPath
                // alone produces no output. Configure both so a log file is always created.
                logging.AddFile(options =>
                {
                    options.RootPath = rootPath;
                    options.FileAccessMode = Karambolo.Extensions.Logging.File.LogFileAccessMode.KeepOpenAndAutoFlush;
                    options.Files = new[]
                    {
                        new Karambolo.Extensions.Logging.File.LogFileOptions { Path = fileName }
                    };
                });

                AnsiConsole.MarkupLine($"[grey]Logging to file:[/] {Markup.Escape(Path.Combine(rootPath, fileName))}");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not configure file logging: {Markup.Escape(ex.Message)}");
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
                AnsiConsole.MarkupLine($"[red]Configuration validation failed:[/] {Markup.Escape(ex.Message)}");
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
                // Stop the dashboard first so subsequent output isn't swallowed by the live region.
                var renderer = services.GetService<DashboardRenderer>();
                if (renderer != null)
                {
                    await renderer.StopAsync();
                }

                AnsiConsole.MarkupLine("[yellow]Shutting down gracefully...[/]");

                var cli = services.GetService<HandBrakeCli>();
                cli?.StopTranscoding();

                await Task.Delay(5000);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error during graceful shutdown:[/] {Markup.Escape(ex.Message)}");
            }
            finally
            {
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
                    AnsiConsole.MarkupLine($"[yellow]Found {processes.Length} existing HandBrakeCLI process(es). Cleaning up...[/]");

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
                            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not kill HandBrakeCLI process {process.Id}: {Markup.Escape(ex.Message)}");
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
                AnsiConsole.MarkupLine($"[red]Error during process cleanup:[/] {Markup.Escape(ex.Message)}");
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

        private const int UploadBufferSize = 1024 * 1024; // 1 MB — good balance for sustained sequential writes

        public async Task<FileReceiveResult> ReceiveFileAsync(HttpRequest request)
        {
            if (!IsMultipartContentType(request.ContentType))
            {
                return FileReceiveResult.FailureResult("Request must be multipart/form-data");
            }

            var boundary = GetBoundary(request.ContentType!);
            if (string.IsNullOrEmpty(boundary))
            {
                return FileReceiveResult.FailureResult("Missing multipart boundary");
            }

            var cancellation = request.HttpContext.RequestAborted;
            var reader = new MultipartReader(boundary, request.Body);

            FileTransferRequest? transferRequest = null;
            string? partPath = null;
            long bytesWritten = 0;

            try
            {
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync(cancellation)) != null)
                {
                    if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd))
                    {
                        continue;
                    }

                    var name = HeaderUtilities.RemoveQuotes(cd.Name).Value;

                    if (cd.IsFormDisposition() && string.Equals(name, "metadata", StringComparison.OrdinalIgnoreCase))
                    {
                        using var sr = new StreamReader(section.Body);
                        var json = await sr.ReadToEndAsync(cancellation);
                        try
                        {
                            transferRequest = JsonSerializer.Deserialize<FileTransferRequest>(json);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to deserialize metadata");
                            return FileReceiveResult.FailureResult("Invalid metadata format");
                        }
                    }
                    else if (cd.IsFileDisposition())
                    {
                        if (partPath != null)
                        {
                            return FileReceiveResult.FailureResult("Multiple file parts are not supported");
                        }

                        // Stream straight to a .part file under incoming. Rename after all
                        // sections are consumed so the monitor never sees a partial file.
                        partPath = Path.Combine(_settings.IncomingDirectory, $"upload-{Guid.NewGuid():N}.part");

                        var fileOptions = new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew,
                            Access = FileAccess.Write,
                            Share = FileShare.None,
                            BufferSize = UploadBufferSize,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                            PreallocationSize = transferRequest?.FileSizeBytes ?? 0
                        };

                        await using var fileStream = new FileStream(partPath, fileOptions);
                        bytesWritten = await CopyToFileAsync(section.Body, fileStream, _settings.MaxFileSizeBytes, cancellation);
                        await fileStream.FlushAsync(cancellation);
                    }
                }

                if (transferRequest == null)
                {
                    return FileReceiveResult.FailureResult("Missing metadata");
                }
                if (partPath == null)
                {
                    return FileReceiveResult.FailureResult("No file uploaded");
                }

                var finalPath = Path.Combine(_settings.IncomingDirectory, transferRequest.OriginalFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? _settings.IncomingDirectory);
                if (File.Exists(finalPath))
                {
                    File.Delete(finalPath);
                }
                File.Move(partPath, finalPath);
                partPath = null;

                _logger.LogInformation("Received file: {File} ({Bytes} bytes)",
                    transferRequest.OriginalFileName, bytesWritten);

                var receivedFileInfo = new ReceivedFileInfo
                {
                    OriginalFileName = transferRequest.OriginalFileName,
                    StoredFileName = transferRequest.OriginalFileName,
                    FilePath = finalPath,
                    FileSizeBytes = bytesWritten,
                    ReceivedTimestamp = DateTime.UtcNow,
                    TransferRequest = transferRequest,
                    RelativeFilePath = transferRequest.RelativeFilePath ?? string.Empty
                };

                _receivedFiles.Add(receivedFileInfo);
                _fileProcessor.QueueForProcessing(receivedFileInfo);

                return FileReceiveResult.SuccessResult(transferRequest.OriginalFileName, finalPath, true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save file: {File}", transferRequest?.OriginalFileName ?? "(unknown)");
                return FileReceiveResult.FailureResult($"Failed to save file: {ex.Message}");
            }
            finally
            {
                if (partPath != null)
                {
                    try { File.Delete(partPath); } catch { }
                }
            }
        }

        private static async Task<long> CopyToFileAsync(Stream source, Stream destination, long maxBytes, CancellationToken ct)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(UploadBufferSize);
            try
            {
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, UploadBufferSize), ct)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new InvalidOperationException($"File exceeds maximum size {maxBytes}");
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                return total;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static bool IsMultipartContentType(string? contentType)
        {
            return !string.IsNullOrEmpty(contentType)
                && contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetBoundary(string contentType)
        {
            var mediaType = MediaTypeHeaderValue.Parse(contentType);
            return HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value ?? string.Empty;
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
        public long MaxFileSizeBytes { get; set; } = 100L * 1024 * 1024 * 1024; // 100GB
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
