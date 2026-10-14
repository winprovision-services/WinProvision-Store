namespace WinProvision.Core.Services;

/// <summary>Reads vector views by index without requiring a separate native iterable interface.</summary>
public static class IndexedCollectionReader
{
    public static IEnumerable<T> Read<T>(IReadOnlyList<T> items, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        for (int index = 0, count = items.Count; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return items[index];
        }
    }
}
