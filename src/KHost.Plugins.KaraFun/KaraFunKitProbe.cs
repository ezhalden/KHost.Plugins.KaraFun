using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.KaraFun.Kit;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.KaraFun;

/// <summary>A kit is ours to describe: ffprobe reads one as "Invalid data found", which the host
/// cannot tell apart from a file with no tracks and no tags, and only this plugin knows which stem
/// is which.</summary>
/// <remarks>Its own extension type, so the host binds it as its own singleton; nothing here is
/// started in the constructor. Same answer as <see cref="KaraFunMediaProvider.Claims"/>, but not
/// the same question: that one says whose file it is, this one says who can read it. Content we
/// gated but the host could describe itself would claim there and not here.</remarks>
public class KaraFunKitProbe : IMediaProbe
{
    private readonly ILogger<KaraFunKitProbe> _logger;

    /// <param name="plugin">Unused, and still required: the host builds every extension type with
    /// the plugin's context as a given argument, and ActivatorUtilities refuses a constructor
    /// that has nowhere to put one.</param>
    public KaraFunKitProbe(IPluginContext plugin, ILogger<KaraFunKitProbe> logger)
    {
        _logger = logger;
    }

    /// <remarks>Answered from the path alone, the same rule <see cref="KaraFunMediaProvider.Claims"/>
    /// follows: this is asked for every file that loads, not only this plugin's own.</remarks>
    public bool CanProbe(string filePath) => KitFiles.IsAKit(filePath);

    /// <summary>Reads the stems and the length out of the kit before any playback is.</summary>
    public async Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var probe = await ProbeKitAsync(filePath, cancellationToken);

            // Said per load: which fader a singer lands on is otherwise invisible in a log.
            _logger.LogInformation("Kit '{FilePath}' carries {Tracks}", filePath,
                string.Join(", ", probe.AudioTracks.Select(track => track.Voice is null ? $"{track.Role}" : $"{track.Role} ({track.Voice})")));

            return probe;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not probe '{FilePath}'", filePath);
            return null;
        }
    }

    /// <summary>The stems are named from the kit's own file labels: ffmpeg cannot read a kit at
    /// all, so without them a host would have no way to tell the lead from the music.</summary>
    /// <remarks>The gate's marker is reported too, but nothing reads it: the host asks
    /// <see cref="KaraFunMediaProvider.Claims"/> first and the extension already answers. Kept
    /// because it costs a dictionary entry.</remarks>
    private static async Task<MediaProbeResult> ProbeKitAsync(string filePath, CancellationToken cancellationToken)
    {
        var streams = KitContainer.Read(await File.ReadAllBytesAsync(filePath, cancellationToken)).Demux();

        var tracks = KitStems.Describe(streams)
            .Select((stem, index) => new AudioTrack(index, stem.Role, stem.Name) { Voice = stem.Voice })
            .ToList();

        var timing = streams.FirstOrDefault(stream => stream.Kind == "xml");

        return new MediaProbeResult
        {
            Duration = timing is null ? null : DurationOf(timing),
            AudioTracks = tracks,
            Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [IMediaPlaybackGate.MetadataTag] = KitFiles.ProviderId,
            },
        };
    }

    private static TimeSpan? DurationOf(KitContainer.Stream timing)
    {
        var seconds = TimingDocument.Parse(timing.Data).DurationSec;

        return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }
}
