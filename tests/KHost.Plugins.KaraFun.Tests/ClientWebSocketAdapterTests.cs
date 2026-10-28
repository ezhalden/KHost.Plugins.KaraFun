using System.Net.WebSockets;
using System.Reflection;
using KHost.Plugins.KaraFun.Client.Kcs;

namespace KHost.Plugins.KaraFun.Tests;

public class ClientWebSocketAdapterTests
{
    /// <summary>An infinite timeout (the framework's default) is the bug: a vanished network is never noticed.</summary>
    [Fact]
    public void TheRealSocket_NoticesAPeerThatStopsAnswering()
    {
        using var adapter = new ClientWebSocketAdapter();
        var socket = (ClientWebSocket)typeof(ClientWebSocketAdapter)
            .GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(adapter)!;

        Assert.Equal(ClientWebSocketAdapter.KeepAliveInterval, socket.Options.KeepAliveInterval);
        Assert.Equal(ClientWebSocketAdapter.KeepAliveTimeout, socket.Options.KeepAliveTimeout);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, socket.Options.KeepAliveTimeout);
    }

    [Fact]
    public void ConfigureKeepAlive_SetsBothHalves()
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromMinutes(5);
        socket.Options.KeepAliveTimeout = Timeout.InfiniteTimeSpan;

        ClientWebSocketAdapter.ConfigureKeepAlive(socket.Options);

        Assert.Equal(ClientWebSocketAdapter.KeepAliveInterval, socket.Options.KeepAliveInterval);
        Assert.Equal(ClientWebSocketAdapter.KeepAliveTimeout, socket.Options.KeepAliveTimeout);
    }
}
