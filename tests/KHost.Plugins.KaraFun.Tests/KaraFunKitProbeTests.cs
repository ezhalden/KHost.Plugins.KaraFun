using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.KaraFun.Kit;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>Describing a kit: ffprobe reads a raw kit as "Invalid data found", which is
/// indistinguishable from a file with nothing in it.</summary>
public class KaraFunKitProbeTests : IDisposable
{
    private readonly IPluginContext _plugin = Substitute.For<IPluginContext>();
    private readonly string _mediaDirectory = Path.Combine(Path.GetTempPath(), $"khost-karafun-probe-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_mediaDirectory))
            Directory.Delete(_mediaDirectory, recursive: true);
    }

    private KaraFunKitProbe Probe() => new(_plugin, NullLogger<KaraFunKitProbe>.Instance);

    [Fact]
    public async Task Probe_AKit_ReportsItsStemsAsNamedTracks()
    {
        var kit = await WriteKitAsync("6229.kit");

        var probe = await Probe().ProbeAsync(kit);

        Assert.NotNull(probe);
        Assert.Equal(
            [
                new AudioTrack(0, AudioTrackRole.Music, "Instrumental"),
                new AudioTrack(1, AudioTrackRole.Backing, "Backing Vocal"),
                new AudioTrack(2, AudioTrackRole.Lead, "Lead Vocal"),
            ],
            probe.AudioTracks);
    }

    /// <summary>The names a host sees on the faders.</summary>
    [Fact]
    public async Task Probe_AKit_NamesItsStems()
    {
        var kit = await WriteKitAsync("6229.kit");

        var probe = await Probe().ProbeAsync(kit);

        Assert.Equal(
            ["Instrumental", "Backing Vocal", "Lead Vocal"],
            probe!.AudioTracks.Select(track => track.Name));
    }

    /// <summary>Read off the timing document, so a kit scanned straight off disk has a length. It
    /// used to arrive only on a search result, which a folder scan never sees.</summary>
    [Fact]
    public async Task Probe_AKit_ReportsTheLengthFromItsTimingDocument()
    {
        var kit = await WriteKitAsync("6229.kit");

        var probe = await Probe().ProbeAsync(kit);

        Assert.Equal(TimeSpan.FromSeconds(243), probe!.Duration);
    }

    /// <summary>With a tag of its own, a kit answers the ownership question the same way every
    /// other gated file does, rather than needing the path-only fallback.</summary>
    [Fact]
    public async Task Probe_AKit_CarriesTheGatesOwnMarker()
    {
        var kit = await WriteKitAsync("6229.kit");

        var probe = await Probe().ProbeAsync(kit);

        Assert.Equal("KHost.Plugins.KaraFun", probe!.Tags[IMediaPlaybackGate.MetadataTag]);
    }

    /// <summary>The probe and the renderer read their stem order off the same helper, so a track's
    /// index names the stem the renderer hands over at it even when the kit's own chunks are not
    /// stored id-ascending — get this wrong and the singer's voice lands on the wrong fader.</summary>
    [Fact]
    public async Task Probe_OrdersTracksAsTheStemsAreHandedOver()
    {
        var kit = await WriteKitAsync("6229.kit", stemIds: [4, 2, 3]);

        var probe = await Probe().ProbeAsync(kit);

        var ordered = KitStems.Describe(KitContainer.Read(await File.ReadAllBytesAsync(kit)).Demux());

        // Ascending by id regardless of how the file stored them: 4 was written first.
        Assert.Equal([(byte)2, (byte)3, (byte)4], ordered.Select(stream => stream.Id));
        Assert.Equal(ordered.Select(stem => stem.Name), probe!.AudioTracks.Select(track => track.Name));
    }

    /// <summary>A kit with no backing vocal stores its lead on stream 3, which the id alone
    /// used to put on the backing fader.</summary>
    [Fact]
    public async Task Probe_AKitWithNoBackingVocal_PutsItsLeadOnTheLeadFader()
    {
        var kit = await WriteKitAsync("14238.kit", stemIds: [2, 3], timingXml: KitStemsTests.SharedLeadDuet);

        var probe = await Probe().ProbeAsync(kit);

        Assert.Equal(
            [
                new AudioTrack(0, AudioTrackRole.Music, "Instrumental"),
                new AudioTrack(1, AudioTrackRole.Lead, "Lead Vocal"),
            ],
            probe!.AudioTracks);
    }

    /// <summary>Both singers' leads are leads, each named for its singer and carrying that singer as
    /// its voice, so the host gives each a fader of its own.</summary>
    [Fact]
    public async Task Probe_ADuetWithAStemPerSinger_ReportsBothLeadsAsLead()
    {
        var kit = await WriteKitAsync("5485.kit", stemIds: [2, 3, 4, 5], timingXml: KitStemsTests.PerSingerDuet);

        var probe = await Probe().ProbeAsync(kit);

        Assert.Equal(
            [
                new AudioTrack(0, AudioTrackRole.Music, "Instrumental"),
                new AudioTrack(1, AudioTrackRole.Backing, "Backing Vocal"),
                new AudioTrack(2, AudioTrackRole.Lead, "Lead Vocal (♂)") { Voice = "♂" },
                new AudioTrack(3, AudioTrackRole.Lead, "Lead Vocal (♀)") { Voice = "♀" },
            ],
            probe!.AudioTracks);
    }

    /// <summary>A real duet kit's shape, words left out: pages ahead of <c>files</c>, one singer's
    /// caption bound by <c>file_index</c> and the other's uncaptioned and misspelled, colours on a
    /// file, art and a font among the files, and mods carrying kfntracks of their own.</summary>
    private const string RealDuetShape = """
        <?xml version="1.0" encoding="UTF-8"?>
        <root><karaoke version="1.0" producer="Recisio" duration="300.68378684807"><kfntracks><kfntrack id="8" caption="Kid Rock" file_index="5"/><kfntrack id="9" caption="Sherly Crow"/></kfntracks><silence step="00:00:01,000" count="4"><position x="60.00" width="520" y="282.50" height="35"/><start>00:00:01,000</start><end>00:00:10,280</end></silence><page id="1" activecolor="#0B96CA" kfntrackid="8"><line id="1"><word id="1"><syllabe><start>00:00:10,280</start><end>00:00:10,580</end><text>aa</text></syllabe></word></line></page><page id="2" activecolor="#F52C77" kfntrackid="9"><line id="2"><word id="1"><syllabe><start>00:00:14,410</start><end>00:00:14,900</end><text>bb</text></syllabe></word></line></page></karaoke><files><file index="1" label="xml">xml</file><file index="2" label="ins.ogg">ins.ogg</file><file index="3" label="bv.ogg">bv.ogg</file><file index="4" label="ld_Sheryl Crow.ogg">ld_Sheryl Crow.ogg</file><file index="5" label="ld_Kid Rock.ogg" inactive="#D7F2FDFF" active="#0B96CAFF">ld_Kid Rock.ogg</file><file index="6" label="COASBL__.TTF">COASBL__.TTF</file><file index="7" label="h05-1.jpg">h05-1.jpg</file></files><mods><mod id="1"><karaoke><kfntracks><kfntrack id="8" caption="Kid Rock" volume="0" visible="0" file_index="5"/><kfntrack id="9" caption="Sherly Crow" volume="100" visible="0"/></kfntracks></karaoke></mod></mods></root>
        """;

    /// <summary>Each singer of a duet gets a voice, so the host gives each a fader of their own
    /// rather than one shared "Lead Vocal".</summary>
    [Fact]
    public async Task Probe_ARealDuetKitsShape_GivesEachSingersLeadItsVoice()
    {
        var kit = await WriteKitAsync("7064.kit", stemIds: [2, 3, 4, 5], timingXml: RealDuetShape);

        var probe = await Probe().ProbeAsync(kit);

        Assert.Equal(
            [
                new AudioTrack(0, AudioTrackRole.Music, "Instrumental"),
                new AudioTrack(1, AudioTrackRole.Backing, "Backing Vocal"),
                new AudioTrack(2, AudioTrackRole.Lead, "Lead Vocal (Sheryl Crow)") { Voice = "Sheryl Crow" },
                new AudioTrack(3, AudioTrackRole.Lead, "Lead Vocal (Kid Rock)") { Voice = "Kid Rock" },
            ],
            probe!.AudioTracks);
    }

    [Theory]
    [InlineData("/library/karafun/6229.kit")]
    [InlineData("/library/karafun/6229.KIT")]
    public void Probe_ClaimsAKit(string path) => Assert.True(Probe().CanProbe(path));

    [Theory]
    [MemberData(nameof(KitClaims.NotAKit), MemberType = typeof(KitClaims))]
    public void Probe_ClaimsNothingButAKit(string path) => Assert.False(Probe().CanProbe(path));

    /// <summary>A file that is not a kit at all must read as "could not tell", not as a kit with
    /// nothing in it: the host would take an empty answer as fact and stop asking.</summary>
    [Fact]
    public async Task Probe_SomethingThatIsNotAKit_AnswersNull()
    {
        Directory.CreateDirectory(_mediaDirectory);
        var path = Path.Combine(_mediaDirectory, "not-a.kit");
        await File.WriteAllTextAsync(path, "this is not a kit");

        Assert.Null(await Probe().ProbeAsync(path));
    }

    /// <summary>Writes a kit carrying a timing document and the three stems, in the shape
    /// <see cref="KitContainer.Read"/> reads: a 9-byte chunk header before each member's bytes.
    /// </summary>
    /// <param name="stemIds">The order the stems are written to the file in. Defaults to already
    /// ascending; a test proving the probe and the renderer agree on ordering wants these scrambled.
    /// </param>
    /// <param name="timingXml">The timing document to carry instead of the bare default, for a test
    /// that needs the kit's <c>files</c> labels.</param>
    private async Task<string> WriteKitAsync(string name, byte[]? stemIds = null, string? timingXml = null)
    {
        Directory.CreateDirectory(_mediaDirectory);
        var path = Path.Combine(_mediaDirectory, name);
        await File.WriteAllBytesAsync(path, KitBytes(stemIds, timingXml));
        return path;
    }

    private static byte[] KitBytes(byte[]? stemIds = null, string? timingXml = null)
    {
        // Sniff() reads the leading bytes, so the timing document has to open with "<?x" and each
        // stem with "OggS" or they are demuxed as "bin" and the probe finds nothing.
        // The length sits on a <karaoke> child, not on the root, which is the shape a real kit has.
        var timing = System.Text.Encoding.UTF8.GetBytes(
            timingXml ?? """<?xml version="1.0"?><kit><karaoke duration="243"/></kit>""");

        var file = new List<byte>();
        file.AddRange(KitContainer.Magic);
        file.Add(0); // no index entries, so the reader starts at the first chunk

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
        file.AddRange(new byte[4]); // the rest of the 9-byte header the reader skips
        file.AddRange(data);
    }
}
