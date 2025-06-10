using System.Collections.Concurrent;

namespace HandbrakeScheduler
{
    public class JobQueue
    {
        private readonly ConcurrentQueue<TranscodeJob> _jobs = new();
        private readonly object _lock = new();
        private bool _isProcessing = false;

        public void EnqueueJob(TranscodeJob job)
        {
            _jobs.Enqueue(job);
        }

        public bool TryDequeueJob(out TranscodeJob? job)
        {
            return _jobs.TryDequeue(out job);
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
    }

    public class TranscodeJob
    {
        public string InputPath { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;
        public string Preset { get; set; } = string.Empty;
        public bool DeleteSource { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string FileName => Path.GetFileName(InputPath);
        public bool IsRemoteSource { get; set; }
        public string? TempFilePath { get; set; }
        public long FileSizeBytes { get; set; }
    }
}
