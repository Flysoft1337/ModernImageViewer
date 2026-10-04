using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ModernImageViewer.Platform.Activation;

/// <summary>
/// Owns one window per Windows user and logon session. Acquire and dispose on the same
/// thread (the WPF UI thread); the pipe listener never owns or releases the mutex.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly TimeSpan _connectionTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private int _ownerThreadId;
    private bool _listening;
    private bool _disposed;

    public SingleInstanceService(string? instanceKey = null, TimeSpan? connectionTimeout = null)
    {
        _connectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(4);
        if (_connectionTimeout <= TimeSpan.Zero || _connectionTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string userId = identity.User?.Value ?? throw new InvalidOperationException("The Windows user SID is unavailable.");
        using Process process = Process.GetCurrentProcess();
        string scope = $"{instanceKey ?? "ModernImageViewer.Portable.v1"}|{userId}|{process.SessionId}";
        string identifier = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        _pipeName = $"ModernImageViewer.Activation.{identifier}";
        // Local scopes the mutex to the logon session; its default Windows ACL belongs
        // to the creating user's token. CurrentUserOnly also restricts both pipe ends.
        _mutex = new Mutex(false, $"Local\\ModernImageViewer.Instance.{identifier}");
    }

    public bool IsPrimary { get; private set; }

    public bool TryAcquirePrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsPrimary)
        {
            return true;
        }

        try
        {
            IsPrimary = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // WaitOne grants ownership when a crashed primary abandoned its mutex.
            IsPrimary = true;
        }

        if (IsPrimary)
        {
            _ownerThreadId = Environment.CurrentManagedThreadId;
        }

        return IsPrimary;
    }

    /// <summary>
    /// The callback must queue a request on the UI dispatcher and complete after it is
    /// accepted, without waiting for image decoding. Only then is acknowledgement sent.
    /// </summary>
    public void StartListening(Func<string[], Task> receive)
    {
        ArgumentNullException.ThrowIfNull(receive);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary || _listening)
        {
            throw new InvalidOperationException("Only the primary instance can start one activation listener.");
        }

        _listening = true;
        CancellationToken cancellationToken = _shutdown.Token;
        _ = Task.Run(() => ListenAsync(receive, cancellationToken), CancellationToken.None);
    }

    public async Task<bool> SendAsync(string[] paths, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] frame;
        try
        {
            frame = ActivationProtocol.Encode(paths);
        }
        catch (ArgumentException)
        {
            return false;
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        timeout.CancelAfter(_connectionTimeout);
        try
        {
            await using NamedPipeClientStream pipe = new(".", _pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            // The primary may own the mutex before its dispatcher and listener are ready.
            while (!pipe.IsConnected)
            {
                try
                {
                    await pipe.ConnectAsync(250, timeout.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await Task.Delay(50, timeout.Token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await Task.Delay(50, timeout.Token).ConfigureAwait(false);
                }
            }

            await pipe.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            byte[] acknowledgement = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgement, timeout.Token).ConfigureAwait(false);
            return acknowledgement[0] == 1;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (IOException)
        {
            // Never retry after sending: an acknowledgement may have been lost after
            // the primary accepted the request, and retrying would duplicate activation.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (IsPrimary && _ownerThreadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("Dispose the primary instance on the mutex-owning UI thread.");
        }

        _disposed = true;
        _shutdown.Cancel();
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
            IsPrimary = false;
        }

        _mutex.Dispose();
        _shutdown.Dispose();
    }

#pragma warning disable CA1031 // An untrusted IPC client or failed UI callback must not kill subsequent activation.
    private async Task ListenAsync(Func<string[], Task> receive, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream pipe = new(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                bool accepted = false;
                try
                {
                    string[] paths = await ActivationProtocol.ReadAsync(pipe, requestTimeout.Token).ConfigureAwait(false);
                    await receive(paths).WaitAsync(requestTimeout.Token).ConfigureAwait(false);
                    accepted = true;
                }
                catch (Exception) when (!requestTimeout.IsCancellationRequested)
                {
                    // Send rejection for malformed messages or requests the dispatcher
                    // cannot accept. The next connection still gets a healthy listener.
                }

                await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, requestTimeout.Token).ConfigureAwait(false);
                await pipe.FlushAsync(requestTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Avoid a tight retry loop for transient pipe creation/access failures.
                try
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
#pragma warning restore CA1031
}
