// LongRun: 24h continuous-operation soak test against FakeDevice.
// Usage:
//   dotnet run --project tests/FakeDevice -- --port 50001 --events 3 &
//   dotnet run --project tests/LongRun -- --hours 24 --port 50001
// Periodically records process memory/handles; at the end reports drift
// (memory-leak check per RULES: メモリリークゼロ).
using System.Net;
using Monitoring.Core;
using Monitoring.Net;

var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i += 2)
    opts[args[i].TrimStart('-')] = i + 1 < args.Length ? args[i + 1] : "0";

double hours = double.Parse(opts.GetValueOrDefault("hours", "24"));
int port = int.Parse(opts.GetValueOrDefault("port", "50001"));
var host = opts.GetValueOrDefault("host", "127.0.0.1");
var sampleEvery = TimeSpan.FromMinutes(5);
var deadline = DateTimeOffset.UtcNow.AddHours(hours);

var log = new CommunicationLog(Path.Combine(AppContext.BaseDirectory, "logs"));
var client = new TcpDeviceClient(new IPEndPoint(IPAddress.Parse(host), port), log: log);
await using var monitor = new DeviceMonitor(client,
    new MonitorOptions { PollInterval = TimeSpan.FromSeconds(1) });

long polls = 0, errors = 0, reconnects = 0, cardEvents = 0;
monitor.StatusReceived += (_, _) => polls++;
monitor.PollError += (_, _) => errors++;
monitor.CardEventReceived += (_, _) => cardEvents++;
monitor.ConnectionChanged += (_, s) => { if (s == ConnectionState.Connected) reconnects++; };

monitor.Start();
var proc = System.Diagnostics.Process.GetCurrentProcess();
var samples = new List<(DateTimeOffset T, long Mem, int Threads, int Handles)>();
Console.WriteLine($"Soak started; duration={hours}h target={host}:{port}");

while (DateTimeOffset.UtcNow < deadline)
{
    proc.Refresh();
    samples.Add((DateTimeOffset.UtcNow, proc.WorkingSet64, proc.Threads.Count, proc.HandleCount));
    Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} polls={polls} errors={errors} reconnects={reconnects} card={cardEvents} mem={proc.WorkingSet64 / 1048576}MB threads={proc.Threads.Count} handles={proc.HandleCount}");
    try { await Task.Delay(sampleEvery); } catch { }
}

var first = samples.First(); var last = samples.Last();
double memDriftPct = first.Mem == 0 ? 0 : 100.0 * (last.Mem - first.Mem) / first.Mem;
Console.WriteLine("=== RESULT ===");
Console.WriteLine($"polls={polls} errors={errors} reconnects={reconnects} cardEvents={cardEvents}");
Console.WriteLine($"mem {first.Mem / 1048576}MB -> {last.Mem / 1048576}MB ({memDriftPct:+0.0;-0.0}%), threads {first.Threads}->{last.Threads}, handles {first.Handles}->{last.Handles}");
Console.WriteLine(memDriftPct < 10 && last.Threads - first.Threads < 5 ? "PASS" : "FAIL: resource drift");
Environment.Exit(memDriftPct < 10 && last.Threads - first.Threads < 5 ? 0 : 1);
