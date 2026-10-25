using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.KaraFun.Kit;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.KaraFun;

/// <summary>Hands the host a kit's timed words, which a screen draws itself and the host burns into
/// its own encode for a display that cannot; the host never learns the kit's own format. Needs only
/// the file, never the KaraFun session, which lives on <see cref="KaraFunMediaProvider"/>.</summary>
/// <remarks>Its own extension type, so the host binds it as its own singleton; nothing here is
/// started in the constructor.</remarks>
public class KaraFunTimedLyricsProvider : ITimedLyricsProvider
{
    private readonly ILogger<KaraFunTimedLyricsProvider> _logger;

    /// <param name="plugin">Unused, and still required: the host builds every extension type with
    /// the plugin's context as a given argument, and ActivatorUtilities refuses a constructor
    /// that has nowhere to put one.</param>
    public KaraFunTimedLyricsProvider(IPluginContext plugin, ILogger<KaraFunTimedLyricsProvider> logger)
    {
        _logger = logger;
    }

    /// <summary>Answered from the path alone, the same rule <see cref="KaraFunMediaProvider.Claims"/>
    /// and <see cref="KaraFunKitProbe.CanProbe"/> follow: this is asked for every file that loads,
    /// not only this plugin's own.</summary>
    public bool CanProvide(string filePath) => KitFiles.IsAKit(filePath);

    /// <summary>Reads a kit's own id-1 timing stream.</summary>
    /// <remarks>Null is the ordinary answer for a file with no timing to give, not an error: a kit
    /// with a corrupt or missing timing stream was never importable in the first place, so this is
    /// reached only when something has changed under an already-imported row.</remarks>
    public async Task<TimedLyrics?> GetTimedLyricsAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var xml = await ReadKitTimingAsync(filePath, cancellationToken);

            return xml is null ? null : TimedLyricsMapper.Map(TimingDocument.Parse(xml));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the timing for '{FilePath}'", filePath);
            return null;
        }
    }

    private static async Task<string?> ReadKitTimingAsync(string filePath, CancellationToken cancellationToken)
    {
        var streams = KitContainer.Read(await File.ReadAllBytesAsync(filePath, cancellationToken)).Demux();
        var timing = streams.FirstOrDefault(stream => stream.Kind == "xml");

        return timing is null ? null : System.Text.Encoding.UTF8.GetString(timing.Data);
    }
}
