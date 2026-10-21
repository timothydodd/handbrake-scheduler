using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace HandbrakeScheduler.Services;

/// <summary>
/// Owns the Spectre.Console Live display that splits the viewport into a scrolling log panel
/// (top) and a fixed progress panel (bottom). Progress bars remain pinned to the bottom of
/// the terminal while log messages scroll above them in a bounded region.
/// </summary>
public class DashboardRenderer : IAsyncDisposable
{
    private readonly ProgressManagerOptions _options;

    private readonly List<ProgressLogEntry> _logBuffer = new();
    private readonly object _logLock = new();

    private readonly ConcurrentDictionary<Guid, ProgressItemState> _tasks = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _completedAt = new();

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _renderLoop;
    private volatile bool _isActive;

    public bool IsActive => _isActive;

    public DashboardRenderer(ProgressManagerOptions? options = null)
    {
        _options = options ?? new ProgressManagerOptions();
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_isActive)
                return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;
            var started = new TaskCompletionSource();

            _renderLoop = Task.Run(async () =>
            {
                try
                {
                    await RunLiveLoopAsync(started, token);
                }
                catch (OperationCanceledException)
                {
                }
            }, token);

            await started.Task;
            _isActive = true;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (!_isActive)
                return;

            _isActive = false;
            _cts?.Cancel();

            if (_renderLoop != null)
            {
                try { await _renderLoop; } catch (OperationCanceledException) { }
            }

            _renderLoop = null;
            _cts?.Dispose();
            _cts = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleLock.Dispose();
    }

    // === Log buffer ===

    public void AppendLog(string message, ProgressLogLevel level, bool isMarkup)
    {
        var entry = new ProgressLogEntry
        {
            Message = message,
            Level = level,
            IsMarkup = isMarkup
        };

        lock (_logLock)
        {
            _logBuffer.Add(entry);
            var overflow = _logBuffer.Count - _options.MaxLogEntries;
            if (overflow > 0)
                _logBuffer.RemoveRange(0, overflow);
        }
    }

    // === Progress tasks ===

    public Guid CreateTask(string description, double maxValue, string? category)
    {
        var state = new ProgressItemState
        {
            Description = description,
            MaxValue = maxValue <= 0 ? 100 : maxValue,
            Category = category,
            StartTime = DateTime.UtcNow
        };
        _tasks[state.Id] = state;
        return state.Id;
    }

    public void UpdateTask(Guid id, double value, string? description = null)
    {
        if (_tasks.TryGetValue(id, out var state))
        {
            state.Value = Math.Min(Math.Max(value, 0), state.MaxValue);
            if (description != null)
                state.Description = description;
        }
    }

    public void IncrementTask(Guid id, double increment)
    {
        if (_tasks.TryGetValue(id, out var state))
            state.Value = Math.Min(Math.Max(state.Value + increment, 0), state.MaxValue);
    }

    public void UpdateTaskBytes(Guid id, long bytesTransferred, long totalBytes,
        double? transferRateMBps, TimeSpan? timeRemaining)
    {
        if (!_tasks.TryGetValue(id, out var state))
            return;

        // Once a task is marked complete, ignore late byte updates to prevent a racing
        // progress callback from resetting Value to a partial ratio.
        if (state.IsComplete)
            return;

        state.BytesTransferred = bytesTransferred;
        state.TotalBytes = totalBytes;
        state.TransferRateMBps = transferRateMBps;
        state.TimeRemaining = timeRemaining;

        if (totalBytes > 0)
            state.Value = Math.Min((double)bytesTransferred / totalBytes * state.MaxValue, state.MaxValue);
    }

    public void CompleteTask(Guid id)
    {
        if (_tasks.TryGetValue(id, out var state))
        {
            state.Value = state.MaxValue;
            state.IsComplete = true;
            state.CompletedTime = DateTime.UtcNow;
            _completedAt[id] = DateTime.UtcNow;

            if (_options.AutoRemoveCompleted)
            {
                var delay = Math.Max(0, _options.AutoRemoveDelayMs);
                _ = Task.Delay(delay).ContinueWith(_ => RemoveTask(id), TaskScheduler.Default);
            }
        }
    }

    public void RemoveTask(Guid id)
    {
        _tasks.TryRemove(id, out _);
        _completedAt.TryRemove(id, out _);
    }

    // === Render loop ===

    private async Task RunLiveLoopAsync(TaskCompletionSource started, CancellationToken token)
    {
        var layout = BuildLayout();

        await AnsiConsole.Live(layout)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Bottom)
            .StartAsync(async ctx =>
            {
                started.TrySetResult();

                var delay = Math.Max(50, _options.RefreshIntervalMs);
                while (!token.IsCancellationRequested)
                {
                    ReapCompletedTasks();
                    UpdateLayout(layout);
                    ctx.Refresh();
                    try { await Task.Delay(delay, token); }
                    catch (OperationCanceledException) { break; }
                }

                UpdateLayout(layout);
                try { ctx.Refresh(); } catch { }
            });
    }

    private static Layout BuildLayout()
    {
        return new Layout("root")
            .SplitRows(
                new Layout("logs"),
                new Layout("progress").Size(5));
    }

    private void UpdateLayout(Layout layout)
    {
        var tasks = SnapshotTasks();
        var progressPanel = BuildProgressPanel(tasks);
        var progressRows = ComputeProgressPanelHeight(tasks.Count);
        layout["progress"].Size(progressRows);
        layout["progress"].Update(progressPanel);

        var viewportHeight = Math.Max(10, AnsiConsole.Profile.Height);
        var logHeight = Math.Max(3, viewportHeight - progressRows - 2);
        layout["logs"].Update(BuildLogPanel(logHeight));
    }

    private static int ComputeProgressPanelHeight(int taskCount)
    {
        var rows = Math.Max(1, taskCount);
        var viewportCap = Math.Max(4, AnsiConsole.Profile.Height / 2);
        return Math.Min(rows + 2, viewportCap);
    }

    private List<ProgressItemState> SnapshotTasks()
    {
        return _tasks.Values
            .OrderBy(t => t.StartTime)
            .ToList();
    }

    private void ReapCompletedTasks()
    {
        if (!_options.AutoRemoveCompleted)
            return;

        var threshold = DateTime.UtcNow - TimeSpan.FromMilliseconds(_options.AutoRemoveDelayMs);
        foreach (var pair in _completedAt)
        {
            if (pair.Value <= threshold)
            {
                _tasks.TryRemove(pair.Key, out _);
                _completedAt.TryRemove(pair.Key, out _);
            }
        }
    }

    private static IRenderable BuildProgressPanel(List<ProgressItemState> tasks)
    {
        IRenderable content;
        if (tasks.Count == 0)
        {
            content = new Markup("[dim]idle[/]");
        }
        else
        {
            var table = new Table()
                .Border(TableBorder.None)
                .HideHeaders()
                .AddColumn(new TableColumn(string.Empty).NoWrap())
                .AddColumn(new TableColumn(string.Empty).NoWrap())
                .AddColumn(new TableColumn(string.Empty).RightAligned())
                .AddColumn(new TableColumn(string.Empty).RightAligned());

            foreach (var t in tasks)
            {
                table.AddRow(
                    new Markup(Markup.Escape(Truncate(t.Description, 48))),
                    RenderBar(t),
                    new Markup($"[yellow]{t.Percentage,5:F1}%[/]"),
                    new Markup(RenderTrailing(t)));
            }

            content = table;
        }

        return new Panel(content)
            .Header("[bold cyan] Progress [/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private IRenderable BuildLogPanel(int maxLines)
    {
        ProgressLogEntry[] tail;
        lock (_logLock)
        {
            var count = _logBuffer.Count;
            var take = Math.Min(count, maxLines);
            tail = new ProgressLogEntry[take];
            _logBuffer.CopyTo(count - take, tail, 0, take);
        }

        IRenderable content;
        if (tail.Length == 0)
        {
            content = new Markup("[dim](no activity)[/]");
        }
        else
        {
            var rows = new IRenderable[tail.Length];
            for (var i = 0; i < tail.Length; i++)
                rows[i] = FormatLogEntry(tail[i]);
            content = new Rows(rows);
        }

        return new Panel(content)
            .Header("[bold cyan] Log [/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private IRenderable FormatLogEntry(ProgressLogEntry entry)
    {
        var (label, style) = entry.Level switch
        {
            ProgressLogLevel.Debug => ("DBG ", "dim"),
            ProgressLogLevel.Info => ("INFO", "blue"),
            ProgressLogLevel.Warning => ("WARN", "yellow"),
            ProgressLogLevel.Error => ("ERR ", "red"),
            ProgressLogLevel.Success => ("OK  ", "green"),
            _ => ("LOG ", "white")
        };

        var body = entry.IsMarkup ? entry.Message : Markup.Escape(entry.Message);
        var prefix = _options.ShowTimestamps
            ? $"[dim]{entry.Timestamp:HH:mm:ss}[/] [{style}]{label}[/] "
            : $"[{style}]{label}[/] ";

        return new Markup(prefix + body);
    }

    private static IRenderable RenderBar(ProgressItemState state)
    {
        const int width = 30;
        var fraction = state.MaxValue > 0
            ? Math.Clamp(state.Value / state.MaxValue, 0, 1)
            : 0;
        var filled = (int)Math.Round(fraction * width);
        var empty = width - filled;
        var color = state.IsComplete ? "green" : "cyan";
        return new Markup($"[{color}]{new string('━', filled)}[/][dim]{new string('─', empty)}[/]");
    }

    private static string RenderTrailing(ProgressItemState state)
    {
        if (state.IsComplete)
            return "[green]done[/]";

        if (state.TransferRateMBps.HasValue && state.TransferRateMBps.Value > 0)
        {
            var rate = $"{state.TransferRateMBps.Value:F1} MB/s";
            if (state.TimeRemaining.HasValue)
                return $"[dim]{FormatEta(state.TimeRemaining.Value)} · {rate}[/]";
            return $"[dim]{rate}[/]";
        }

        if (state.TimeRemaining.HasValue)
            return $"[dim]{FormatEta(state.TimeRemaining.Value)}[/]";

        var frames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        var idx = (int)((DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond / 80) % frames.Length);
        return $"[cyan]{frames[idx]}[/]";
    }

    private static string FormatEta(TimeSpan span)
    {
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
        return $"{span.Minutes:D2}:{span.Seconds:D2}";
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value ?? string.Empty;
        return value.Substring(0, Math.Max(1, max - 1)) + "…";
    }
}
