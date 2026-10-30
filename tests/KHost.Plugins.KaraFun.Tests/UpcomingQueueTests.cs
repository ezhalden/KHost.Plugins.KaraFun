using KHost.Abstractions.Models;
using KHost.Plugins.KaraFun;
using KHost.Plugins.KaraFun.Client.Kcs;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>What the guest remote's queue list is told. Unlike the now-playing bar this needs
/// ViewQueue, and unlike the host's own panels it shows more than each singer's next song.</summary>
public class UpcomingQueueTests
{
    private static readonly Guid Ada = Guid.NewGuid();
    private static readonly Guid Bo = Guid.NewGuid();
    private static readonly Guid Cy = Guid.NewGuid();

    /// <summary>A finished singer drops to the back of the rotation, so a second song waits for
    /// everyone else's first. Ordering the queue any other way would tell a guest they are up
    /// sooner than they are.</summary>
    [Fact]
    public void SingersTakeTurns_RatherThanEachSingerDrainingTheirOwnList()
    {
        var queue = KaraFunMediaProvider.UpcomingQueue(
            [Singer(Ada), Singer(Bo)],
            [
                Queued(Ada, 1, "Ada one"),
                Queued(Ada, 2, "Ada two"),
                Queued(Bo, 1, "Bo one"),
                Queued(Bo, 2, "Bo two"),
            ],
            Library("Ada one", "Ada two", "Bo one", "Bo two"));

        Assert.Equal(
            ["Ada one", "Bo one", "Ada two", "Bo two"],
            queue.Items.Select(item => item.Song.Title));
    }

    /// <summary>QueuePosition counts within one singer, so two songs both numbered 1 are two
    /// singers' first songs and not two songs in a row.</summary>
    [Fact]
    public void QueuePosition_OrdersASingersOwnSongsAndNothingAcross()
    {
        var queue = KaraFunMediaProvider.UpcomingQueue(
            [Singer(Ada)],
            [Queued(Ada, 2, "second"), Queued(Ada, 1, "first")],
            Library("first", "second"));

        Assert.Equal(["first", "second"], queue.Items.Select(item => item.Song.Title));
    }

    /// <summary>The rotation decides the order, so a singer added later is heard later even when
    /// their song was queued first.</summary>
    [Fact]
    public void TheRotationOrdersTheLap_NotTheOrderSongsWereQueuedIn()
    {
        var queue = KaraFunMediaProvider.UpcomingQueue(
            [Singer(Cy), Singer(Ada)],
            [Queued(Ada, 1, "queued first"), Queued(Cy, 1, "queued second")],
            Library("queued first", "queued second"));

        Assert.Equal(["queued second", "queued first"], queue.Items.Select(item => item.Song.Title));
    }

    /// <summary>A singer who runs out drops out of the later laps rather than leaving a gap.</summary>
    [Fact]
    public void ASingerWithFewerSongs_StopsAppearingOnceTheirsRunOut()
    {
        var queue = KaraFunMediaProvider.UpcomingQueue(
            [Singer(Ada), Singer(Bo)],
            [Queued(Ada, 1, "Ada one"), Queued(Ada, 2, "Ada two"), Queued(Bo, 1, "Bo one")],
            Library("Ada one", "Ada two", "Bo one"));

        Assert.Equal(["Ada one", "Bo one", "Ada two"], queue.Items.Select(item => item.Song.Title));
    }

    /// <summary>The page converts a song identifier with no null guard and builds the whole array
    /// before assigning it, so one item without one discards the entire queue refresh rather than
    /// itself. Every KHost song is unidentified to KaraFun, making this the normal case.</summary>
    [Fact]
    public void EveryItem_CarriesAnIdentifier()
    {
        var queue = KaraFunMediaProvider.UpcomingQueue(
            [Singer(Ada), Singer(Bo)],
            [Queued(Ada, 1, "one"), Queued(Bo, 1, "two")],
            Library("one", "two"));

        Assert.NotEmpty(queue.Items);
        Assert.All(queue.Items, item =>
        {
            Assert.NotNull(item.Song.Id);
            Assert.Equal(PlayerSongIdentifier.Types.TypeEnum.Unknown, item.Song.Id.Type);
            Assert.Equal(0u, item.Song.Id.Id);
        });
    }

    /// <summary>A row whose media has gone still has to carry strings: the wire rejects a null,
    /// and dropping the row would renumber everybody behind it.</summary>
    [Fact]
    public void AMissingMediaRow_SendsEmptyStringsRatherThanBeingDropped()
    {
        var queue = KaraFunMediaProvider.UpcomingQueue(
            [Singer(Ada)],
            [Queued(Ada, 1, "gone")],
            new Dictionary<Guid, Media>());

        var item = Assert.Single(queue.Items);
        Assert.Equal(string.Empty, item.Song.Title);
        Assert.Equal(string.Empty, item.Song.Artist);
        Assert.NotNull(item.Song.Id);
    }

    /// <summary>The item names the turn, not the song: the same song queued by two singers is two
    /// rows, and the page keys its list on this.</summary>
    [Fact]
    public void AnItemNamesThePerformance_SoOneSongQueuedTwiceIsTwoRows()
    {
        var shared = Guid.NewGuid();
        var ada = new Performance { Id = Guid.NewGuid(), SingerId = Ada, MediaId = shared, QueuePosition = 1 };
        var bo = new Performance { Id = Guid.NewGuid(), SingerId = Bo, MediaId = shared, QueuePosition = 1 };

        var queue = KaraFunMediaProvider.UpcomingQueue(
            [Singer(Ada), Singer(Bo)],
            [ada, bo],
            new Dictionary<Guid, Media> { [shared] = new() { Id = shared, Title = "Wonderwall", Artist = "Oasis", FilePath = "/shared.mp4" } });

        Assert.Equal([ada.Id.ToString(), bo.Id.ToString()], queue.Items.Select(item => item.Id));
        Assert.All(queue.Items, item => Assert.Equal("Wonderwall", item.Song.Title));
    }

    [Fact]
    public void AnArtistlessSong_SendsAnEmptyStringRatherThanNull()
    {
        var mediaId = Guid.NewGuid();
        var queue = KaraFunMediaProvider.UpcomingQueue(
            [Singer(Ada)],
            [new Performance { Id = Guid.NewGuid(), SingerId = Ada, MediaId = mediaId, QueuePosition = 1 }],
            new Dictionary<Guid, Media> { [mediaId] = new() { Id = mediaId, Title = "Untitled", Artist = null!, FilePath = "/untitled.mp4" } });

        Assert.Equal(string.Empty, Assert.Single(queue.Items).Song.Artist);
    }

    [Fact]
    public void AnEmptyRoom_SendsAnEmptyQueueRatherThanThrowing()
        => Assert.Empty(KaraFunMediaProvider.UpcomingQueue([], [], new Dictionary<Guid, Media>()).Items);

    /// <summary>A singer on the rotation with nothing queued contributes no rows.</summary>
    [Fact]
    public void ASingerWithNothingQueued_AddsNothing()
        => Assert.Empty(KaraFunMediaProvider.UpcomingQueue(
            [Singer(Ada)], [], new Dictionary<Guid, Media>()).Items);

    private static KHostUser Singer(Guid id) => new() { Id = id, Name = id.ToString("n")[..4] };

    private static Performance Queued(Guid singer, int position, string title) => new()
    {
        Id = Guid.NewGuid(),
        SingerId = singer,
        MediaId = MediaIdFor(title),
        QueuePosition = position,
    };

    // Derived from the title so a test can name what it expects without threading ids through.
    private static Guid MediaIdFor(string title)
    {
        var bytes = new byte[16];
        System.Text.Encoding.UTF8.GetBytes(title).CopyTo(bytes.AsSpan(0, Math.Min(16, title.Length)));
        return new Guid(bytes);
    }

    private static Dictionary<Guid, Media> Library(params string[] titles)
        => titles.ToDictionary(MediaIdFor, title => new Media { Id = MediaIdFor(title), Title = title, Artist = "An Artist", FilePath = $"/{title}.mp4" });
}
