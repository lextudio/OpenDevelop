// Clean-room stub matching CPS's Microsoft.VisualStudio.ProjectSystem.Utilities.DisposableBag
// usage in collection-initializer form (`new DisposableBag { a, b, c }`).
// See docs/project-system.md (Slice 41).

namespace Microsoft.VisualStudio.ProjectSystem.Utilities;

public sealed class DisposableBag : IDisposable, System.Collections.IEnumerable
{
    private readonly List<IDisposable> _items = new();

    public void Add(IDisposable item) => _items.Add(item);

    public System.Collections.IEnumerator GetEnumerator() => _items.GetEnumerator();

    public void Dispose()
    {
        foreach (var item in _items)
        {
            item.Dispose();
        }
    }
}
