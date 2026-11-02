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
        var opt = DeviceSecurityOptions.FromConfig(false, null, null, psk);
        Assert.Null(opt.Psk);
    }
}
