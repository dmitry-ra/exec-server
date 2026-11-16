using System.Runtime.ExceptionServices;

namespace ExecServer.Storage;

/// <summary>
/// Retains one raw output stream. Operations serialize against disposal; waiters
/// never hold the storage gate. The caller owns and secures the spool directory.
/// </summary>
public sealed class OutputBuffer : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _state = new();
    private readonly string _spoolDirectory;
    private readonly long _memoryThreshold;
    private MemoryStream? _memory = new();
    private FileStream? _file;
    private TaskCompletionSource _changed = NewChange();
    private long _length;
    private bool _isComplete;
    private bool _disposed;
    private bool _isFileBacked;
    private Exception? _error;

    public OutputBuffer(string spoolDirectory, long memoryThreshold)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(memoryThreshold);
        _spoolDirectory = Path.GetFullPath(spoolDirectory);
        var directory = new DirectoryInfo(_spoolDirectory);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Spool directory must be an existing non-link directory.", nameof(spoolDirectory));
        _memoryThreshold = memoryThreshold;
    }

    public Guid BlobId { get; } = Guid.NewGuid();
    public long Length { get { lock (_state) return _length; } }
    public bool IsFileBacked { get { lock (_state) return _isFileBacked; } }
    public bool IsComplete { get { lock (_state) return _isComplete; } }
    public Exception? Error { get { lock (_state) return _error; } }

    public async Task AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long offset;
            lock (_state)
            {
                ThrowIfDisposed();
                if (_error is not null) ExceptionDispatchInfo.Capture(_error).Throw();
                if (_isComplete) throw new InvalidOperationException("Output has completed.");
                offset = _length;
            }
            if (bytes.Length == 0) return;
            try
            {
                // MemoryStream is int-sized even when the configured threshold is larger.
                if (_file is null && (offset + bytes.Length > _memoryThreshold || offset + bytes.Length > int.MaxValue))
                    await SpillAsync(cancellationToken).ConfigureAwait(false);
                if (_file is not null)
                    await RandomAccess.WriteAsync(_file.SafeFileHandle, bytes, offset, cancellationToken).ConfigureAwait(false);
                else
                    _memory!.Write(bytes.Span);
                lock (_state)
                {
                    _length = checked(offset + bytes.Length);
                    Pulse();
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                lock (_state)
                {
                    _error = error;
                    Pulse();
                }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int available;
            lock (_state)
            {
                ThrowIfDisposed();
                if (offset > _length) throw new ArgumentOutOfRangeException(nameof(offset), "Offset exceeds retained output length.");
                available = (int)Math.Min(count, _length - offset);
            }
            var result = new byte[available];
            if (_file is null)
            {
                _memory!.GetBuffer().AsSpan((int)offset, available).CopyTo(result);
                return result;
            }
            var read = 0;
            while (read < result.Length)
            {
                var actual = await RandomAccess.ReadAsync(_file.SafeFileHandle, result.AsMemory(read), offset + read, cancellationToken).ConfigureAwait(false);
                if (actual == 0) throw new IOException("Retained output ended before its recorded length.");
                read += actual;
            }
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lock (_state)
            {
                _error ??= error;
                Pulse();
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task WaitForChangeAsync(long knownLength, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(knownLength);
        if (wait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(wait));
        Task pending;
        lock (_state)
        {
            ThrowIfDisposed();
            if (_length != knownLength || _isComplete || _error is not null || wait == TimeSpan.Zero) return;
            // The snapshot and subscription are atomic with append/completion.
            pending = _changed.Task;
        }
        try { await pending.WaitAsync(wait, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    public void MarkComplete()
    {
        lock (_state)
        {
            ThrowIfDisposed();
            _isComplete = true;
            Pulse();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_state)
            {
                if (_disposed) return;
                _disposed = true;
                Pulse();
            }
            _memory?.Dispose();
            _memory = null;
            if (_file is not null) await _file.DisposeAsync().ConfigureAwait(false);
            _file = null;
        }
        finally { _gate.Release(); }
    }

    private async Task SpillAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_spoolDirectory, BlobId.ToString("N") + ".blob");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.RandomAccess | FileOptions.DeleteOnClose,
            BufferSize = 1
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var file = new FileStream(path, options);
        try
        {
            if (_memory!.Length != 0)
                await RandomAccess.WriteAsync(file.SafeFileHandle, _memory.GetBuffer().AsMemory(0, (int)_memory.Length), 0, cancellationToken).ConfigureAwait(false);
            _file = file;
            _memory.Dispose();
            _memory = null;
            lock (_state) _isFileBacked = true;
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static TaskCompletionSource NewChange() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse()
    {
        var previous = _changed;
        _changed = NewChange();
        previous.TrySetResult();
    }
}
