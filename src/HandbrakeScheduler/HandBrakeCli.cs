using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

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
        private DateTime _lastProgressUpdate = DateTime.Now;
        private readonly TimeSpan _progressTimeout = TimeSpan.FromSeconds(30);
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

        public HandBrakeCli(string cliPath)
        {
            _cliPath = cliPath;
        }

        public async Task<string?> Transcode(string inputFile, string outputDirectory, string preset, Action<HandbrakeConversionStatus> status, bool overwriteExisting = true, bool deletesource = true)
        {
            if (!File.Exists(inputFile))
            {
                throw new HandbrakeCliWrapperException($"The input file '{inputFile}' could not be found");
            }

            if (Status.Converting)
            {
                throw new HandbrakeCliWrapperException("A conversion is already running");
            }

            _status = status;
            _lastProgressUpdate = DateTime.Now;
            _timeoutCancellationTokenSource = new CancellationTokenSource();

            string outputFilename = FileUtil.GetFileNameWithNewExtension(inputFile, ".mp4");

            inputFile = Path.GetFullPath(inputFile);
            outputFilename = Path.Combine(Path.GetFullPath(outputDirectory), outputFilename);

            if (File.Exists(outputFilename) && !overwriteExisting)
            {
                throw new HandbrakeCliWrapperException($"The file '{outputFilename}' already exists. Set overwriteExisting to true to overwrite");
            }

            string arg = $"-i \"{inputFile}\" -o \"{outputFilename}\" --preset \"{preset}\"";

            if (!File.Exists(_cliPath))
            {
                throw new FileNotFoundException("No HandbrakeCLI executable found", _cliPath);
            }

            _process = new Process
            {
                StartInfo = new ProcessStartInfo(_cliPath, arg)
            };
            _process.OutputDataReceived += OnOutputDataReceived;

            StartedTranscoding(inputFile, outputFilename);

            bool success;
            try
            {
                // Start the progress timeout monitoring
                var timeoutTask = MonitorProgressTimeout(_timeoutCancellationTokenSource.Token);
                var processTask = AwaitProcess(_process);

                // Wait for either the process to complete or timeout to occur
                var completedTask = await Task.WhenAny(processTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    // Timeout occurred
                    StopTranscoding();
                    throw new HandbrakeCliWrapperException("Transcoding timed out - no progress for 15 seconds");
                }

                success = await processTask;
            }
            catch (Exception e) when (!(e is HandbrakeCliWrapperException))
            {
                throw new HandbrakeCliWrapperException("An error occured when starting the HandbrakeCLI process. See inner exception", e);
            }
            finally
            {
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
                        File.Delete(inputFile);
                    }
                    catch (Exception e)
                    {
                        throw new HandbrakeCliWrapperException($"Could not remove original '{inputFile}'", e);
                    }
                }
                return outputFilename;
            }
            else
            {
                ErrorTranscoding();
            }
            return null;
        }

        private async Task MonitorProgressTimeout(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken); // Check every second

                if (cancellationToken.IsCancellationRequested)
                    break;

                var timeSinceLastUpdate = DateTime.Now - _lastProgressUpdate;
                if (timeSinceLastUpdate > _progressTimeout)
                {
                    return; // Timeout reached
                }
            }
        }

        private void StartedTranscoding(string inputFile, string outputFile)
        {
            SetStatus(inputFile, outputFile);
            _lastProgressUpdate = DateTime.Now;
            TranscodingStarted?.Invoke(this, new HandbrakeTranscodingEventArgs(Status.InputFile));
        }

        private void DoneTranscoding()
        {
            SetStatus();
            TranscodingCompleted?.Invoke(this, new HandbrakeTranscodingEventArgs(Status.InputFile));
        }

        private void ErrorTranscoding()
        {
            SetStatus();
            TranscodingError?.Invoke(this, new HandbrakeTranscodingEventArgs(Status.InputFile));
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs dataReceivedEventArgs)
        {
            if (string.IsNullOrEmpty(dataReceivedEventArgs.Data))
            {
                return;
            }

            // Update the last progress timestamp for any output (not just progress updates)
            _lastProgressUpdate = DateTime.Now;

            Match match = HandbrakeOutputRegex.Match(dataReceivedEventArgs.Data);
            if (!match.Success)
            {
                return;
            }

            Status.Percentage =
                float.Parse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!match.Groups[2].Success)
            {
                return;
            }

            Status.CurrentFps =
                float.Parse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            Status.AverageFps =
                float.Parse(match.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            Status.Estimated =
                TimeSpan.ParseExact(match.Groups[5].Value, "h\\hm\\ms\\s", CultureInfo.InvariantCulture);

            _status?.Invoke(Status);
        }

        private static async Task<bool> AwaitProcess(Process process)
        {
            TaskCompletionSource<bool> tcs = new();
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.EnableRaisingEvents = true;
            process.StartInfo.CreateNoWindow = true;
            process.Exited += Exited;

            void Exited(object sender, EventArgs eventArgs)
            {
                process.Exited -= Exited;
                tcs.SetResult(process.ExitCode == 0);
            }

            _ = process.Start();
            process.BeginOutputReadLine();
            return await tcs.Task;
        }

        /// <summary>
        /// Gets the HandBrake CLI version
        /// </summary>
        /// <returns>The version string of HandBrake CLI</returns>
        /// <exception cref="HandbrakeCliWrapperException">Thrown when HandBrake CLI is not found or version cannot be retrieved</exception>
        public async Task<string> GetVersionAsync()
        {
            if (!File.Exists(_cliPath))
            {
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
                    throw new HandbrakeCliWrapperException($"Failed to get HandBrake version. Exit code: {process.ExitCode}. Error: {error}");
                }

                // HandBrake typically outputs version info to stdout
                // Example output: "HandBrake 1.6.1 (2023010400)"
                string version = !string.IsNullOrWhiteSpace(output) ? output.Trim() : error.Trim();

                if (string.IsNullOrWhiteSpace(version))
                {
                    throw new HandbrakeCliWrapperException("Unable to parse HandBrake version from output");
                }

                return version;
            }
            catch (Exception ex) when (!(ex is HandbrakeCliWrapperException))
            {
                throw new HandbrakeCliWrapperException($"Error retrieving HandBrake version: {ex.Message}", ex);
            }
        }

        public void StopTranscoding()
        {
            _timeoutCancellationTokenSource?.Cancel();

            if (_process == null)
            {
                return;
            }

            if (!_process.HasExited)
            {
                _process.Kill();
            }

            if (!File.Exists(_out))
            {
                return;
            }

            try
            {
                File.Delete(_out);
            }
            catch { }
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
