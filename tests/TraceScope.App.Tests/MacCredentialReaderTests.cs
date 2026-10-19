using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class MacCredentialReaderTests
{
    private static byte[] Frame(byte status, int osStatus, int length, byte[]? payload = null)
    {
        var frame = new byte[9 + (payload?.Length ?? 0)]; frame[0] = status;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), osStatus);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(5), length);
        payload?.CopyTo(frame, 9); return frame;
    }
    [Fact]
    public async Task ReadsBoundedResponseWithoutAnyFileOrStandardStream()
    {
        var value = "synthetic-secret-λ"; var bytes = Encoding.UTF8.GetBytes(value);
        using var stream = new MemoryStream(Frame(0, 0, bytes.Length, bytes));
        Assert.Equal(value, await MacCredentialReader.ReadFrameAsync(stream, new byte[2560], default));
    }
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(0, 0, 2561)]
    [InlineData(0, -1, 10)]
    [InlineData(2, 0, 10)]
    [InlineData(1, -25308, 1)]
    public async Task RejectsMalformedOrUnboundedFrames(byte status, int code, int length)
    {
        using var stream = new MemoryStream(Frame(status, code, length));
        await Assert.ThrowsAsync<WebhookCredentialException>(() => MacCredentialReader.ReadFrameAsync(stream, new byte[2560], default));
    }
    [Fact]
    public async Task PreservesOnlyNativeStatusOnFailure()
    {
        using var stream = new MemoryStream(Frame(1, -25308, 0));
        var error = await Assert.ThrowsAsync<WebhookCredentialException>(() => MacCredentialReader.ReadFrameAsync(stream, new byte[2560], default));
        Assert.Equal(-25308, error.OSStatus); Assert.Equal(WebhookCredentialOperation.Read, error.Operation);
    }
    [Fact]
    public async Task RejectsInvalidUtf8AndTruncation()
    {
        using var utf8 = new MemoryStream(Frame(0, 0, 1, [0xff]));
        await Assert.ThrowsAsync<DecoderFallbackException>(() => MacCredentialReader.ReadFrameAsync(utf8, new byte[2560], default));
        using var shortFrame = new MemoryStream(Frame(0, 0, 3, [65]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => MacCredentialReader.ReadFrameAsync(shortFrame, new byte[2560], default));
    }
    [Fact]
    public async Task HonorsCancellation()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        using var stream = new MemoryStream(Frame(0, 0, 1, [65]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MacCredentialReader.ReadFrameAsync(stream, new byte[2560], source.Token));
    }
    // Reproduces Read() synchronously waiting on the UI thread during Apply.
    // MemoryStream alone cannot expose this: its header/payload reads complete inline.
    [Theory]
    [InlineData("success")]
    [InlineData("native-error")]
    [InlineData("cancellation")]
    [InlineData("truncated-payload")]
    public async Task DelayedResponseCompletesWithoutPumpingSettingsSynchronizationContext(string scenario)
    {
        var outcome = await Task.Run(() =>
        {
            var context = new NonPumpingContext();
            using var cancellation = new CancellationTokenSource();
            var payload = Encoding.UTF8.GetBytes("disposable-test-value");
            var frame = scenario switch
            {
                "native-error" => Frame(1, -25293, 0),
                "truncated-payload" => Frame(0, 0, payload.Length + 1, payload),
                _ => Frame(0, 0, payload.Length, payload)
            };
            using var stream = new DelayedFrameStream(frame);
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var read = MacCredentialReader.ReadFrameAsync(stream, new byte[2560], cancellation.Token);
                Assert.False(read.IsCompleted); // Actual asynchronous I/O, including the initial header.
                if (scenario == "cancellation") cancellation.CancelAfter(10);
                var completedWithoutPumping = ((IAsyncResult)read).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
                var postsWithoutPumping = context.PostCount;

                // On a regression, release any captured continuations before failing so
                // this test leaves no blocked threads or abandoned asynchronous work.
                var cleanup = Stopwatch.StartNew();
                while (!read.IsCompleted && cleanup.Elapsed < TimeSpan.FromSeconds(5))
                {
                    context.PumpOnce();
                    Thread.Sleep(1);
                }
                Assert.True(read.IsCompleted, "Credential read did not finish during bounded test cleanup.");
                string? result = null;
                var error = Record.Exception(() => result = read.GetAwaiter().GetResult());
                return (completedWithoutPumping, postsWithoutPumping, result, error, stream.ReadCount);
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });

        Assert.True(outcome.completedWithoutPumping, "Apply would deadlock waiting for a continuation posted to its blocked UI thread.");
        Assert.Equal(0, outcome.postsWithoutPumping);
        if (scenario == "success")
        {
            Assert.Null(outcome.error);
            Assert.Equal("disposable-test-value", outcome.result);
            Assert.Equal(2, outcome.ReadCount); // Both header and payload completed asynchronously.
        }
        else if (scenario == "native-error")
            Assert.Equal(-25293, Assert.IsType<WebhookCredentialException>(outcome.error).OSStatus);
        else if (scenario == "cancellation")
            Assert.IsAssignableFrom<OperationCanceledException>(outcome.error);
        else
            Assert.IsType<EndOfStreamException>(outcome.error);
    }

    private sealed class NonPumpingContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> pending = new();
        private int posts;
        public int PostCount => Volatile.Read(ref posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref posts);
            pending.Enqueue((callback, state));
        }
        public void PumpOnce()
        {
            if (pending.TryDequeue(out var work)) work.Callback(work.State);
        }
    }

    private sealed class DelayedFrameStream(byte[] frame) : MemoryStream(frame)
    {
        public int ReadCount { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            return base.Read(destination.Span);
        }
    }

    [Fact]
    public void StandaloneChildRejectsUnrelatedParentBeforeAnyCredentialAccess()
        => Assert.Equal(1, MacCredentialReader.RunChild("invalid", "invalid"));
}
