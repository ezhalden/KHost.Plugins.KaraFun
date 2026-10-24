using KHost.Plugins.KaraFun.Client;

namespace KHost.Plugins.KaraFun.Tests;

public class KaraFunApiDownloadTests
{
    private const string SongRequest =
        """<response status="OK"><song><stream>https://cdn.example.test/6229.kit</stream><md5>00</md5><filesize>0</filesize></song></response>""";

    private static KaraFunApi Api(TimeSpan idle)
        => new(ClientProfile.MacOS, bodyIdleTimeout: idle, http: new HttpClient(new Stub(_ => new HttpResponseMessage
        {
            Content = new StringContent(SongRequest),
        })));

    private static HttpClient Cdn(Stream body)
        => new(new Stub(_ => new HttpResponseMessage { Content = new StreamContent(body) }));

    /// <summary>The body stops after headers, where HttpClient.Timeout no longer applies.</summary>
    [Fact]
    public async Task ABodyThatStalls_FailsWithATimeoutRatherThanWaiting()
    {
        var api = Api(TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => api.DownloadSongAsync("sk", 6229, cdn: Cdn(new SlowStream(chunks: 2, TimeSpan.FromHours(1)))));

        Assert.Contains("stalled", ex.Message);
    }

    /// <summary>The timer starts with the request, not with the first chunk.</summary>
    [Fact]
    public async Task ABodyThatNeverStarts_FailsWithATimeoutToo()
    {
        var api = Api(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(
            () => api.DownloadSongAsync("sk", 6229, cdn: Cdn(new SlowStream(1, TimeSpan.FromHours(1), delayFirst: true))));
    }

    /// <summary>Idle, not total: a slow but moving transfer longer than the timeout still finishes.</summary>
    [Fact]
    public async Task ABodyThatKeepsMoving_IsNotCutOffByTheIdleTimeout()
    {
        var api = Api(TimeSpan.FromMilliseconds(400));

        var download = await api.DownloadSongAsync(
            "sk", 6229, cdn: Cdn(new SlowStream(chunks: 12, TimeSpan.FromMilliseconds(50))));

        Assert.Equal(12, download!.Data.Length);
    }

    /// <summary>The caller's own cancel must stay a cancel, or a dequeue would read as a network failure.</summary>
    [Fact]
    public async Task ACancelFromTheCaller_StaysACancel()
    {
        var api = Api(TimeSpan.FromHours(1));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => api.DownloadSongAsync("sk", 6229, cdn: Cdn(new SlowStream(2, TimeSpan.FromHours(1))), ct: cancel.Token));
    }

    /// <summary>Hands back a distinct client per name and records what it was asked for.</summary>
    private sealed class RecordingFactory(HttpClient api, HttpClient cdn) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return name == KaraFunApi.CdnClientName ? cdn : api;
        }
    }

    /// <summary>The kit body is bytes, the headers are all the factory's resilience limits may cover.</summary>
    [Fact]
    public async Task ADownload_UsesTheFactorysNamedClients_AndReadsOnlyHeadersBeforeStreaming()
    {
        var apiClient = new HttpClient(new Stub(_ => new HttpResponseMessage { Content = new StringContent(SongRequest) }));
        var cdnBodyRead = false;
        var cdnClient = new HttpClient(new Stub(_ => new HttpResponseMessage
        {
            Content = new StreamContent(new FlagStream(() => cdnBodyRead = true)),
        }));
        var factory = new RecordingFactory(apiClient, cdnClient);
        var api = new KaraFunApi(ClientProfile.MacOS, httpClientFactory: factory);

        var download = await api.DownloadSongAsync("sk", 6229);

        Assert.Equal([KaraFunApi.ApiClientName, KaraFunApi.CdnClientName], factory.Names);
        Assert.True(cdnBodyRead);
        Assert.Equal(3, download!.Data.Length);
    }

    /// <summary>ResponseHeadersRead: the response is handed back before its body is buffered.</summary>
    [Fact]
    public async Task AKitDownload_ReturnsFromTheCdnBeforeTheBodyIsBuffered()
    {
        var apiClient = new HttpClient(new Stub(_ => new HttpResponseMessage { Content = new StringContent(SongRequest) }));
        var cdnClient = new HttpClient(new Stub(_ => new HttpResponseMessage
        {
            Content = new StreamContent(new SlowStream(chunks: 2, TimeSpan.FromMilliseconds(10))),
        }));
        var api = new KaraFunApi(ClientProfile.MacOS, httpClientFactory: new RecordingFactory(apiClient, cdnClient));
        var progress = new List<long>();

        await api.DownloadSongAsync("sk", 6229, onProgress: (received, _) => progress.Add(received));

        Assert.Equal([1L, 2L], progress);
    }

    /// <summary>Yields three bytes and flags that the body was read.</summary>
    private sealed class FlagStream(Action onRead) : Stream
    {
        private int _served;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            if (_served == 3) return ValueTask.FromResult(0);
            _served = 3;
            buffer.Span[..3].Fill(1);
            return ValueTask.FromResult(3);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(answer(request));
    }

    /// <summary>Hands out one byte per read, waiting between them; after the last it ends.</summary>
    private sealed class SlowStream(int chunks, TimeSpan between, bool delayFirst = false) : Stream
    {
        private int _served;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // The first read answers at once, so headers and one byte land before any stall.
            if (_served > 0 || delayFirst)
                await Task.Delay(between, cancellationToken);

            if (_served == chunks)
                return 0;

            _served++;
            buffer.Span[0] = 1;
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
