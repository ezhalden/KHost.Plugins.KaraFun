using KHost.Plugins.KaraFun.Client;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>Against a worked example verified end-to-end against the live API.</summary>
public class PasswordServiceTests
{
    [Fact]
    public void Substitute_KnownPassword_MatchesTheVerifiedWorkedExample()
        => Assert.Equal("qXYqrfOT", PasswordService.Substitute("test1234"));

    [Fact]
    public void Hash_KnownPassword_MatchesTheVerifiedWorkedExample()
        => Assert.Equal("bf95cad846d3580da1b96e66f09cb1de", PasswordService.Hash("test1234"));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("!@#$%^&*()")]
    public void Substitute_NonAlphanumeric_PassesThroughUnchanged(string value)
        => Assert.Equal(value, PasswordService.Substitute(value));
}
