using System;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace HandbrakeScheduler.Services;

/// <summary>
/// ILoggerProvider that routes log messages into the DashboardRenderer's log panel when the
/// dashboard is rendering, and to AnsiConsole otherwise. Replaces the default console logger
/// provider, which would write directly to Console.Out and corrupt the live region.
/// </summary>
public sealed class DashboardLoggerProvider : ILoggerProvider
{
    private readonly DashboardRenderer _renderer;

    public DashboardLoggerProvider(DashboardRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public ILogger CreateLogger(string categoryName) => new DashboardLogger(_renderer, categoryName);

    public void Dispose() { }
}

internal sealed class DashboardLogger : ILogger
{
    private readonly DashboardRenderer _renderer;
    private readonly string _category;
    private readonly string _shortCategory;

    public DashboardLogger(DashboardRenderer renderer, string category)
    {
        _renderer = renderer;
        _category = category;
        var lastDot = category.LastIndexOf('.');
        _shortCategory = lastDot >= 0 && lastDot < category.Length - 1
            ? category.Substring(lastDot + 1)
            : category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
    {
        // ProgressManager routes its Log/LogMarkup calls directly into the renderer AND through
        // ILogger (so the file logger still captures them). Dropping its category here prevents
        // double entries in the log panel.
        if (_category == "HandbrakeScheduler.Services.ProgressManager")
            return false;

        if (logLevel < LogLevel.Warning &&
            (_category.StartsWith("Microsoft.", StringComparison.Ordinal) ||
             _category.StartsWith("System.", StringComparison.Ordinal)))
        {
            return false;
        }
        return logLevel != LogLevel.None;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter is null)
            return;

        var message = formatter(state, exception);
        if (exception != null)
            message += $" — {exception.GetType().Name}: {exception.Message}";

        if (string.IsNullOrEmpty(message))
            return;

        var level = logLevel switch
        {
            LogLevel.Trace => ProgressLogLevel.Debug,
            LogLevel.Debug => ProgressLogLevel.Debug,
            LogLevel.Information => ProgressLogLevel.Info,
            LogLevel.Warning => ProgressLogLevel.Warning,
            LogLevel.Error => ProgressLogLevel.Error,
            LogLevel.Critical => ProgressLogLevel.Error,
            _ => ProgressLogLevel.Info
        };

        if (_renderer.IsActive)
        {
            _renderer.AppendLog(message, level, isMarkup: false);
            return;
        }

        var style = level switch
        {
            ProgressLogLevel.Debug => "dim",
            ProgressLogLevel.Warning => "yellow",
            ProgressLogLevel.Error => "red",
            ProgressLogLevel.Success => "green",
            _ => "blue"
        };
        var label = level switch
        {
            ProgressLogLevel.Debug => "DBG ",
            ProgressLogLevel.Warning => "WARN",
            ProgressLogLevel.Error => "ERR ",
            ProgressLogLevel.Success => "OK  ",
            _ => "INFO"
        };
        AnsiConsole.MarkupLine($"[{style}]{label}[/] [dim]{_shortCategory}[/] {Markup.Escape(message)}");
    }
}
