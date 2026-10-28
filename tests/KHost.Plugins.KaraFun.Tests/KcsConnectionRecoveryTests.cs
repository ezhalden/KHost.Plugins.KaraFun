using KHost.Plugins.KaraFun.Client.Kcs;
using Microsoft.Extensions.Logging.Abstractions;

namespace KHost.Plugins.KaraFun.Tests;

/// <summary>The remote has to survive a bad minute of wifi. It did not: one slow request retired it
/// for the rest of the session, and the only thing the room saw was the QR code quietly gone.
/// </summary>
public class KcsConnectionRecoveryTests
{
    /// <summary>The bug, exactly. HttpClient reports its own timeout as a TaskCanceledException,
    /// which is an OperationCanceledException with nobody having cancelled anything, so a catch on
    /// the type alone read a timed-out request as "we are shutting down" and returned.</summary>
    [Fact]
    public async Task AResolveThatTimesOut_DoesNotRetireTheRemote()
    {
        var attempts = 0;
        var reconnected = new TaskCompletionSource();

        await using var connection = new KcsConnection(
            resolveUrl: _ =>
            {
                // A request timeout, not a shutdown. This is what HttpClient actually throws.
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout");

                reconnected.TrySetResult();
                return Task.FromResult<string?>(null);
            },
            onMessage: (_, _) => Task.CompletedTask,
            logger: NullLogger.Instance,
            delay: (_, _) => Task.CompletedTask);

        connection.Start();

        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(attempts >= 2, "the pump gave up after the timeout instead of retrying");
    }

    /// <summary>The other half: a real shutdown must still stop it, or the fix above would spin a
    /// dead connection forever on the way out.</summary>
    [Fact]
    public async Task ARealCancellation_StopsThePump()
    {
        var reached = new TaskCompletionSource();
        var attempts = 0;

        var connection = new KcsConnection(
            resolveUrl: _ =>
            {
                Interlocked.Increment(ref attempts);
                reached.TrySetResult();
                return Task.FromResult<string?>(null);
            },
            onMessage: (_, _) => Task.CompletedTask,
            logger: NullLogger.Instance,
            delay: (_, _) => Task.CompletedTask);

        connection.Start();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await connection.DisposeAsync();

        var settled = attempts;
        await Task.Delay(120);

        Assert.Equal(settled, attempts);
    }
}
