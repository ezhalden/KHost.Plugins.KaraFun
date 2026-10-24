namespace KHost.Plugins.KaraFun.Kit;

/// <summary>The file names this plugin owns, shared by the gate on <see cref="KaraFunMediaProvider"/>
/// and the probe (<see cref="KaraFunKitProbe"/>), the timing (<see cref="KaraFunTimedLyricsProvider"/>)
/// and the renderer (<see cref="KaraFunKitRenderer"/>): they must claim the same files, or a song is
/// gated by one and unreadable by the other.</summary>
/// <remarks>Decided from the extension alone: the gate and the probe are asked for every queued
/// turn on every reconcile, and reading the file to answer would make a cheap question expensive.
/// Nothing but KaraFun produces a <c>.kit</c>, so the extension is the whole answer.</remarks>
internal static class KitFiles
{
    /// <summary>This plugin's own assembly name, written onto its files as the gate's
    /// <c>khost_provider</c> tag and answered as the gate's own id, which must match.</summary>
    public const string ProviderId = "KHost.Plugins.KaraFun";

    /// <summary>The library row: the download, kept exactly as KaraFun served it, and the only file
    /// this plugin reads. Its stems go to the host as they are; no standard, openable copy of
    /// licensed content is ever written beside it.</summary>
    public const string KitExtension = ".kit";

    /// <summary>Whether this file is a kit.</summary>
    public static bool IsAKit(string filePath)
        => Path.GetExtension(filePath).Equals(KitExtension, StringComparison.OrdinalIgnoreCase);
}
