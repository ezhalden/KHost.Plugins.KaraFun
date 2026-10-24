using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.KaraFun.Kit;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>The plugin's renderer: the stems every display is handed, and the level each one
/// rides at.</summary>
public class KaraFunKitRendererTests : IDisposable
{
    private readonly IPluginContext _plugin = Substitute.For<IPluginContext>();
    private readonly IServiceProvider _services = Substitute.For<IServiceProvider>();
    private readonly string _mediaDirectory = Path.Combine(Path.GetTempPath(), $"khost-karafun-renderer-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_mediaDirectory))
            Directory.Delete(_mediaDirectory, recursive: true);
    }

    private KaraFunKitRenderer Renderer() => new(_plugin, _services);

    /// <summary>The stems come back in the one order that matters — ascending by chunk id, which
    /// is the order the roles are assigned in. Written scrambled so a file that happens to store
    /// them in order cannot make this pass.</summary>
    [Fact]
    public async Task RenderAsync_HandsBackTheStems_InIdOrder_WithTheLevelEachRidesAt()
    {
        var kit = await WriteKitAsync("6229.kit", stemIds: [4, 2, 3]);
        var directory = Directory.CreateTempSubdirectory("khost-render").FullName;

        try
        {
            var streams = Substitute.For<IMediaStreamService>();
            streams.OpenWithoutEncodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new MediaStreamSession
                {
                    Id = "session-1",
                    SourcePath = kit,
                    PlaylistUrl = null,
                    WorkingDirectory = directory,
                    StartOffset = TimeSpan.Zero,
                    Pitch = 0,
                    Tempo = 0,
                });
            streams.BuildArtifactUrl(Arg.Any<string>(), Arg.Any<string>())
                .Returns(call => $"http://host/media/{call.ArgAt<string>(0)}/{call.ArgAt<string>(1)}");
            _services.GetService(typeof(IMediaStreamService)).Returns(streams);

            var rendition = await Renderer().RenderAsync(new MediaRenderRequest
            {
                FilePath = kit,
                Mix = new AudioMix([], LeadVolume: 40, BackingVolume: 75),
                Target = new RenderTarget { MixesStems = true },
            });

            Assert.NotNull(rendition);

            // Nothing to encode, and the whole song in every part, so a seek is free.
            Assert.Null(rendition.Url);
            Assert.True(rendition.SeekableInPlace);

            Assert.Equal(
                [AudioTrackRole.Music, AudioTrackRole.Backing, AudioTrackRole.Lead],
                rendition.Stems.Select(stem => stem.Role));

            // The music is the reference and rides at full whatever the console shows.
            Assert.Equal(AudioMix.MaxVolume, rendition.Stems.Single(s => s.Role == AudioTrackRole.Music).Volume);
            Assert.Equal(40, rendition.Stems.Single(s => s.Role == AudioTrackRole.Lead).Volume);
            Assert.Equal(75, rendition.Stems.Single(s => s.Role == AudioTrackRole.Backing).Volume);

            // Written where the session will sweep them, and named by the URL the display fetches.
            Assert.Equal("http://host/media/session-1/stem2.ogg", rendition.Stems[0].Url);
            Assert.True(File.Exists(Path.Combine(directory, "stem2.ogg")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>Stems for every target, key and tempo: whether a display takes them as they are or
    /// needs one stream is the host's call, and the host encodes that stream from these.</summary>
    [Theory]
    [InlineData(false, false, 0, 0)]
    [InlineData(true, true, 0, 0)]
    [InlineData(false, true, 0, 0)]
    [InlineData(true, false, 2, 0)]
    [InlineData(true, false, 0, -30)]
    [InlineData(false, false, -3, 15)]
    public async Task RenderAsync_HandsBackTheStems_WhateverTheTargetKeyOrTempo(
        bool mixes, bool burnLyrics, int pitch, int tempo)
    {
        var kit = await WriteKitAsync("6229.kit");
        var directory = Directory.CreateTempSubdirectory("khost-render").FullName;

        try
        {
            ArrangeRenderableHost(directory);

            var rendition = await Renderer().RenderAsync(new MediaRenderRequest
            {
                FilePath = kit,
                Pitch = pitch,
                Tempo = tempo,
                Mix = new AudioMix([], LeadVolume: 40, BackingVolume: 75),
                Target = new RenderTarget { MixesStems = mixes, BurnLyrics = burnLyrics },
            });

            Assert.NotNull(rendition);
            Assert.Null(rendition.Url);
            Assert.Equal(
                [(AudioTrackRole.Music, 100), (AudioTrackRole.Backing, 75), (AudioTrackRole.Lead, 40)],
                rendition.Stems.Select(stem => (stem.Role, stem.Volume)));
            Assert.Equal(["stem2.ogg", "stem3.ogg", "stem4.ogg"], Directory.GetFiles(directory).Select(Path.GetFileName).Order());
            Assert.Equal("session-1", rendition.Session!.Id);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CanRender_AKit() => Assert.True(Renderer().CanRender("/library/karafun/6229.kit"));

    [Theory]
    [MemberData(nameof(KitClaims.NotAKit), MemberType = typeof(KitClaims))]
    public void CanRender_NothingButAKit(string path) => Assert.False(Renderer().CanRender(path));

    /// <summary>Gives the renderer a stream service that works, so a null answer can only be the
    /// renderer's own decision and not a missing host service.</summary>
    private void ArrangeRenderableHost(string directory)
    {
        var streams = Substitute.For<IMediaStreamService>();
        streams.OpenWithoutEncodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MediaStreamSession
            {
                Id = "session-1",
                SourcePath = "kit",
                PlaylistUrl = null,
                WorkingDirectory = directory,
                StartOffset = TimeSpan.Zero,
                Pitch = 0,
                Tempo = 0,
            });
        streams.BuildArtifactUrl(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => $"http://host/media/{call.ArgAt<string>(0)}/{call.ArgAt<string>(1)}");
        _services.GetService(typeof(IMediaStreamService)).Returns(streams);
    }

    /// <summary>A file that is not a kit fails with a cause, which the host turns into a song that
    /// says why it cannot play rather than one that plays something wrong.</summary>
    /// <remarks>Read before a session is opened, so a failure leaves no directory behind.</remarks>
    [Fact]
    public async Task Render_AFileThatIsNotAKit_ThrowsWithoutOpeningASession()
    {
        Directory.CreateDirectory(_mediaDirectory);
        var kit = Path.Combine(_mediaDirectory, "broken.kit");
        await File.WriteAllTextAsync(kit, "this is not a kit container");
        var streams = Substitute.For<IMediaStreamService>();
        _services.GetService(typeof(IMediaStreamService)).Returns(streams);

        await Assert.ThrowsAnyAsync<Exception>(() => Renderer().RenderAsync(new MediaRenderRequest
        {
            FilePath = kit,
            Target = new RenderTarget { MixesStems = true },
        }));

        await streams.DidNotReceiveWithAnyArgs().OpenWithoutEncodeAsync(default!, default);
    }

    /// <summary>A kit the library lost between queueing and playing.</summary>
    [Fact]
    public async Task Render_AMissingKit_ThrowsWithoutOpeningASession()
    {
        Directory.CreateDirectory(_mediaDirectory);
        var streams = Substitute.For<IMediaStreamService>();
        _services.GetService(typeof(IMediaStreamService)).Returns(streams);

        await Assert.ThrowsAsync<FileNotFoundException>(() => Renderer().RenderAsync(new MediaRenderRequest
        {
            FilePath = Path.Combine(_mediaDirectory, "gone.kit"),
            Target = new RenderTarget { MixesStems = true },
        }));

        await streams.DidNotReceiveWithAnyArgs().OpenWithoutEncodeAsync(default!, default);
    }

    [Fact]
    public async Task Render_ADuet_HandsEachSingersLeadOverAtThatSingersLevel()
    {
        var kit = await WriteKitAsync("5485.kit", stemIds: [2, 3, 4, 5], timingXml: KitStemsTests.PerSingerDuet);
        var directory = Directory.CreateTempSubdirectory("khost-render").FullName;

        try
        {
            var streams = Substitute.For<IMediaStreamService>();
            streams.OpenWithoutEncodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new MediaStreamSession
                {
                    Id = "session-1",
                    SourcePath = kit,
                    PlaylistUrl = null,
                    WorkingDirectory = directory,
                    StartOffset = TimeSpan.Zero,
                    Pitch = 0,
                    Tempo = 0,
                });
            streams.BuildArtifactUrl(Arg.Any<string>(), Arg.Any<string>())
                .Returns(call => $"http://host/media/{call.ArgAt<string>(0)}/{call.ArgAt<string>(1)}");
            _services.GetService(typeof(IMediaStreamService)).Returns(streams);

            var rendition = await Renderer().RenderAsync(new MediaRenderRequest
            {
                FilePath = kit,
                Mix = new AudioMix([], LeadVolume: 5, BackingVolume: 100)
                {
                    VoiceVolumes = new Dictionary<string, int> { ["♂"] = 60, ["♀"] = 20 },
                },
                Target = new RenderTarget { MixesStems = true },
            });

            Assert.Equal(
                [
                    (AudioTrackRole.Music, (string?)null, AudioMix.MaxVolume),
                    (AudioTrackRole.Backing, null, 100),
                    (AudioTrackRole.Lead, "♂", 60),
                    (AudioTrackRole.Lead, "♀", 20),
                ],
                rendition!.Stems.Select(stem => (stem.Role, stem.Voice, stem.Volume)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static IReadOnlyList<KitStems.Stem> DuetStems() => KitStems.Describe(
    [
        new KitContainer.Stream(1, "xml", System.Text.Encoding.UTF8.GetBytes(KitStemsTests.PerSingerDuet.TrimStart())),
        .. ((byte[])[2, 3, 4, 5]).Select(id => new KitContainer.Stream(id, "ogg", [id])),
    ]);

    [Fact]
    public void LevelFor_GivesEachSingersLeadThatSingersLevel()
    {
        var levels = Levels(DuetStems(), new AudioMix([], LeadVolume: 5, BackingVolume: 90)
        {
            VoiceVolumes = new Dictionary<string, int> { ["♀"] = 35 },
        });

        // ♂ has no level of its own, so it rides the lead's; the music stays the reference.
        Assert.Equal(new Dictionary<byte, int> { [2] = 100, [3] = 90, [4] = 5, [5] = 35 }, levels);
    }

    [Fact]
    public void LevelFor_WithNoMix_StartsAsKaraFunDoes()
    {
        // Backing at full, every lead out of the way.
        Assert.Equal(new Dictionary<byte, int> { [2] = 100, [3] = 100, [4] = 0, [5] = 0 },
            Levels(DuetStems(), null));
    }

    /// <summary>The stems a mixing display is handed carry the same roles the probe offered, so
    /// the lead of a kit with no backing vocal rides the lead level.</summary>
    [Fact]
    public async Task Render_AKitWithNoBackingVocal_HandsItsLeadOverAtTheLeadLevel()
    {
        var kit = await WriteKitAsync("14238.kit", stemIds: [2, 3], timingXml: KitStemsTests.SharedLeadDuet);
        var directory = Directory.CreateTempSubdirectory("khost-render").FullName;

        try
        {
            var streams = Substitute.For<IMediaStreamService>();
            streams.OpenWithoutEncodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new MediaStreamSession
                {
                    Id = "session-1",
                    SourcePath = kit,
                    PlaylistUrl = null,
                    WorkingDirectory = directory,
                    StartOffset = TimeSpan.Zero,
                    Pitch = 0,
                    Tempo = 0,
                });
            streams.BuildArtifactUrl(Arg.Any<string>(), Arg.Any<string>())
                .Returns(call => $"http://host/media/{call.ArgAt<string>(0)}/{call.ArgAt<string>(1)}");
            _services.GetService(typeof(IMediaStreamService)).Returns(streams);

            var rendition = await Renderer().RenderAsync(new MediaRenderRequest
            {
                FilePath = kit,
                Mix = new AudioMix([], LeadVolume: 40, BackingVolume: 75),
                Target = new RenderTarget { MixesStems = true },
            });

            Assert.Equal(
                [(AudioTrackRole.Music, AudioMix.MaxVolume), (AudioTrackRole.Lead, 40)],
                rendition!.Stems.Select(stem => (stem.Role, stem.Volume)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>Writes a kit carrying a timing document and the three stems, in the shape
    /// <see cref="KitContainer.Read"/> reads: a 9-byte chunk header before each member's bytes.
    /// </summary>
    private async Task<string> WriteKitAsync(string name, byte[]? stemIds = null, string? timingXml = null)
    {
        Directory.CreateDirectory(_mediaDirectory);
        var path = Path.Combine(_mediaDirectory, name);
        await File.WriteAllBytesAsync(path, KitBytes(stemIds, timingXml));
        return path;
    }

    private static byte[] KitBytes(byte[]? stemIds = null, string? timingXml = null)
    {
        var timing = System.Text.Encoding.UTF8.GetBytes(
            timingXml ?? """<?xml version="1.0"?><kit><karaoke duration="243"/></kit>""");

        var file = new List<byte>();
        file.AddRange(KitContainer.Magic);
        file.Add(0);

        AppendChunk(file, 1, timing);
        foreach (byte id in stemIds ?? [2, 3, 4])
            AppendChunk(file, id, [.. "OggS"u8.ToArray(), .. new byte[8]]);

        return [.. file];
    }

    private static void AppendChunk(List<byte> file, byte id, byte[] data)
    {
        file.Add(id);
        file.AddRange(
        [
            (byte)(data.Length >> 24), (byte)(data.Length >> 16),
            (byte)(data.Length >> 8), (byte)data.Length,
        ]);
        file.AddRange(new byte[4]);
        file.AddRange(data);
    }

    private static Dictionary<byte, int> Levels(IReadOnlyList<KitStems.Stem> stems, AudioMix? mix)
        => stems.ToDictionary(stem => stem.Id, stem => KaraFunKitRenderer.LevelFor(stem, mix));
}
