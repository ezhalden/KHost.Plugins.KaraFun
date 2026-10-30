using System.Net.Sockets;

namespace KHost.Plugins.KaraFun;

/// <summary>The one line a host reads when KaraFun fails. The wording matches the host's own, since
/// the host's helper is not in the published contracts.</summary>
internal static class KaraFunFailure
{
    internal const string Unreachable = "Could not reach KaraFun. Check this computer is online, then try again.";

    /// <param name="caller">The caller's own token: a cancel it asked for is not a network failure.</param>
    internal static bool IsNetwork(Exception ex, CancellationToken caller = default)
    {
        // Walked, not tested at the top: HttpClient and the stream layers wrap the socket error.
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case SocketException:
                case TimeoutException:
                case HttpRequestException { StatusCode: null }:
                    return true;

                // HttpClient reports its own timeout as a cancel nobody asked for.
                case TaskCanceledException when !caller.IsCancellationRequested:
                    return true;
            }
        }

        return false;
    }

    internal static string Describe(string what, Exception ex, CancellationToken caller = default)
        => IsNetwork(ex, caller) ? Unreachable : $"KaraFun {what} failed: {ex.Message}";
}
