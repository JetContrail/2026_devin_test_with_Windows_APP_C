using System.Globalization;
using System.Text.Json;

namespace Monitoring.Net;

/// <summary>
/// Rotating JSON-Lines communication log (protocol spec §6):
/// logs/comm[-label]-YYYYMMDD.log, max 16MB per file, sequence-numbered rollover.
/// Write failures are isolated: the log disables itself after repeated errors and
/// never propagates exceptions into the communication path.
/// </summary>
public sealed class CommunicationLog : IDisposable
{
    public const long MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxConsecutiveFailures = 5;

    private readonly string? _directory;
    private readonly string? _label;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";
    private int _fileSeq;
    private long _bytes;
    private int _failures;
    private bool _disposed;
    private bool _disabled;

    public CommunicationLog(string directory, string? label = null)
    {
        _label = label is null ? null : Sanitize(label);
        try
        {
            Directory.CreateDirectory(directory);
            _directory = directory;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _disabled = true; // logging is best-effort; never block monitoring
        }
    }

    private static string Sanitize(string label)
    {
        var chars = label.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }

    public void Frame(string direction, byte type, ushort sequence, int length, bool crcOk, string? note = null)
        => Write(direction, type, sequence, length, crcOk, note);

    public void Event(string note)
        => Write("event", null, null, 0, true, note);

    private void Write(string dir, byte? type, ushort? seq, int len, bool crcOk, string? note)
    {
        lock (_lock)
        {
            if (_disposed || _disabled) return;
            try
            {
                EnsureWriter();
                var line = JsonSerializer.Serialize(new
                {
                    ts = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                    dev = _label,
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
                _failures = 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // a full/read-only disk or vanished log dir must not stop monitoring:
                // close the writer, retry on the next call, then give up quietly
                try { _writer?.Dispose(); } catch (ObjectDisposedException) { }
                _writer = null;
                if (++_failures >= MaxConsecutiveFailures)
                    _disabled = true;
            }
        }
    }

    private void EnsureWriter()
    {
        var date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_writer is not null && _currentDate == date && _bytes < MaxFileBytes)
            return;
        _writer?.Dispose();
        if (_currentDate != date) { _currentDate = date; _fileSeq = 0; }
        string prefix = _label is null ? "comm" : $"comm-{_label}";
        string path;
        do
        {
            var suffix = _fileSeq == 0 ? "" : $".{_fileSeq}";
            path = Path.Combine(_directory!, $"{prefix}-{date}{suffix}.log");
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
