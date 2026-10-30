using KHost.Abstractions.Models;
using KHost.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>Which extension type owns which interface, and that every one of them is buildable
/// the way the host's plugin loader builds an extension type: against the container, with the
/// plugin's context as a given argument.</summary>
public class KaraFunExtensionTypesTests
{
    private readonly IPluginContext _plugin = Substitute.For<IPluginContext>();

    // --- which extension type owns what ---

    /// <summary>Pinned both ways, so an interface drifting onto the wrong extension type fails
    /// here: the host binds each extension type as its own singleton, and a format member on the
    /// service type would be a second, session-holding answer to the same question.</summary>
    [Fact]
    public void Probe_ImplementsExactlyIMediaProbe()
        => Assert.Equal([typeof(IMediaProbe)], ExtensionInterfaces(typeof(KaraFunKitProbe)));

    [Fact]
    public void TimedLyricsProvider_ImplementsExactlyITimedLyricsProvider()
        => Assert.Equal([typeof(ITimedLyricsProvider)], ExtensionInterfaces(typeof(KaraFunTimedLyricsProvider)));

    [Fact]
    public void Renderer_ImplementsExactlyIMediaRenderer()
        => Assert.Equal([typeof(IMediaRenderer)], ExtensionInterfaces(typeof(KaraFunKitRenderer)));

    [Fact]
    public void Provider_ImplementsExactlyTheServiceInterfaces()
        => Assert.Equal(
            [typeof(IMediaPlaybackGate), typeof(IMediaProvider), typeof(IMediaRefetcher), typeof(IPluginButtonHandler)],
            ExtensionInterfaces(typeof(KaraFunMediaProvider)));

    /// <summary>The whole set, so a fifth extension type — or one of these going missing — fails
    /// here rather than binding silently at load.</summary>
    [Fact]
    public void TheExtensionTypes_AreExactlyTheseFour()
        => Assert.Equal(
            [typeof(KaraFunKitProbe), typeof(KaraFunKitRenderer), typeof(KaraFunMediaProvider), typeof(KaraFunTimedLyricsProvider)],
            typeof(KaraFunKitProbe).Assembly.GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } && ExtensionInterfaces(type).Length > 0)
                .OrderBy(type => type.Name, StringComparer.Ordinal));

    /// <summary>A kit reaches the host as stems, which the host encodes itself where a display needs
    /// one stream; nothing here turns a kit into a file for the host's encoder to open.</summary>
    [Fact]
    public void NoType_ImplementsIPlayableMediaSource()
        => Assert.DoesNotContain(
            typeof(KaraFunKitProbe).Assembly.GetTypes(),
            type => typeof(IPlayableMediaSource).IsAssignableFrom(type));

    /// <summary>The host binds the settings class the entry point names here; without it the options
    /// the provider injects would hold defaults, never the saved values.</summary>
    [Fact]
    public void Plugin_NamesItsSettingsClass()
        => Assert.Contains(typeof(IPlugin<KaraFunSettings>), typeof(KaraFunPlugin).GetInterfaces());

    // The entry point is not an extension type, in either its plain or settings-naming form.
    private static bool IsEntryPoint(Type i)
        => i == typeof(IPlugin) || (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPlugin<>));

    private static Type[] ExtensionInterfaces(Type type)
        => [.. type.GetInterfaces()
            .Where(i => i.Namespace == typeof(IMediaProbe).Namespace && !IsEntryPoint(i))
            .OrderBy(i => i.Name, StringComparer.Ordinal)];

    // --- built the way the plugin loader builds them ---

    /// <summary>What the host's loader does with an extension type: build it against the container
    /// with the plugin's context as a given argument. A constructor with nowhere to put that
    /// argument is refused, which would drop every format interface at load with no error here.</summary>
    [Fact]
    public void Probe_IsBuiltTheWayThePluginLoaderBuildsIt()
    {
        var services = ServicesAnsweringOwnLogger<KaraFunKitProbe>();

        var built = ActivatorUtilities.CreateInstance(services, typeof(KaraFunKitProbe), _plugin);

        Assert.IsType<KaraFunKitProbe>(built);
    }

    [Fact]
    public void TimedLyricsProvider_IsBuiltTheWayThePluginLoaderBuildsIt()
    {
        var services = ServicesAnsweringOwnLogger<KaraFunTimedLyricsProvider>();

        var built = ActivatorUtilities.CreateInstance(services, typeof(KaraFunTimedLyricsProvider), _plugin);

        Assert.IsType<KaraFunTimedLyricsProvider>(built);
    }

    [Fact]
    public void Renderer_IsBuiltTheWayThePluginLoaderBuildsIt()
    {
        var services = ServicesAnsweringOwnLogger<KaraFunKitRenderer>();
        services.GetService(typeof(IServiceProvider)).Returns(services);

        var built = ActivatorUtilities.CreateInstance(services, typeof(KaraFunKitRenderer), _plugin);

        Assert.IsType<KaraFunKitRenderer>(built);
    }

    /// <summary>Answers as a container does: the logger a constructor asks for, resolved by its
    /// own generic argument.</summary>
    private static IServiceProvider ServicesAnsweringOwnLogger<T>()
    {
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(Microsoft.Extensions.Logging.ILogger<T>))
            .Returns(NullLogger<T>.Instance);
        return services;
    }
}
