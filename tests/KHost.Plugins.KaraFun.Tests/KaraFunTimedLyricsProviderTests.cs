using KHost.Abstractions.Services;
using KHost.Plugins.KaraFun.Kit;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>Reading a kit's timing for a screen to draw itself, without going anywhere near the
/// KaraFun session.</summary>
public class KaraFunTimedLyricsProviderTests : IDisposable
{
    private readonly IPluginContext _plugin = Substitute.For<IPluginContext>();
    private readonly string _mediaDirectory = Path.Combine(Path.GetTempPath(), $"khost-karafun-lyrics-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_mediaDirectory))
            Directory.Delete(_mediaDirectory, recursive: true);
    }

    private KaraFunTimedLyricsProvider Provider() => new(_plugin, NullLogger<KaraFunTimedLyricsProvider>.Instance);

    [Theory]
    [InlineData("/library/karafun/6229.kit")]
    [InlineData("/library/karafun/6229.KIT")]
    public void CanProvide_ClaimsAKit(string path) => Assert.True(Provider().CanProvide(path));

    /// <summary>The kit is the only file this plugin reads, so it is the only one it times.</summary>
    [Theory]
    [MemberData(nameof(KitClaims.NotAKit), MemberType = typeof(KitClaims))]
    public void CanProvide_ClaimsNothingButAKit(string path) => Assert.False(Provider().CanProvide(path));

    /// <summary>A kit's own id-1 stream is the only source a raw kit has for its timing, so one
    /// without it must answer null rather than a document with nothing in it.</summary>
    [Fact]
    public async Task GetTimedLyricsAsync_AKitWithNoTimingStream_AnswersNull()
    {
        Directory.CreateDirectory(_mediaDirectory);
        var path = Path.Combine(_mediaDirectory, "6229.kit");
        var file = new List<byte>();
        file.AddRange(KitContainer.Magic);
        file.Add(0);
        foreach (byte id in (byte[])[2, 3, 4])
            AppendChunk(file, id, [.. "OggS"u8.ToArray(), .. new byte[8]]);
        await File.WriteAllBytesAsync(path, [.. file]);

        Assert.Null(await Provider().GetTimedLyricsAsync(path));
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
}
