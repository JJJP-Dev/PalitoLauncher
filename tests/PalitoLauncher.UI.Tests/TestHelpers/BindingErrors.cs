using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace PalitoLauncher.UI.Tests.TestHelpers;

/// <summary>
/// Captura los mensajes "System.Windows.Data Error/Warning" de WPF mientras está activo. Sin depurador WPF no traza nada
/// hasta llamar a PresentationTraceSources.Refresh(); por eso se activa aquí explícitamente.
/// </summary>
internal sealed class BindingErrors : TraceListener
{
    private readonly ConcurrentQueue<string> _messages = new();

    private BindingErrors()
    {
    }

    public IReadOnlyCollection<string> Messages => _messages;

    public static BindingErrors Capture()
    {
        PresentationTraceSources.Refresh();
        var listener = new BindingErrors();
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Warning; // Critical + Error + Warning
        return listener;
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        => Record(eventType, id, message);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format,
        params object?[]? args)
        => Record(eventType, id, args is { Length: > 0 } && format != null ? string.Format(CultureInfo.InvariantCulture, format, args) : format);

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
        base.Dispose(disposing);
    }

    private void Record(TraceEventType type, int id, string? message)
    {
        if (type > TraceEventType.Warning) return;
        _messages.Enqueue($"System.Windows.Data {type}: {id} : {message}");
    }
}
