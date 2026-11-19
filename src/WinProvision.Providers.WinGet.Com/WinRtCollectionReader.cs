using Microsoft.Extensions.Logging;

namespace WinProvision.Providers.WinGet.Com;

internal static class WinRtCollectionReader
{
    internal static IEnumerable<T> Enumerate<T>(IReadOnlyList<T> collection, string collectionName,
        ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        for (int index = 0; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count;
            T item;
            try
            {
                count = collection.Count;
                if (index >= count) break;
            }
            catch (InvalidCastException exception)
            {
                logger?.LogError(exception,
                    "WinRT collection access failed; collection={Collection}; collectionType={CollectionType}; elementType={ElementType}; index={Index}",
                    collectionName, collection.GetType().FullName, typeof(T).FullName, index);
                throw;
            }
            try { item = collection[index]; }
            catch (InvalidCastException exception)
            {
                logger?.LogError(exception,
                    "WinRT collection access failed; collection={Collection}; collectionType={CollectionType}; elementType={ElementType}; index={Index}",
                    collectionName, collection.GetType().FullName, typeof(T).FullName, index);
                throw;
            }
            yield return item;
        }
    }

    internal static T? FirstOrDefault<T>(IReadOnlyList<T> collection, Func<T, bool> predicate,
        string collectionName, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        foreach (T item in Enumerate(collection, collectionName, logger))
            if (predicate(item)) return item;
        return default;
    }
}
