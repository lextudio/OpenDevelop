// Clean-room stub. The real OnceInitializedOnceDisposed lives in the closed
// Microsoft.VisualStudio.ProjectSystem.dll base assembly. Reconstructed here as a minimal
// thread-safe lazy-init/dispose-once base, matching the public usage pattern in
// MSBuildDependencySubscriber (EnsureInitialized() / Initialize() / Dispose(bool)).
// See docs/project-system.md (Slice 41).

namespace Microsoft.VisualStudio.ProjectSystem;

public abstract class OnceInitializedOnceDisposed : IDisposable
{
    private readonly object _lock = new();
    private bool _initialized;
    private bool _disposed;

    protected bool IsInitialized => _initialized;
    protected bool IsDisposed => _disposed;

    protected void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (_lock)
        {
            if (_initialized)
            {
                return;
            }

            Initialize();
            _initialized = true;
        }
    }

    protected virtual void Initialize()
    {
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Dispose(disposing: true);
        }

        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
