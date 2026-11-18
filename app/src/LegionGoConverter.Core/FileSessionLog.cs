using System.Globalization;
using System.Text;

namespace LegionGoConverter.Core;

public sealed class FileSessionLog : ISessionLog, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public string Path { get; }

    public FileSessionLog(string sessionDir)
    {
        Directory.CreateDirectory(sessionDir);
        Path = System.IO.Path.Combine(sessionDir, "session.log");
        var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private void Write(string level, string message)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            if (_disposed) return;
            _writer.WriteLine($"{stamp} {level} {message}");
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}
