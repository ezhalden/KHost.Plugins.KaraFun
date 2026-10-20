using KHost.Abstractions.Services;

namespace KHost.Plugins.KaraFun;

/// <summary>Opens the room before a guest's phone could reach it, sharing the loader's one
/// <see cref="KaraFunMediaProvider"/> instance rather than building a second session and socket.</summary>
public sealed class KaraFunPlugin : IPlugin<KaraFunSettings>
{
    private readonly KaraFunMediaProvider _provider;

    public KaraFunPlugin(KaraFunMediaProvider provider) => _provider = provider;

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        // Not awaited: the host runs this blocking on its startup thread, and a session is a round
        // trip over whatever wifi the room has, so awaiting it stalls the console on a slow server.
        _provider.StartRemoteInBackground(cancellationToken);

        return Task.CompletedTask;
    }
}
