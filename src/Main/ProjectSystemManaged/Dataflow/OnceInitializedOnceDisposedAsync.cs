// Clean-room stub. The real OnceInitializedOnceDisposedAsync lives in the closed
// Microsoft.VisualStudio.ProjectSystem.dll base assembly. Reconstructed as a minimal
// thread-safe async lazy-init/dispose-once base matching DependenciesSnapshotProvider's usage
// (InitializeAsync() / InitializeCoreAsync(CancellationToken) / DisposeCoreAsync(bool)).
// See docs/project-system.md (Slice 42).

namespace Microsoft.VisualStudio.ProjectSystem;

public abstract class OnceInitializedOnceDisposedAsync : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    protected OnceInitializedOnceDisposedAsync(object? joinableTaskContext = null)
    {
    }

    protected async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            await InitializeCoreAsync(CancellationToken.None);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected abstract Task InitializeCoreAsync(CancellationToken cancellationToken);

    protected abstract Task DisposeCoreAsync(bool initialized);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await DisposeCoreAsync(_initialized);
        }
        finally
        {
            _gate.Release();
        }
    }
}
