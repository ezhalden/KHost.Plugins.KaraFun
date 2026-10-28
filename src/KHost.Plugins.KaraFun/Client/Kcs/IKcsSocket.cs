namespace KHost.Plugins.KaraFun.Client.Kcs;

/// <summary>Behind an interface for the same reason <see cref="KaraFunApi"/> takes an
/// <see cref="System.Net.Http.HttpClient"/>: reconnect pacing and handshake order want testing.</summary>
internal interface IKcsSocket : IDisposable
{
    bool IsOpen { get; }

    Task ConnectAsync(Uri url, CancellationToken cancellationToken);

    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>One whole message, or null once the peer has closed. Text frames read as empty.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken);
}
