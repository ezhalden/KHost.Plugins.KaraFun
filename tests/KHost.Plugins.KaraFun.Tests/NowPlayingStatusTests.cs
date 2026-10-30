using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using KHost.Plugins.KaraFun;
using KHost.Plugins.KaraFun.Client.Kcs;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>What the guest remote's now-playing bar is told. The bar is gated by no permission, so
/// this is reachable without ViewQueue and without showing a queue at all.</summary>
public class NowPlayingStatusTests
{
    private readonly IPlaybackService _playback = Substitute.For<IPlaybackService>();

    /// <summary>The page converts a song identifier with no null guard, and builds its whole list
    /// before assigning it, so an item without one throws and discards the entire update. Every
    /// song KHost plays is local as far as KaraFun is concerned, making this the normal case.
    /// </summary>
    [Fact]
    public void ACurrentSong_AlwaysCarriesAnIdentifier()
    {
        Playing(new Media { Title = "Today", Artist = "The Smashing Pumpkins", FilePath = "/x.mp4" });

        var id = KaraFunMediaProvider.NowPlaying(_playback).Current.Song.Id;

        Assert.NotNull(id);
        Assert.Equal(PlayerSongIdentifier.Types.TypeEnum.Unknown, id.Type);
        Assert.Equal(0u, id.Id);
    }

    [Fact]
    public void ACurrentSong_CarriesTheTitleAndArtistTheBarDraws()
    {
        Playing(new Media { Title = "Today", Artist = "The Smashing Pumpkins", FilePath = "/x.mp4" });

        var song = KaraFunMediaProvider.NowPlaying(_playback).Current.Song;

        Assert.Equal("Today", song.Title);
        Assert.Equal("The Smashing Pumpkins", song.Artist);
    }

    /// <summary>An empty artist is a real library row, and a null would throw on the wire.</summary>
    [Fact]
    public void ASongWithNoArtist_SendsAnEmptyStringRatherThanNull()
    {
        Playing(new Media { Title = "Untitled", Artist = null!, FilePath = "/x.mp4" });

        Assert.Equal(string.Empty, KaraFunMediaProvider.NowPlaying(_playback).Current.Song.Artist);
    }

    [Theory]
    [InlineData(PlaybackState.Playing, PlayerStatus.Types.State.Playing)]
    [InlineData(PlaybackState.Paused, PlayerStatus.Types.State.Paused)]
    public void PlayingAndPaused_AreTheOnlyStatesThatDrawAnActiveBar(PlaybackState state, PlayerStatus.Types.State expected)
    {
        Playing(new Media { Title = "Today", Artist = "x", FilePath = "/x.mp4" }, state);

        Assert.Equal(expected, KaraFunMediaProvider.NowPlaying(_playback).State);
    }

    /// <summary>The page folds everything else into idle, so reporting anything other than idle for
    /// a host that is not playing only risks it reading as something it is not.</summary>
    [Theory]
    [InlineData(PlaybackState.Stopped)]
    [InlineData(PlaybackState.Stopping)]
    public void EveryOtherState_IsReportedAsIdle(PlaybackState state)
    {
        Playing(new Media { Title = "Today", Artist = "x", FilePath = "/x.mp4" }, state);

        Assert.Equal(PlayerStatus.Types.State.Idle, KaraFunMediaProvider.NowPlaying(_playback).State);
    }

    /// <summary>"Is there a current item" is the page's whole test for drawing the bar, so nothing
    /// loaded has to leave it unset rather than send an empty one.</summary>
    [Fact]
    public void NothingLoaded_SendsNoCurrentItem()
    {
        _playback.State.Returns(PlaybackState.Stopped);
        _playback.CurrentMedia.Returns((Media?)null);

        Assert.Null(KaraFunMediaProvider.NowPlaying(_playback).Current);
    }

    /// <summary>The item id is the turn, not the song: two singers queuing the same track are two
    /// rows, and a reorder names this id.</summary>
    [Fact]
    public void TheItemId_NamesThePerformanceRatherThanTheMedia()
    {
        var performance = new Performance { Id = Guid.NewGuid(), SingerId = Guid.NewGuid(), MediaId = Guid.NewGuid() };
        Playing(new Media { Id = performance.MediaId, Title = "Today", Artist = "x", FilePath = "/x.mp4" });
        _playback.CurrentPerformance.Returns(performance);

        Assert.Equal(performance.Id.ToString(), KaraFunMediaProvider.NowPlaying(_playback).Current.Id);
    }

    private void Playing(Media media, PlaybackState state = PlaybackState.Playing)
    {
        _playback.State.Returns(state);
        _playback.CurrentMedia.Returns(media);
    }
}
