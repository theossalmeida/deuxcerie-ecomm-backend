using System.Security.Cryptography;
using System.Text;

namespace EcommerceApi.Infrastructure.Services;

public class WebhookValidationService
{
    public bool ValidateWebhookSecret(string received, string expected)
    {
        var a = Encoding.UTF8.GetBytes(received ?? "");
        var b = Encoding.UTF8.GetBytes(expected ?? "");
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public bool ValidateHmacSignature(string rawBody, string signatureFromHeader, string hmacKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(hmacKey);
        var bodyBytes = Encoding.UTF8.GetBytes(rawBody);

        using var hmac = new HMACSHA256(keyBytes);
        var computedHash = hmac.ComputeHash(bodyBytes);
        var computedSignature = Convert.ToBase64String(computedHash);

        var a = Encoding.UTF8.GetBytes(computedSignature);
        var b = Encoding.UTF8.GetBytes(signatureFromHeader ?? "");
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
