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

    /// <summary>Parse a hex key. A present-but-invalid value must fail closed:
    /// silently ignoring it would run door control without authentication.</summary>
    public static byte[] ParseHexRequired(string hex, string keyName)
    {
        try { return Convert.FromHexString(hex); }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"appsettings.json: \"{keyName}\" is set but not valid hex. " +
                "Replace the placeholder with the device key, or set it to null to disable PSK.");
        }
    }

    /// <summary>Build options from raw config fields (all strings optional).
    /// Throws InvalidOperationException on a present-but-invalid pskHex.
    /// Security is mandatory: TLS+PSK are required unless allowInsecure is set
    /// explicitly (lab/demo use only — auth is a project requirement).</summary>
    public static DeviceSecurityOptions FromConfig(
        bool useTls, string? targetHost, string? certThumbprint, string? pskHex,
        bool allowInsecure = false)
    {
        if (!allowInsecure && (!useTls || pskHex is not { Length: > 0 }))
            throw new InvalidOperationException(
                "appsettings.json: secure transport is required — set \"UseTls\": true and a valid \"PskHex\", " +
                "or set \"AllowInsecure\": true explicitly for lab/demo use only.");
        var psk = pskHex is { Length: > 0 } ? ParseHexRequired(pskHex, "PskHex") : null;
        if (psk is { Length: < 16 })
            throw new InvalidOperationException(
                "appsettings.json: \"PskHex\" must be at least 16 bytes (32 hex chars) — " +
                "a shorter key can be brute-forced from an observed AuthHello.");
        return new()
        {
            UseTls = useTls,
            TargetHost = targetHost,
            ExpectedCertThumbprint = certThumbprint,
            Psk = psk,
        };
    }
}
