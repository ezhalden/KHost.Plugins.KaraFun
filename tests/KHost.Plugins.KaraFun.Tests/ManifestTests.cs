using KHost.Abstractions.Models.Plugins;
using System.Text.Json;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>The manifest is read by the host, not this assembly, so a setting the SDK cannot parse
/// costs nothing at build time and everything at load time.</summary>
public class ManifestTests
{
    private static readonly string ManifestPath = Path.Combine(AppContext.BaseDirectory, "manifest.json");

    [Fact]
    public void Manifest_ParsesTheWayTheHostParsesIt()
    {
        var manifest = Read();

        Assert.NotEqual(Guid.Empty, manifest.Id);
        Assert.Equal(PluginApi.CurrentVersion, manifest.ApiVersion);
        Assert.Equal(KHost.Abstractions.Models.Plugins.PluginApi.CurrentVersion, manifest.ApiVersion);
        Assert.Equal("KHost.Plugins.KaraFun.dll", manifest.EntryAssembly);
        Assert.NotEmpty(manifest.Settings);
    }

    [Fact]
    public void Manifest_EverySettingKeyBindsToAKaraFunSettingsProperty()
    {
        var properties = typeof(KaraFunSettings)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var setting in Read().Settings)
            Assert.True(properties.Contains(setting.Key), $"Manifest setting '{setting.Key}' binds to nothing.");
    }

    /// <summary>The login is prompted at sign-in and never stored (see KaraFunMediaProviderTests).</summary>
    [Fact]
    public void Manifest_DeclaresNoLoginOrPasswordSetting()
        => Assert.DoesNotContain(Read().Settings, s =>
            s.Key.Contains("login", StringComparison.OrdinalIgnoreCase)
            || s.Key.Contains("password", StringComparison.OrdinalIgnoreCase));

    /// <summary>The Plugins-page session button; its key is what KaraFunMediaProvider handles.</summary>
    [Fact]
    public void Manifest_DeclaresTheSessionButton()
        => Assert.Contains(Read().Buttons, b => b.Key == "session");

    /// <summary>A kit reaches the library only through the plugin's own download, which knows the
    /// song. The file carries no title or artist, just the catalog id in its name, so a folder scan
    /// could only name the row "11342" or ask the network once per file.</summary>
    /// <remarks>The render extension stays undeclared too: nothing produces one since the host
    /// dropped its render path.</remarks>
    [Fact]
    public void Manifest_DeclaresNoImportFormat()
    {
        Assert.DoesNotContain(".kit", Read().ImportFormats);
        Assert.DoesNotContain(".khv", Read().ImportFormats);
    }

    /// <summary>The host binds the manifest's default into the settings class, so the two must agree
    /// on a value nobody has touched.</summary>
    [Fact]
    public void Manifest_SingleUse_IsABoolOnByDefaultWithAnExplanation()
    {
        var setting = Assert.Single(Read().Settings, s => s.Key == "singleUse");

        Assert.Equal(PluginSettingType.Bool, setting.Type);
        Assert.True(setting.Default!.Value.GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(setting.Description));
        Assert.True(new KaraFunSettings().SingleUse);
    }

    private static PluginManifest Read()
        => JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(ManifestPath), JsonSerializerOptions.Web)!;
}
