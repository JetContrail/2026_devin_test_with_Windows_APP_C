// Security tests: TLS wrapping, cert pinning, mutual PSK auth, multi-device.
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Monitoring.Net;
using Monitoring.Protocol;
using Xunit;

namespace Monitoring.IntegrationTests;

public class TlsAuthTests : IAsyncLifetime
{
    private static readonly byte[] Psk = Convert.FromHexString(
        "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF");

    private X509Certificate2 _serverCert = null!;
    private TcpListener? _listener;
    private int _port;
    private byte[]? _serverPsk;
    private bool _requireTls;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();
    private readonly List<TcpClient> _clients = new();

    public Task InitializeAsync()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=FakeDevice", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        _serverCert = new X509Certificate2(req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1))
            .Export(X509ContentType.Pfx));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try { _listener?.Stop(); } catch (SocketException) { }
        lock (_clients)
            foreach (var c in _clients)
                try { c.Dispose(); } catch (ObjectDisposedException) { }
        await _stop.CancelAsync();
        try { await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        _serverCert.Dispose();
    }

    private int StartServer(bool tls, byte[]? psk)
    {
        _requireTls = tls;
        _serverPsk = psk;
        _listener = new TcpListener(IPAddress.Loopback, _port == 0 ? 0 : _port);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
        return _port;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var c = await _listener!.AcceptTcpClientAsync(_stop.Token);
                lock (_clients) _clients.Add(c);
                _connections.Add(Task.Run(() => ServeAsync(c)));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient c)
    {
        try
        {
            Stream stream = c.GetStream();
            if (_requireTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(_serverCert,
                    clientCertificateRequired: false, checkCertificateRevocation: false);
                stream = ssl;
            }
            bool authed = _serverPsk is null;
            var buf = new byte[4096];
            var rx = new List<byte>();
            while (true)
            {
                int n = await stream.ReadAsync(buf, _stop.Token);
                if (n == 0) return;
                rx.AddRange(buf.AsSpan(0, n).ToArray());
                while (rx.Count >= FrameCodec.HeaderSize + FrameCodec.CrcSize)
                {
                    int len = FrameCodec.HeaderSize + ((rx[6] << 8) | rx[7]) + FrameCodec.CrcSize;
                    if (rx.Count < len) break;
                    var frame = rx.GetRange(0, len).ToArray();
                    rx.RemoveRange(0, len);
                    if (!FrameCodec.TryDecode(frame, out var type, out var seq, out var payload))
                        continue;
                    if (!authed)
                    {
                        bool ok = type == MessageType.AuthHello
                                  && payload.Length == AuthCode.HelloPayloadSize
                                  && AuthCode.VerifyClient(_serverPsk!,
                                      payload[..AuthCode.NonceSize],
                                      payload[AuthCode.NonceSize..]);
                        if (!ok)
                        {
                            await stream.WriteAsync(FrameCodec.Encode(
                                MessageType.ErrorResponse, seq, [4]));
                            return;
                        }
                        await stream.WriteAsync(FrameCodec.Encode(MessageType.AuthAck, seq,
                            AuthCode.ServerProof(_serverPsk!,
                                payload[..AuthCode.NonceSize])));
                        authed = true;
                        continue;
                    }
                    if (type == MessageType.PollStatus)
                        await stream.WriteAsync(FrameCodec.Encode(MessageType.StatusResponse, seq,
                            [(byte)LockState.Locked, (byte)DoorState.Closed, 0, 0, 0, 0,
                             (byte)CardReaderStatus.Online, 0, 0]));
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException
            or OperationCanceledException or AuthenticationException)
        { }
        finally { c.Dispose(); }
    }

    private TcpDeviceClient NewClient(DeviceSecurityOptions? security = null)
        => new(new IPEndPoint(IPAddress.Loopback, _port), 500,
               failuresBeforeReconnect: 3,
               backoffBase: TimeSpan.FromMilliseconds(20),
               backoffMax: TimeSpan.FromMilliseconds(200),
               security: security);

    private static async Task WaitForAsync(Func<bool> cond, int ms = 8000)
    {
        for (int i = 0; i < ms / 10 && !cond(); i++)
            await Task.Delay(10);
        Assert.True(cond(), "timed out waiting for condition");
    }

    [Fact]
    public async Task PskOnly_MutualAuth_RoundTrips()
    {
        StartServer(tls: false, psk: Psk);
        await using var client = NewClient(new DeviceSecurityOptions { Psk = Psk });
        var s = await client.PollStatusAsync(CancellationToken.None);
        Assert.Equal(LockState.Locked, s.Lock);
    }

    [Fact]
    public async Task PskOnly_WrongKey_NeverComesUp()
    {
        StartServer(tls: false, psk: Psk);
        var wrong = new byte[32]; wrong[0] = 0xEE;
        await using var client = NewClient(new DeviceSecurityOptions { Psk = wrong });
        var events = new List<TransportLinkState>();
        client.LinkChanged += (_, s) => events.Add(s);
        _ = client.PollStatusAsync(CancellationToken.None);
        await Task.Delay(700);
        Assert.DoesNotContain(TransportLinkState.Up, events);
        Assert.Contains(TransportLinkState.Reconnecting, events);
    }

    [Fact]
    public async Task PskOnly_UnsecuredServer_ClientAuthsButDeviceSkipsHandshake()
    {
        // device without psk treats AuthHello as unknown → ErrorResponse → auth fails → retry
        StartServer(tls: false, psk: null);
        await using var client = NewClient(new DeviceSecurityOptions { Psk = Psk });
        var events = new List<TransportLinkState>();
        client.LinkChanged += (_, s) => events.Add(s);
        _ = client.PollStatusAsync(CancellationToken.None);
        await Task.Delay(700);
        Assert.DoesNotContain(TransportLinkState.Up, events);
    }

    [Fact]
    public async Task Tls_WithPinning_AndPsk_RoundTrips()
    {
        StartServer(tls: true, psk: Psk);
        await using var client = NewClient(new DeviceSecurityOptions
        {
            UseTls = true,
            TargetHost = "FakeDevice",
            ExpectedCertThumbprint = _serverCert.GetCertHashString(),
            Psk = Psk,
        });
        var s = await client.PollStatusAsync(CancellationToken.None);
        Assert.Equal(LockState.Locked, s.Lock);
    }

    [Fact]
    public async Task Tls_WrongThumbprint_Rejected()
    {
        StartServer(tls: true, psk: null);
        await using var client = NewClient(new DeviceSecurityOptions
        {
            UseTls = true,
            TargetHost = "FakeDevice",
            ExpectedCertThumbprint = "AAAA0000BBBB1111CCCC2222DDDD3333EEEE4444",
        });
        var events = new List<TransportLinkState>();
        client.LinkChanged += (_, s) => events.Add(s);
        _ = client.PollStatusAsync(CancellationToken.None);
        await Task.Delay(700);
        Assert.DoesNotContain(TransportLinkState.Up, events);
    }

    [Fact]
    public async Task Tls_ServerWithoutTls_ClientNeverConnects()
    {
        StartServer(tls: false, psk: null);
        await using var client = NewClient(new DeviceSecurityOptions
        {
            UseTls = true,
            TargetHost = "FakeDevice",
        });
        var events = new List<TransportLinkState>();
        client.LinkChanged += (_, s) => events.Add(s);
        _ = client.PollStatusAsync(CancellationToken.None);
        await Task.Delay(700);
        Assert.DoesNotContain(TransportLinkState.Up, events);
    }

    [Fact]
    public async Task MultipleDevices_IndependentConnections()
    {
        StartServer(tls: false, psk: null);
        int port1 = _port;
        var listener2 = new TcpListener(IPAddress.Loopback, 0);
        listener2.Start();
        int port2 = ((IPEndPoint)listener2.LocalEndpoint).Port;
        listener2.Stop();

        await using var c1 = NewClient();
        await using var c2 = new TcpDeviceClient(new IPEndPoint(IPAddress.Loopback, port2), 500,
            backoffBase: TimeSpan.FromMilliseconds(20),
            backoffMax: TimeSpan.FromMilliseconds(200));
        // port2 has no listener: c2 retries forever while c1 still works
        var s = await c1.PollStatusAsync(CancellationToken.None);
        Assert.Equal(LockState.Locked, s.Lock);
        var events = new List<TransportLinkState>();
        c2.LinkChanged += (_, st) => events.Add(st);
        _ = c2.PollStatusAsync(CancellationToken.None);
        await WaitForAsync(() =>
        {
            lock (events) return events.Contains(TransportLinkState.Reconnecting);
        });
        // c1 unaffected
        Assert.Equal(LockState.Locked, (await c1.PollStatusAsync(CancellationToken.None)).Lock);
    }
}
