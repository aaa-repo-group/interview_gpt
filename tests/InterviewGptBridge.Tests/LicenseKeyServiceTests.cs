using InterviewGptBridge.Licensing;
using Xunit;

namespace InterviewGptBridge.Tests;

public sealed class LicenseKeyServiceTests
{
    [Fact]
    public void CreateSixMonthLicense_ValidatesForSameDevice()
    {
        var issued = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var licenseKey = LicenseKeyService.CreateSixMonthLicense("abcd-1234", issued);

        var result = LicenseKeyService.Validate(licenseKey, "ABCD1234", issued.AddMonths(3));

        Assert.True(result.IsValid);
        Assert.Equal(issued.AddMonths(6), result.ExpiresUtc);
        Assert.Equal("ABCD1234", result.DeviceId);
    }

    [Fact]
    public void Validate_RejectsDifferentDevice()
    {
        var issued = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var licenseKey = LicenseKeyService.CreateSixMonthLicense("device-one", issued);

        var result = LicenseKeyService.Validate(licenseKey, "device-two", issued);

        Assert.False(result.IsValid);
        Assert.Equal("License key is for a different device.", result.Message);
    }

    [Fact]
    public void Validate_RejectsExpiredLicense()
    {
        var issued = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        var licenseKey = LicenseKeyService.CreateSixMonthLicense("device-one", issued);

        var result = LicenseKeyService.Validate(licenseKey, "device-one", issued.AddMonths(6).AddSeconds(1));

        Assert.False(result.IsValid);
        Assert.Equal("License key has expired.", result.Message);
    }
}
