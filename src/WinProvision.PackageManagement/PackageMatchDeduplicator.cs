namespace WinProvision.PackageManagement;

/// <summary>Consolida consultas por fonte e ID sem materializar chaves compostas por pacote.</summary>
internal static class PackageMatchDeduplicator
{
    internal static WinGetService.WinGetPackageMatch[] Deduplicate(
        IEnumerable<IReadOnlyList<WinGetService.WinGetPackageMatch>> batches,
        CancellationToken cancellationToken)
    {
        var results = new List<WinGetService.WinGetPackageMatch>();
        var idsBySource = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in batches)
        {
            foreach (var match in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!idsBySource.TryGetValue(match.Source, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    idsBySource.Add(match.Source, ids);
                }
                if (ids.Add(match.PackageId)) results.Add(match);
            }
        }
        return results.ToArray();
    }
}
