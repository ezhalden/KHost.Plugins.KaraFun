using System.Security.Cryptography;
using System.Text;

namespace KHost.Plugins.KaraFun.Client;

/// <summary>Reproduces <c>KFAPICoordinator.additionalHeadersFor</c> bit for bit, reverse engineered
/// from the KaraFun.app binary; the server verifies the signed string exactly as sent.</summary>
public static class RequestSigner
{
    public const string VersionTag = "kfun-v1.5|";

    public sealed record Signature(string Value, string Timestamp);

    /// <summary>Null when <paramref name="sessionKey"/> is empty, matching the unsigned open-session
    /// request. Pass the exact string the request line carries; the server signs it verbatim.</summary>
    public static Signature? SignExact(string fullUrl, string? sessionKey, long? unixSeconds = null)
    {
        if (string.IsNullOrEmpty(sessionKey)) return null;

        long secs = unixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string ts = secs.ToString();
        string signed = VersionTag + fullUrl + "|" + ts + "|" + sessionKey;
        string sig = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signed)));

        return new Signature(sig, ts);
    }
}
