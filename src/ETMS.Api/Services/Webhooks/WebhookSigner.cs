using System.Security.Cryptography;
using System.Text;

namespace ETMS.Api.Services.Webhooks;

/// <summary>FR-HOOK-04 signature: <c>t={unix},v1={hex(HMAC-SHA256(secret, "{t}.{body}"))}</c>.</summary>
public static class WebhookSigner
{
    public const string SignatureHeader = "X-ETMS-Signature";
    public const string EventHeader = "X-ETMS-Event";
    public const string DeliveryHeader = "X-ETMS-Delivery";

    public static string ComputeSignature(string secret, long timestamp, string payload)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    public static string BuildHeader(string secret, long timestamp, string payload) =>
        $"t={timestamp},v1={ComputeSignature(secret, timestamp, payload)}";
}
