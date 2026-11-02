using System.Globalization;
using System.Text.Json;

namespace Monitoring.Net;

/// <summary>
/// Rotating JSON-Lines communication log (protocol spec §6):
/// logs/comm-YYYYMMDD.log, max 16MB per file, sequence-numbered rollover.
/// </summary>
public sealed class CommunicationLog : IDisposable
{
    public const long MaxFileBytes = 16 * 1024 * 1024;

    private readonly string _directory;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";
    private int _fileSeq;
    private long _bytes;
    private bool _disposed;

    public CommunicationLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public void Frame(string direction, byte type, ushort sequence, int length, bool crcOk, string? note = null)
        => Write(direction, type, sequence, length, crcOk, note);

    public void Event(string note)
        => Write("event", null, null, 0, true, note);

    private void Write(string dir, byte? type, ushort? seq, int len, bool crcOk, string? note)
    {
        lock (_lock)
        {
            if (_disposed) return;
            EnsureWriter();
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                dir,
                type = type?.ToString("X2", CultureInfo.InvariantCulture),
                seq,
                len,
                crcOk,
                note,
            });
            _writer!.WriteLine(line);
            _writer.Flush();
            _bytes += line.Length + Environment.NewLine.Length;
        }
    }

    private void EnsureWriter()
    {
        var date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_writer is not null && _currentDate == date && _bytes < MaxFileBytes)
            return;
        _writer?.Dispose();
        if (_currentDate != date) { _currentDate = date; _fileSeq = 0; }
        string path;
        do
        {
            var suffix = _fileSeq == 0 ? "" : $".{_fileSeq}";
            path = Path.Combine(_directory, $"comm-{date}{suffix}.log");
            _fileSeq++;
        } while (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read));
        _bytes = new FileInfo(path).Exists ? new FileInfo(path).Length : 0;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
