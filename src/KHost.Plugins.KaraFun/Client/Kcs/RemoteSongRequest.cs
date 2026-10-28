namespace KHost.Plugins.KaraFun.Client.Kcs;

/// <summary>
/// One pick, as a guest's phone asked for it. Carries no KHost types on purpose: what arrives off
/// the wire is a KaraFun song id and a name typed into a web page, and turning either into a
/// library row or a singer is the host's business, not the channel's.
/// </summary>
/// <param name="SongId">KaraFun's id for the song. Catalog and community ids overlap in value.</param>
/// <param name="Community">Whether <paramref name="SongId"/> is a community id rather than a catalog one.</param>
/// <param name="SingerName">
/// Who to put it on: the name the guest gave the pick, or failing that the name they joined the
/// room under. Never blank, since a request that resolves to neither is dropped before it gets here.
/// </param>
/// <param name="RemoteId">
/// The phone that asked, as KaraFun names it. Blank when the pick carried none. A guest who queues
/// five songs in a night can spell their name differently each time, but this does not change
/// between picks, which makes it the steadier half of this record's identity. Good only for as
/// long as the pairing is, which is why the host stores it as ephemeral.
/// </param>
internal sealed record RemoteSongRequest(int SongId, bool Community, string SingerName, string RemoteId);
