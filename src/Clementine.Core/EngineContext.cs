namespace Clementine.Core;

/// <summary>Shared services handed to every provider and operation.</summary>
public sealed class EngineContext
{
    readonly Func<ClementineSettings> _settings;

    public EngineContext(ToolLocator tools, Func<ClementineSettings> settings, ILog log)
    {
        Tools = tools;
        _settings = settings;
        Log = log;
    }

    public ToolLocator Tools { get; }

    /// <summary>The current settings snapshot (re-read on every access).</summary>
    public ClementineSettings Settings => _settings();

    public ILog Log { get; }
}
