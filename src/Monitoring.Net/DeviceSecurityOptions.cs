namespace Monitoring.Net;

/// <summary>Transport security for a device connection (protocol spec §9).
/// TLS encrypts the stream and pins the device certificate; PSK adds the
/// application-level AuthHello/AuthAck handshake.</summary>
public sealed record DeviceSecurityOptions
{
    /// <summary>Wrap the TCP stream in TLS (SslStream).</summary>
    public bool UseTls { get; init; }

    /// <summary>TLS server name (SNI / cert CN check). Defaults to the device IP.</summary>
    public string? TargetHost { get; init; }

    /// <summary>Pinned device certificate thumbprint (hex, no separators).
    /// When set, a TLS device presenting any other certificate is rejected.</summary>
    public string? ExpectedCertThumbprint { get; init; }

    /// <summary>Pre-shared key for the AuthHello/AuthAck handshake. Null disables auth.</summary>
    public byte[]? Psk { get; init; }

    public static readonly DeviceSecurityOptions None = new();
}
