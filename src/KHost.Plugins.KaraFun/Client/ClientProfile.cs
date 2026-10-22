namespace KHost.Plugins.KaraFun.Client;

/// <summary>A compiled-in KaraFun client identity, read verbatim from KaraFun.app 2.17.2.</summary>
/// <remarks><see cref="Key"/> folds into the signature; <see cref="Id"/> is sent as the client id.</remarks>
public sealed record ClientProfile(int Id, string Key, string UserAgentPrefix)
{
    /// <summary>Production consumer app on macOS, the identity this plugin impersonates.</summary>
    public static readonly ClientProfile MacOS = new(8, "koPo@u_A", "KaraFun/macOS/");

    /// <summary>Full User-Agent, e.g. "KaraFun/macOS/2.17.2".</summary>
    public string UserAgent(string appVersion) => UserAgentPrefix + appVersion;
}
