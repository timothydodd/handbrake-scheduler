using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HandbrakeScheduler
{
    public class JobQueue
    {
        private readonly ConcurrentQueue<TranscodeJob> _jobs = new();
        private readonly object _lock = new();
        private readonly string _persistenceFilePath;
        private bool _isProcessing = false;

        public JobQueue(HandBrakeSettings settings)
        {
            _persistenceFilePath = Path.Combine(AppContext.BaseDirectory, settings.PersistencePath, "job_queue.json");

            // Create directory if it doesn't exist
            var directory = Path.GetDirectoryName(_persistenceFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            LoadFromDisk();
        }

        public void EnqueueJob(TranscodeJob job)
        {
            lock (_lock)
            {
                job.Id = Guid.NewGuid().ToString();
                job.Status = JobStatus.Pending;
                _jobs.Enqueue(job);
                SaveToDisk();
            }
        }

        public bool TryDequeueJob(out TranscodeJob? job)
        {
            lock (_lock)
            {
                bool result = _jobs.TryDequeue(out job);
                if (result && job != null)
                {
                    job.Status = JobStatus.Processing;
                    job.StartedAt = DateTime.Now;
                    SaveToDisk();
                }
                return result;
            }
        }

        public void MarkJobFailed(TranscodeJob job, string errorMessage = "")
        {
            lock (_lock)
            {
                job.Status = JobStatus.Failed;
                job.LastError = errorMessage;
                SaveToDisk();
            }
        }

        public int Count => _jobs.Count;

        public bool IsProcessing
        {
            get
            {
                lock (_lock)
                {
                    return _isProcessing;
                }
            }
            set
            {
                lock (_lock)
                {
                    _isProcessing = value;
                }
            }
        }

        public bool IsEmpty => _jobs.IsEmpty;



        private void SaveToDisk()
        {
            try
            {
                var jobs = _jobs.ToList();
                var json = JsonSerializer.Serialize(jobs, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                // Write to temp file first, then rename (atomic operation)
                var tempFile = _persistenceFilePath + ".tmp";
                File.WriteAllText(tempFile, json);
                File.Move(tempFile, _persistenceFilePath, true);
            }
            catch (Exception ex)
            {
                // Log error but don't throw - persistence failure shouldn't crash the app
                Console.WriteLine($"Failed to save job queue: {ex.Message}");
            }
        }

        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(_persistenceFilePath))
                    return;

                var json = File.ReadAllText(_persistenceFilePath);
                var jobs = JsonSerializer.Deserialize<List<TranscodeJob>>(json);

                if (jobs != null)
                {
                    // Clear existing queue and reload only pending jobs
                    while (_jobs.TryDequeue(out _))
                    { }

                    foreach (var job in jobs.Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Processing))
                    {
                        // Reset processing jobs to pending on startup
                        if (job.Status == JobStatus.Processing)
                        {
                            job.Status = JobStatus.Pending;
                            job.StartedAt = null;
                        }
                        _jobs.Enqueue(job);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load job queue: {ex.Message}");
                // Continue with empty queue if loading fails
            }
        }

        public void ClearCompleted()
        {
            lock (_lock)
            {
                var pendingJobs = _jobs.Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Processing).ToList();

                while (_jobs.TryDequeue(out _))
                { }

                foreach (var job in pendingJobs)
                {
                    _jobs.Enqueue(job);
                }

                SaveToDisk();
            }
        }

        // Clean shutdown - save current state
        public void Shutdown()
        {
            SaveToDisk();
        }
    }

    public enum JobStatus
    {
        Pending,
        Processing,
        Completed,
        Failed
    }

    public class TranscodeJob
    {
        public string Id { get; set; } = string.Empty;
        public string InputPath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public string Preset { get; set; } = string.Empty;
        public bool DeleteSource { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? StartedAt { get; set; }
        public JobStatus Status { get; set; } = JobStatus.Pending;
        public int RetryCount { get; set; } = 0;
        public string LastError { get; set; } = string.Empty;
        public string FileName => Path.GetFileName(InputPath);
        public string StagingPath { get; set; } = string.Empty;
        public string? TempFilePath { get; set; }
        public long FileSizeBytes { get; set; }
    }
}
