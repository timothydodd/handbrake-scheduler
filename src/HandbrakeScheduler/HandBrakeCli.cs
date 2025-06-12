using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace HandbrakeScheduler
{
    public class HandBrakeCli
    {
        private static readonly Regex HandbrakeOutputRegex =
            new(
                "Encoding:.*?, (\\d{1,3}\\.\\d{1,2}) %( \\((\\d{1,4}\\.\\d{1,2}) fps, avg (\\d{1,4}\\.\\d{1,2}) fps, ETA (\\d{2}h\\d{2}m\\d{2}s)\\))?",
                RegexOptions.Compiled);

        public HandbrakeConversionStatus Status { get; } = new HandbrakeConversionStatus();
        private Process? _process;
        private string _out;
        private readonly string _cliPath;
        private readonly ILogger<HandBrakeCli> _logger;
        private DateTime _lastProgressUpdate = DateTime.Now;
        private readonly TimeSpan _progressTimeout = TimeSpan.FromSeconds(15);
        private CancellationTokenSource? _timeoutCancellationTokenSource;

        /// <summary>
        /// Invoked when a conversion has been completed succesfully
        /// </summary>
        public event EventHandler<HandbrakeTranscodingEventArgs> TranscodingCompleted;
        /// <summary>
        /// Invoked when a conversion has been started
        /// </summary>
        public event EventHandler<HandbrakeTranscodingEventArgs> TranscodingStarted;
        /// <summary>
        /// Invoked when an error occurs during a conversion
        /// </summary>
        public event EventHandler<HandbrakeTranscodingEventArgs> TranscodingError;

        public Action<HandbrakeConversionStatus> _status;

        public HandBrakeCli(string cliPath, ILogger<HandBrakeCli> logger)
        {
            _cliPath = cliPath;
            _logger = logger;
            _logger.LogInformation("HandBrakeCli initialized with CLI path: {CliPath}", cliPath);
        }

        public async Task<string?> Transcode(string inputFile, string outputDirectory, string preset, Action<HandbrakeConversionStatus> status, bool overwriteExisting = true, bool deletesource = true)
        {
            _logger.LogInformation("Starting transcode process for {InputFile}", inputFile);

            if (!File.Exists(inputFile))
            {
                _logger.LogError("Input file not found: {InputFile}", inputFile);
                throw new HandbrakeCliWrapperException($"The input file '{inputFile}' could not be found");
            }

            if (Status.Converting)
            {
                _logger.LogWarning("Attempted to start transcoding while conversion already running");
                throw new HandbrakeCliWrapperException("A conversion is already running");
            }

            _status = status;
            _lastProgressUpdate = DateTime.Now;
            _timeoutCancellationTokenSource = new CancellationTokenSource();

            _logger.LogDebug("Progress timeout monitoring initialized. Last update: {LastUpdate}", _lastProgressUpdate);

            string outputFilename = FileUtil.GetFileNameWithNewExtension(inputFile, ".mp4");

            inputFile = Path.GetFullPath(inputFile);
            outputFilename = Path.Combine(Path.GetFullPath(outputDirectory), outputFilename);

            _logger.LogInformation("Input: {InputFile}, Output: {OutputFile}", inputFile, outputFilename);

            if (File.Exists(outputFilename) && !overwriteExisting)
            {
                _logger.LogError("Output file already exists and overwrite is disabled: {OutputFile}", outputFilename);
                throw new HandbrakeCliWrapperException($"The file '{outputFilename}' already exists. Set overwriteExisting to true to overwrite");
            }

            string arg = $"-i \"{inputFile}\" -o \"{outputFilename}\" --preset \"{preset}\"";
            _logger.LogDebug("HandBrake arguments: {Arguments}", arg);

            if (!File.Exists(_cliPath))
            {
                _logger.LogError("HandBrake CLI executable not found: {CliPath}", _cliPath);
                throw new FileNotFoundException("No HandbrakeCLI executable found", _cliPath);
            }

            _process = new Process
            {
                StartInfo = new ProcessStartInfo(_cliPath, arg)
            };
            _process.OutputDataReceived += OnOutputDataReceived;
            _process.ErrorDataReceived += OnErrorDataReceived;

            StartedTranscoding(inputFile, outputFilename);

            bool success;
            try
            {
                _logger.LogInformation("Starting HandBrake process and timeout monitoring");

                // Start the progress timeout monitoring
                var timeoutTask = MonitorProgressTimeout(_timeoutCancellationTokenSource.Token);
                var processTask = AwaitProcess(_process);

                _logger.LogDebug("Both tasks started, waiting for completion");

                // Wait for either the process to complete or timeout to occur
                var completedTask = await Task.WhenAny(processTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    // Timeout occurred
                    _logger.LogWarning("Progress timeout occurred after 15 seconds. Last progress update: {LastUpdate}, Current time: {CurrentTime}",
                        _lastProgressUpdate, DateTime.Now);
                    StopTranscoding();
                    throw new HandbrakeCliWrapperException("Transcoding timed out - no progress for 15 seconds");
                }

                _logger.LogDebug("Process task completed, getting result");
                success = await processTask;
                _logger.LogInformation("HandBrake process completed with success: {Success}", success);
            }
            catch (Exception e) when (!(e is HandbrakeCliWrapperException))
            {
                _logger.LogError(e, "Error occurred during HandBrake process execution");
                throw new HandbrakeCliWrapperException("An error occured when starting the HandbrakeCLI process. See inner exception", e);
            }
            finally
            {
                _logger.LogDebug("Cleaning up timeout monitoring resources");
                _timeoutCancellationTokenSource?.Cancel();
                _timeoutCancellationTokenSource?.Dispose();
                _timeoutCancellationTokenSource = null;
            }

            _process = null;
            if (success)
            {
                DoneTranscoding();
                if (deletesource)
                {
                    try
                    {
                        _logger.LogInformation("Deleting source file: {InputFile}", inputFile);
                        File.Delete(inputFile);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Failed to delete source file: {InputFile}", inputFile);
                        throw new HandbrakeCliWrapperException($"Could not remove original '{inputFile}'", e);
                    }
                }
                _logger.LogInformation("Transcoding completed successfully. Output: {OutputFile}", outputFilename);
                return outputFilename;
            }
            else
            {
                _logger.LogError("Transcoding failed");
                ErrorTranscoding();
            }
            return null;
        }

        private async Task MonitorProgressTimeout(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Progress timeout monitoring started");
            int checkCount = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationToken); // Check every second
                    checkCount++;

                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogDebug("Progress monitoring cancelled after {CheckCount} checks", checkCount);
                        break;
                    }

                    var timeSinceLastUpdate = DateTime.Now - _lastProgressUpdate;

                    if (checkCount % 5 == 0) // Log every 5 seconds to avoid spam
                    {
                        _logger.LogDebug("Progress check #{CheckCount}: Time since last update: {TimeSinceUpdate}s",
                            checkCount, timeSinceLastUpdate.TotalSeconds);
                    }

                    if (timeSinceLastUpdate > _progressTimeout)
                    {
                        _logger.LogWarning("Progress timeout detected! Time since last update: {TimeSinceUpdate}s, Timeout threshold: {TimeoutThreshold}s",
                            timeSinceLastUpdate.TotalSeconds, _progressTimeout.TotalSeconds);
                        return; // Timeout reached
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Progress monitoring was cancelled (normal completion)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in progress timeout monitoring");
            }

            _logger.LogDebug("Progress timeout monitoring ended after {CheckCount} checks", checkCount);
        }

        private void StartedTranscoding(string inputFile, string outputFile)
        {
            _logger.LogInformation("Transcoding started: {InputFile} -> {OutputFile}", Path.GetFileName(inputFile), Path.GetFileName(outputFile));
            SetStatus(inputFile, outputFile);
            _lastProgressUpdate = DateTime.Now;
            _logger.LogDebug("Progress timestamp reset to: {LastUpdate}", _lastProgressUpdate);
            TranscodingStarted?.Invoke(this, new HandbrakeTranscodingEventArgs(Status.InputFile));
        }

        private void DoneTranscoding()
        {
            _logger.LogInformation("Transcoding completed successfully");
            SetStatus();
            TranscodingCompleted?.Invoke(this, new HandbrakeTranscodingEventArgs(Status.InputFile));
        }

        private void ErrorTranscoding()
        {
            _logger.LogError("Transcoding completed with error");
            SetStatus();
            TranscodingError?.Invoke(this, new HandbrakeTranscodingEventArgs(Status.InputFile));
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
        {
            if (string.IsNullOrEmpty(dataReceivedEventArgs.Data))
            {
                return;
            }

            // Update the last progress timestamp for any output
            var previousUpdate = _lastProgressUpdate;
            _lastProgressUpdate = DateTime.Now;

            _logger.LogTrace("HandBrake output received: {Output} (Previous update: {PreviousUpdate}, New update: {NewUpdate})",
                dataReceivedEventArgs.Data, previousUpdate, _lastProgressUpdate);

            Match match = HandbrakeOutputRegex.Match(dataReceivedEventArgs.Data);
            if (!match.Success)
            {
                _logger.LogTrace("Output did not match progress regex");
                return;
            }

            var percentage = float.Parse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            Status.Percentage = percentage;

            _logger.LogDebug("Progress update: {Percentage}%", percentage);

            if (!match.Groups[2].Success)
            {
                return;
            }

            Status.CurrentFps = float.Parse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            Status.AverageFps = float.Parse(match.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            Status.Estimated = TimeSpan.ParseExact(match.Groups[5].Value, "h\\hm\\ms\\s", CultureInfo.InvariantCulture);

            _logger.LogDebug("Detailed progress: {Percentage}%, {CurrentFps} fps, {AvgFps} avg fps, {ETA} remaining",
                Status.Percentage, Status.CurrentFps, Status.AverageFps, Status.Estimated);

            _status?.Invoke(Status);
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
        {
            if (string.IsNullOrEmpty(dataReceivedEventArgs.Data))
            {
                return;
            }

            // Update progress timestamp for error output too (HandBrake might output to stderr)
            _lastProgressUpdate = DateTime.Now;
            _logger.LogWarning("HandBrake error output: {ErrorOutput}", dataReceivedEventArgs.Data);
        }

        private async Task<bool> AwaitProcess(Process process)
        {
            _logger.LogDebug("Setting up process monitoring");

            TaskCompletionSource<bool> tcs = new();
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.EnableRaisingEvents = true;
            process.StartInfo.CreateNoWindow = true;
            process.Exited += Exited;

            void Exited(object sender, EventArgs eventArgs)
            {
                _logger.LogInformation("HandBrake process exited with code: {ExitCode}", process.ExitCode);
                process.Exited -= Exited;
                tcs.SetResult(process.ExitCode == 0);
            }

            _logger.LogDebug("Starting HandBrake process");
            _ = process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _logger.LogInformation("HandBrake process started with PID: {ProcessId}", process.Id);

            return await tcs.Task;
        }

        /// <summary>
        /// Gets the HandBrake CLI version
        /// </summary>
        /// <returns>The version string of HandBrake CLI</returns>
        /// <exception cref="HandbrakeCliWrapperException">Thrown when HandBrake CLI is not found or version cannot be retrieved</exception>
        public async Task<string> GetVersionAsync()
        {
            _logger.LogDebug("Getting HandBrake CLI version");

            if (!File.Exists(_cliPath))
            {
                _logger.LogError("HandBrake CLI not found for version check: {CliPath}", _cliPath);
                throw new HandbrakeCliWrapperException($"HandBrake CLI not found at path: {_cliPath}");
            }

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo(_cliPath, "--version")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    _logger.LogError("HandBrake version check failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                    throw new HandbrakeCliWrapperException($"Failed to get HandBrake version. Exit code: {process.ExitCode}. Error: {error}");
                }

                string version = !string.IsNullOrWhiteSpace(output) ? output.Trim() : error.Trim();

                if (string.IsNullOrWhiteSpace(version))
                {
                    _logger.LogError("Unable to parse HandBrake version from output");
                    throw new HandbrakeCliWrapperException("Unable to parse HandBrake version from output");
                }

                _logger.LogInformation("HandBrake version: {Version}", version);
                return version;
            }
            catch (Exception ex) when (!(ex is HandbrakeCliWrapperException))
            {
                _logger.LogError(ex, "Error retrieving HandBrake version");
                throw new HandbrakeCliWrapperException($"Error retrieving HandBrake version: {ex.Message}", ex);
            }
        }

        public void StopTranscoding()
        {
            _logger.LogWarning("StopTranscoding called");

            _timeoutCancellationTokenSource?.Cancel();

            if (_process == null)
            {
                _logger.LogDebug("No process to stop");
                return;
            }

            if (!_process.HasExited)
            {
                _logger.LogWarning("Killing HandBrake process (PID: {ProcessId})", _process.Id);
                _process.Kill();
            }
            else
            {
                _logger.LogDebug("Process already exited");
            }

            if (!File.Exists(_out))
            {
                _logger.LogDebug("Output file does not exist, nothing to delete: {OutputFile}", _out);
                return;
            }

            try
            {
                _logger.LogInformation("Deleting incomplete output file: {OutputFile}", _out);
                File.Delete(_out);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete incomplete output file: {OutputFile}", _out);
            }
        }

        private void SetStatus(string inputFile = "", string outputFilename = "")
        {
            _out = outputFilename;
            Status.Converting = !string.IsNullOrEmpty(inputFile);
            Status.InputFile = !string.IsNullOrEmpty(inputFile) ? Path.GetFileName(inputFile) : "";
            Status.OutputFile = !string.IsNullOrEmpty(outputFilename) ? Path.GetFileName(outputFilename) : "";
            Status.Percentage = 0;
            Status.CurrentFps = 0;
            Status.AverageFps = 0;
            Status.Estimated = TimeSpan.Zero;

            _logger.LogDebug("Status updated - Converting: {Converting}, Input: {InputFile}, Output: {OutputFile}",
                Status.Converting, Status.InputFile, Status.OutputFile);
        }
    }

    public class HandbrakeTranscodingEventArgs : EventArgs
    {
        public HandbrakeTranscodingEventArgs(string inputFilename)
        {
            InputFilename = inputFilename;
        }
        public string InputFilename { get; }
    }

    public class HandbrakeCliWrapperException : Exception
    {
        public HandbrakeCliWrapperException(string msg) : base(msg)
        {
        }
        public HandbrakeCliWrapperException(string msg, Exception inner) : base(msg, inner)
        {
        }
    }

    public class HandbrakeConversionStatus
    {
        /// <summary>
        /// Whether a conversion is going on at the moment
        /// </summary>
        public bool Converting { get; internal set; }
        /// <summary>
        /// The file used as input file for the current conversion
        /// </summary>
        public string? InputFile { get; internal set; }
        /// <summary>
        /// The filename used as output filename for the current conversion
        /// </summary>
        public string? OutputFile { get; internal set; }
        /// <summary>
        /// How many percentage done the current conversion is
        /// </summary>
        public float Percentage { get; internal set; }
        /// <summary>
        /// The current fps for the current conversion
        /// </summary>
        public float CurrentFps { get; internal set; }
        /// <summary>
        /// The average fps for the current conversion
        /// </summary>
        public float AverageFps { get; internal set; }
        /// <summary>
        /// The estimated time left of the current conversion
        /// </summary>
        public TimeSpan Estimated { get; internal set; }

        public override string ToString()
        {
            return !Converting
                ? "Idle"
                : $"{InputFile} -> {OutputFile} - {Percentage}%  {CurrentFps} fps.  {AverageFps} fps. avg.  {Estimated} time remaining";
        }
    }
}
