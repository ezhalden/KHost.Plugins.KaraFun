using System.Security.Cryptography;
using System.Text;
using KHost.Plugins.KaraFun.Client;

namespace KHost.Plugins.KaraFun.Tests;

public class RequestSignerTests
{
    [Fact]
    public void SignExact_NoSessionKey_ReturnsNull()
        => Assert.Null(RequestSigner.SignExact("https://api.karafun.com/session/info", sessionKey: null));

    [Fact]
    public void SignExact_EmptySessionKey_ReturnsNull()
        => Assert.Null(RequestSigner.SignExact("https://api.karafun.com/session/info", sessionKey: ""));

    [Fact]
    public void SignExact_MatchesTheDocumentedFormula()
    {
        const string url = "https://api.karafun.com/session/info?client=8";
        const string sessionKey = "abc123";
        const long unixSeconds = 1_700_000_000;

        var sig = RequestSigner.SignExact(url, sessionKey, unixSeconds);

        var expectedSigned = $"{RequestSigner.VersionTag}{url}|{unixSeconds}|{sessionKey}";
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expectedSigned)));

        Assert.NotNull(sig);
        Assert.Equal(unixSeconds.ToString(), sig.Timestamp);
        Assert.Equal(expectedHash, sig.Value);
    }

    [Fact]
    public void SignExact_SameInputsTwice_IsDeterministic()
    {
        var first = RequestSigner.SignExact("https://api.karafun.com/x", "key", 1_700_000_000);
        var second = RequestSigner.SignExact("https://api.karafun.com/x", "key", 1_700_000_000);

        Assert.Equal(first, second);
    }
}
