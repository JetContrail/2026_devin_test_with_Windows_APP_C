using Monitoring.Net;
using Xunit;

namespace Monitoring.UnitTests;

public class SecurityConfigTests
{
    // regression: a present-but-invalid PskHex must fail closed, never silently
    // skip authentication (placeholder in shipped appsettings must be replaced)
    [Fact]
    public void FromConfig_InvalidPskHex_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeviceSecurityOptions.FromConfig(true, "DoorDevice", "AA00",
                "REPLACE_WITH_DEVICE_SPECIFIC_64_HEX_KEY"));
        Assert.Contains("PskHex", ex.Message);
    }

    [Fact]
    public void FromConfig_ValidPskHex_Parses()
    {
        var opt = DeviceSecurityOptions.FromConfig(true, "DoorDevice", "AA00",
            "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF");
        Assert.NotNull(opt.Psk);
        Assert.Equal(32, opt.Psk!.Length);
        Assert.Equal(0x00, opt.Psk[0]);
        Assert.Equal(0xFF, opt.Psk[31]);
        Assert.True(opt.UseTls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromConfig_AbsentPskHex_NoAuth(string? psk)
    {
        var opt = DeviceSecurityOptions.FromConfig(false, null, null, psk, allowInsecure: true);
        Assert.Null(opt.Psk);
    }

    // auth+encryption are required by spec: omitting TLS or PSK must fail unless
    // the operator explicitly opts into insecure mode
    [Theory]
    [InlineData(false, "00112233445566778899AABBCCDDEEFF")] // PSK but no TLS
    [InlineData(true, null)]   // TLS but no PSK
    [InlineData(false, null)]  // neither
    public void FromConfig_InsecureWithoutOptIn_Throws(bool useTls, string? psk)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeviceSecurityOptions.FromConfig(useTls, "DoorDevice", "AA00", psk));
        Assert.Contains("AllowInsecure", ex.Message);
    }

    [Fact]
    public void FromConfig_InsecureWithOptIn_Allowed()
    {
        var opt = DeviceSecurityOptions.FromConfig(false, null, null, null, allowInsecure: true);
        Assert.False(opt.UseTls);
        Assert.Null(opt.Psk);
    }
}
