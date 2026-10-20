namespace KHost.Plugins.KaraFun;

/// <summary>Typed view of manifest.json's settings, minus the login: a setting lands in
/// <c>plugins.json</c>, and a password has no business on disk once a session key covers it.</summary>
public class KaraFunSettings
{
    /// <summary>Picks one guest may send inside <see cref="RemotePickWindowMinutes"/>. Zero or less
    /// lifts the limit.</summary>
    public int RemotePickLimit { get; set; } = 5;

    /// <summary>The sliding window <see cref="RemotePickLimit"/> counts over. Zero or less lifts the
    /// limit.</summary>
    public int RemotePickWindowMinutes { get; set; } = 10;

    /// <summary>Marks each downloaded song single-use: KHost deletes the file once it has been sung
    /// and asks for it again when the song is next queued.</summary>
    public bool SingleUse { get; set; } = true;
}
