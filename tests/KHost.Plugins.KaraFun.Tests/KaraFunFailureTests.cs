using System.Net;
using System.Net.Sockets;

namespace KHost.Plugins.KaraFun.Tests;

public class KaraFunFailureTests
{
    [Fact]
    public void AnHttpFailureWithNoStatus_IsNetwork()
        => Assert.True(KaraFunFailure.IsNetwork(new HttpRequestException("nodename nor servname provided")));

    /// <summary>The server answered, so KaraFun was reachable.</summary>
    [Fact]
    public void AnHttpFailureWithAStatus_IsNot()
        => Assert.False(KaraFunFailure.IsNetwork(
            new HttpRequestException("Not Found", inner: null, HttpStatusCode.NotFound)));

    [Fact]
    public void ASocketError_IsNetwork()
        => Assert.True(KaraFunFailure.IsNetwork(new SocketException()));

    [Fact]
    public void AnIoErrorWrappingASocketError_IsNetwork()
        => Assert.True(KaraFunFailure.IsNetwork(new IOException("read failed", new SocketException())));

    [Fact]
    public void AnIoErrorOnItsOwn_IsNot()
        => Assert.False(KaraFunFailure.IsNetwork(new IOException("disk full")));

    [Fact]
    public void ATimeout_IsNetwork()
        => Assert.True(KaraFunFailure.IsNetwork(new TimeoutException()));

    [Fact]
    public void ACancelNobodyAskedFor_IsNetwork()
        => Assert.True(KaraFunFailure.IsNetwork(new TaskCanceledException("HttpClient.Timeout"), CancellationToken.None));

    [Fact]
    public void ACancelTheCallerAskedFor_IsNot()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.False(KaraFunFailure.IsNetwork(new TaskCanceledException(), source.Token));
    }

    [Fact]
    public void Describe_ANetworkFailure_IsTheHostsWording()
        => Assert.Equal(
            "Could not reach KaraFun. Check this computer is online, then try again.",
            KaraFunFailure.Describe("search", new HttpRequestException("nodename nor servname provided")));

    [Fact]
    public void Describe_AnythingElse_NamesWhatFailedAndWhy()
        => Assert.Equal(
            "KaraFun search failed: boom",
            KaraFunFailure.Describe("search", new InvalidOperationException("boom")));
}
