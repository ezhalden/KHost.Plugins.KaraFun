namespace KHost.Plugins.KaraFun.Tests;

/// <summary>Paths every claim in this plugin must refuse: the gate, the probe, the timing and the
/// renderer all read the kit and nothing else.</summary>
/// <remarks>Sweeps every <c>.kf?</c> neighbour rather than naming a few, so a sibling format a
/// claim once took cannot creep back in under a letter nobody thought to list.</remarks>
public static class KitClaims
{
    public static TheoryData<string> NotAKit
    {
        get
        {
            var paths = new TheoryData<string>();

            foreach (var letter in "abcdefghijklmnopqrstuvwxyz")
            {
                paths.Add($"/library/karafun/6229.kf{letter}");
                paths.Add($"/library/karafun/6229.KF{char.ToUpperInvariant(letter)}");
            }

            foreach (var other in new[] { "/library/karafun/6229.khv", "/music/song.mp4", "/music/song.ogg", "/library/karafun/6229.kit.part", "/library/karafun/6229" })
                paths.Add(other);

            return paths;
        }
    }
}
