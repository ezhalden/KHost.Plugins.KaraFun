using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Common.Media;
using KHost.Plugins.KaraFun.Kit;
using Microsoft.Extensions.DependencyInjection;

namespace KHost.Plugins.KaraFun;

/// <summary>The plugin's renderer: hands the host the kit's stems, for every display. Needs only the
/// file, never the KaraFun session, which lives on <see cref="KaraFunMediaProvider"/>.</summary>
/// <remarks>Its own extension type, so the host binds it as its own singleton; nothing here is
/// started in the constructor.
///
/// <para>Supplies audio and makes no mixing decision. Whether a display takes the stems as they are
/// or needs one stream — keyed, retimed, or with the words burned in from the timing
/// <see cref="KaraFunTimedLyricsProvider"/> hands over — is the host's call, and the host encodes
/// that stream from these same stems.</para></remarks>
public class KaraFunKitRenderer : IMediaRenderer
{
    // Resolved on use: the host's stream service reaches every renderer, this one included, so
    // taking it in the constructor closes a ring the container cannot build.
    private readonly IServiceProvider _services;

    /// <param name="plugin">Unused, and still required: the host builds every extension type with
    /// the plugin's context as a given argument, and ActivatorUtilities refuses a constructor
    /// that has nowhere to put one.</param>
    public KaraFunKitRenderer(IPluginContext plugin, IServiceProvider services)
    {
        _services = services;
    }

    public bool CanRender(string filePath) => KitFiles.IsAKit(filePath);

    /// <summary>The kit's stems, whatever the target, key or tempo asked for.</summary>
    /// <remarks>Never null: a kit is nothing the host's encoder can open, so declining would only
    /// hand it a file it rejects. <see cref="KitStems.Describe"/> is the one order, and the level each
    /// stem rides at is the mix the host asked for.</remarks>
    public async Task<MediaRendition?> RenderAsync(
        MediaRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        var streams = _services.GetService<IMediaStreamService>()
            ?? throw new InvalidOperationException("The host offers no stream service to write the stems into.");

        var kit = KitContainer.Read(await File.ReadAllBytesAsync(request.FilePath, cancellationToken)).Demux();
        var oggs = KitStems.Describe(kit);

        if (oggs.Count == 0)
            throw new InvalidOperationException($"'{request.FilePath}' carries no audio stems.");

        var session = await streams.OpenWithoutEncodeAsync(request.FilePath, cancellationToken);

        if (session.WorkingDirectory is not { Length: > 0 } directory)
            throw new InvalidOperationException("The host opened a session with nowhere to write the stems.");

        var stems = new List<StemSource>(oggs.Count);

        for (var index = 0; index < oggs.Count; index++)
        {
            var stream = oggs[index];
            var name = $"stem{stream.Id}.ogg";

            await File.WriteAllBytesAsync(Path.Combine(directory, name), stream.Data, cancellationToken);

            stems.Add(new StemSource(index, stream.Role, streams.BuildArtifactUrl(session.Id, name), LevelFor(stream, request.Mix))
            {
                Voice = stream.Voice,
            });
        }

        return new MediaRendition
        {
            Stems = stems,

            // The whole song, every stem, so a seek is arithmetic rather than a new encode.
            SeekableInPlace = true,
            StartOffset = TimeSpan.Zero,
            Session = session,
        };
    }

    /// <summary>The music is the reference the voices are set against and rides at full; a
    /// singer's lead rides at that singer's level when the host sent one.</summary>
    internal static int LevelFor(KitStems.Stem stem, AudioMix? mix) => mix is not null
        ? mix.VolumeFor(stem.Role, stem.Voice)
        : stem.Role switch
        {
            AudioTrackRole.Lead => AudioMix.DefaultLeadVolume,
            AudioTrackRole.Backing => AudioMix.DefaultBackingVolume,
            _ => AudioMix.MaxVolume,
        };
}
