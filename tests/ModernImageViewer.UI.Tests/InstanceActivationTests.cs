using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

using ModernImageViewer.Platform.Activation;

namespace ModernImageViewer.UI.Tests;

public sealed class InstanceActivationTests
{
    [Fact]
    public async Task ForwardingAcknowledgesAcceptanceAndRecoversFromFailedCallback()
    {
        string key = Guid.NewGuid().ToString("N");
        ConcurrentQueue<string[]> requests = new();
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using PrimaryInstanceHost primary = new(key, paths =>
        {
            requests.Enqueue(paths);
            int call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                throw new InvalidOperationException("The dispatcher rejected this request.");
            }

            if (call == 2)
            {
                received.TrySetResult();
                return accepted.Task;
            }

            return Task.CompletedTask;
        });
        using SingleInstanceService secondary = new(key);
        Assert.False(secondary.TryAcquirePrimary());
        string[] paths = [@"C:\图片\空 格 & (1).png", "", "   "];

        Assert.False(await secondary.SendAsync(paths, TestContext.Current.CancellationToken));
        Task<bool> forwarding = secondary.SendAsync(paths, TestContext.Current.CancellationToken);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(forwarding.IsCompleted);
        accepted.TrySetResult();
        Assert.True(await forwarding);
        Assert.True(await secondary.SendAsync([], TestContext.Current.CancellationToken));

        Assert.Equal(3, requests.Count);
        Assert.Equal(paths, requests.ElementAt(1));
        Assert.Empty(requests.ElementAt(2));
    }

    [Fact]
    public async Task ForwardingBoundsInputAndSupportsCancellationWithoutAListener()
    {
        using SingleInstanceService secondary = new(Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(300));
        Assert.False(await secondary.SendAsync(new string[129], TestContext.Current.CancellationToken));
        Assert.False(await secondary.SendAsync([new string('x', 32_769)], TestContext.Current.CancellationToken));
        Assert.False(await secondary.SendAsync(Enumerable.Repeat(new string('x', 32_768), 40).ToArray(), TestContext.Current.CancellationToken));
        Assert.False(await secondary.SendAsync([], TestContext.Current.CancellationToken));

        using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        canceled.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondary.SendAsync([], canceled.Token));
    }

    // A Windows mutex is thread-owned. Keep acquisition and disposal on this dedicated
    // thread while the async test client naturally changes threads between awaits.
    private sealed class PrimaryInstanceHost : IDisposable
    {
        private readonly ManualResetEventSlim _started = new();
        private readonly ManualResetEventSlim _stop = new();
        private readonly Thread _thread;
        private ExceptionDispatchInfo? _error;

        public PrimaryInstanceHost(string key, Func<string[], Task> receive)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    using SingleInstanceService service = new(key);
                    Assert.True(service.TryAcquirePrimary());
                    service.StartListening(receive);
                    _started.Set();
                    _stop.Wait();
                }
#pragma warning disable CA1031 // Propagate errors on the xUnit thread rather than terminate the process.
                catch (Exception exception)
                {
                    _error = ExceptionDispatchInfo.Capture(exception);
                    _started.Set();
                }
#pragma warning restore CA1031
            })
            {
                IsBackground = true,
            };
            _thread.Start();
            Assert.True(_started.Wait(TimeSpan.FromSeconds(5)));
            _error?.Throw();
        }

        public void Dispose()
        {
            _stop.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)));
            _started.Dispose();
            _stop.Dispose();
            _error?.Throw();
        }
    }
}
