using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InterviewGptBridge.Licensing;

public sealed record LicenseValidationResult(
    bool IsValid,
    string Message,
    DateTimeOffset? ExpiresUtc = null,
    string? DeviceId = null);

public static class LicenseKeyService
{
    private const int CurrentVersion = 1;
    private const string ProductId = "InterviewGPT";
    private static readonly byte[] SecretKey = Convert.FromHexString("7765D0E4D3AB45C899A0BC79C4F113AD7B5B46F65777A430CF9A13CF13428D97");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string CreateSixMonthLicense(string deviceId, DateTimeOffset? issuedUtc = null)
    {
        var issued = ToUtc(issuedUtc ?? DateTimeOffset.UtcNow);
        return CreateLicense(deviceId, issued.AddMonths(6), issued);
    }

    public static string CreateLicense(string deviceId, DateTimeOffset expiresUtc, DateTimeOffset? issuedUtc = null)
    {
        var normalizedDeviceId = NormalizeDeviceId(deviceId);
        if (string.IsNullOrWhiteSpace(normalizedDeviceId))
        {
            throw new ArgumentException("Device id is required.", nameof(deviceId));
        }

        var issued = ToUtc(issuedUtc ?? DateTimeOffset.UtcNow);
        var expires = ToUtc(expiresUtc);
        if (expires <= issued)
        {
            throw new ArgumentException("Expiration must be later than the issued time.", nameof(expiresUtc));
        }

        var payload = new LicensePayload
        {
            Version = CurrentVersion,
            Product = ProductId,
            DeviceId = normalizedDeviceId,
            IssuedUtc = issued.ToUnixTimeSeconds(),
            ExpiresUtc = expires.ToUnixTimeSeconds()
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var payloadPart = Base64UrlEncode(payloadBytes);
        var signature = Sign(payloadPart);
        return payloadPart + "." + Base64UrlEncode(signature);
    }

    public static LicenseValidationResult Validate(string? licenseKey, string expectedDeviceId, DateTimeOffset? nowUtc = null)
    {
        var normalizedLicense = NormalizeLicenseKey(licenseKey);
        if (string.IsNullOrWhiteSpace(normalizedLicense))
        {
            return new LicenseValidationResult(false, "Enter a license key.");
        }

        var expectedDevice = NormalizeDeviceId(expectedDeviceId);
        if (string.IsNullOrWhiteSpace(expectedDevice))
        {
            return new LicenseValidationResult(false, "This device id is unavailable.");
        }

        try
        {
            var parts = normalizedLicense.Split('.');
            if (parts.Length != 2)
            {
                return new LicenseValidationResult(false, "License key format is invalid.");
            }

            var expectedSignature = Sign(parts[0]);
            var providedSignature = Base64UrlDecode(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
            {
                return new LicenseValidationResult(false, "License key signature is invalid.");
            }

            var payload = JsonSerializer.Deserialize<LicensePayload>(Base64UrlDecode(parts[0]), JsonOptions);
            if (payload is null)
            {
                return new LicenseValidationResult(false, "License payload is invalid.");
            }

            if (payload.Version != CurrentVersion || !string.Equals(payload.Product, ProductId, StringComparison.Ordinal))
            {
                return new LicenseValidationResult(false, "License key is not for this app.");
            }

            if (!string.Equals(payload.DeviceId, expectedDevice, StringComparison.Ordinal))
            {
                return new LicenseValidationResult(false, "License key is for a different device.");
            }

            var expires = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresUtc);
            var now = ToUtc(nowUtc ?? DateTimeOffset.UtcNow);
            if (now > expires)
            {
                return new LicenseValidationResult(false, "License key has expired.", expires, payload.DeviceId);
            }

            return new LicenseValidationResult(true, "License accepted.", expires, payload.DeviceId);
        }
        catch (FormatException)
        {
            return new LicenseValidationResult(false, "License key format is invalid.");
        }
        catch (JsonException)
        {
            return new LicenseValidationResult(false, "License payload is invalid.");
        }
        catch (ArgumentException)
        {
            return new LicenseValidationResult(false, "License key format is invalid.");
        }
    }

    public static string NormalizeDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(deviceId.Length);
        foreach (var character in deviceId)
        {
            if (char.IsWhiteSpace(character) || character == '-')
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString();
    }

    public static string FormatDeviceId(string? deviceId)
    {
        var normalized = NormalizeDeviceId(deviceId);
        if (normalized.Length <= 8)
        {
            return normalized;
        }

        var groups = Enumerable.Range(0, (normalized.Length + 7) / 8)
            .Select(index => normalized.Substring(index * 8, Math.Min(8, normalized.Length - index * 8)));
        return string.Join("-", groups);
    }

    private static string NormalizeLicenseKey(string? licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(licenseKey.Length);
        foreach (var character in licenseKey)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static byte[] Sign(string payloadPart)
    {
        return HMACSHA256.HashData(SecretKey, Encoding.UTF8.GetBytes(payloadPart));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;
        if (padding > 0)
        {
            base64 = base64.PadRight(base64.Length + 4 - padding, '=');
        }

        return Convert.FromBase64String(base64);
    }

    private static DateTimeOffset ToUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime();
    }

    private sealed class LicensePayload
    {
        public int Version { get; set; }
        public string Product { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public long IssuedUtc { get; set; }
        public long ExpiresUtc { get; set; }
    }
}
