using System.Diagnostics;

namespace Clementine.Core;

public interface ILog
{
    void Info(string message);

    void Warn(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);
}

/// <summary>Daily rolling text log in <see cref="AppPaths.LogDirectory"/>. Never throws.</summary>
public sealed class FileLog : ILog
{
    readonly object _gate = new();
    readonly string _directory;

    public FileLog(string? directory = null) => _directory = directory ?? AppPaths.LogDirectory;

    public string CurrentFile => Path.Combine(_directory, $"clementine-{DateTime.Now:yyyyMMdd}.log");

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{(exception is null ? "" : Environment.NewLine + exception)}";
        Debug.WriteLine(line);
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(CurrentFile, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? exception = null)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
