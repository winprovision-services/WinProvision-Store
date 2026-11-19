using System.Globalization;
using System.Text;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>Stable relevance ordering for mixed package catalogs using only item metadata.</summary>
public static class PackageSearchRanking
{
    public static IReadOnlyList<AppEntry> Rank(IEnumerable<AppEntry> entries, string query,
        PackageSearchRankingDefinition options)
    {
        RankedApp[] apps = entries.Select((app, index) => new RankedApp(app, index,
            MatchTier(app, query), Normalize(app.Name), Normalize(app.Publisher), DuplicateKey(app, options.NoiseSuffixes)))
            .ToArray();

        var normalizedSignals = NormalizeSignalsBySource(apps, options);
        var duplicateAlternates = apps.Where(app => app.DuplicateKey.Length > 0)
            .GroupBy(app => $"{app.DuplicateKey}\0{app.PublisherKey}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.OrderByDescending(app => GetSourceWeight(app.Entry.Source, options))
                .ThenByDescending(app => normalizedSignals[app.Index].Total)
                .ThenBy(app => app.Entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(app => app.Entry.Id, StringComparer.OrdinalIgnoreCase)
                .Skip(1))
            .Select(app => app.Index)
            .ToHashSet();

        return apps
            .Select(app =>
            {
                NormalizedSignals signals = normalizedSignals[app.Index];
                bool derived = IsDerived(app.Entry.Id, options.NoiseSuffixes);
                double structuralAdjustment = (derived ? options.DerivedPenalty : 0)
                    + (duplicateAlternates.Contains(app.Index) ? options.DuplicatePenalty : 0);
                double score = signals.Total + GetSourceWeight(app.Entry.Source, options) - structuralAdjustment;
                int sourceTier = app.Tier / Math.Clamp(options.SourceMatchAdvantageThreshold, 1, 6);
                return (App: app, Tier: sourceTier, Score: score);
            })
            .OrderBy(item => item.Tier)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.App.NameKey, StringComparer.Ordinal)
            .ThenBy(item => item.App.Entry.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.App.Entry.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.App.Index)
            .Select(item => item.App.Entry)
            .ToArray();
    }

    /// <summary>Returns generic tiers: exact, prefix, exact word sequence, contains, metadata, fallback.</summary>
    public static int MatchTier(AppEntry app, string query)
    {
        return MatchTierNormalized(NormalizeText(app.Name), NormalizeText(app.Id), NormalizeText(app.Description),
            app.Tags.Select(NormalizeText).ToArray(), NormalizeText(app.Publisher), NormalizeText(query));
    }

    public static int MatchTierNormalized(string name, string id, string description,
        IReadOnlyList<string> tags, string publisher, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0) return 6;
        if (name == normalizedQuery || id == normalizedQuery) return 0;
        if (StartsWithPhrase(name, normalizedQuery) || StartsWithPhrase(id, normalizedQuery)) return 1;
        if (HasExactWordSequence(name, normalizedQuery) || HasExactWordSequence(id, normalizedQuery)) return 2;
        if (ContainsPhrase(name, normalizedQuery) || ContainsPhrase(id, normalizedQuery)) return 3;
        if (ContainsPhrase(description, normalizedQuery)
            || tags.Any(tag => ContainsPhrase(tag, normalizedQuery))
            || ContainsPhrase(publisher, normalizedQuery)) return 4;
        return 5;
    }

    public static string NormalizeText(string? value) => Normalize(value);

    private static Dictionary<int, NormalizedSignals> NormalizeSignalsBySource(IReadOnlyList<RankedApp> apps,
        PackageSearchRankingDefinition options)
    {
        var result = new Dictionary<int, NormalizedSignals>();
        foreach (IGrouping<string, RankedApp> sourceGroup in apps.GroupBy(app => app.Entry.Source, StringComparer.OrdinalIgnoreCase))
        {
            RankedApp[] sourceApps = sourceGroup.ToArray();
            Dictionary<int, double> scores = Percentiles(sourceApps, app => app.Entry.HasCatalogScore ? app.Entry.Score : null, options.ScoreWeight);
            Dictionary<int, double> stars = Percentiles(sourceApps, app => app.Entry.HasGitHubMetrics ? app.Entry.GitHubStars : null, options.StarsWeight,
                logarithmic: true);
            Dictionary<int, double> downloads = Percentiles(sourceApps, app => app.Entry.SourceDownloadCount, options.DownloadsWeight,
                logarithmic: true);
            Dictionary<int, double> storeSignals = Percentiles(sourceApps, app => app.Entry.StoreRating is double rating
                ? rating * Math.Max(1, app.Entry.StoreRatingCount ?? 1) : null, options.StoreRatingWeight, logarithmic: true);
            foreach (RankedApp app in sourceApps)
            {
                double completeness = MetadataCompleteness(app.Entry);
                (double Value, double Weight)[] availableSignals =
                [
                    (scores.GetValueOrDefault(app.Index), scores.ContainsKey(app.Index) ? options.ScoreWeight : 0),
                    (stars.GetValueOrDefault(app.Index), stars.ContainsKey(app.Index) ? options.StarsWeight : 0),
                    (downloads.GetValueOrDefault(app.Index), downloads.ContainsKey(app.Index) ? options.DownloadsWeight : 0),
                    (storeSignals.GetValueOrDefault(app.Index), storeSignals.ContainsKey(app.Index) ? options.StoreRatingWeight : 0)
                ];
                double effectiveSignalWeight = availableSignals.Sum(signal => signal.Weight);
                double sourceSignalWeight = options.ScoreWeight + options.StarsWeight
                    + options.DownloadsWeight + options.StoreRatingWeight;
                double normalizedQuality = effectiveSignalWeight > 0
                    ? availableSignals.Sum(signal => signal.Value * signal.Weight) / effectiveSignalWeight * sourceSignalWeight
                    : sourceSignalWeight / 2;
                result[app.Index] = new NormalizedSignals(
                    normalizedQuality + (HasCatalogIcon(app.Entry) ? options.CuratedIconBonus : 0)
                    + completeness * options.MetadataCompletenessWeight);
            }
        }
        return result;
    }

    private static Dictionary<int, double> Percentiles(IEnumerable<RankedApp> apps,
        Func<RankedApp, double?> valueSelector, double weight, bool logarithmic = false)
    {
        RankedApp[] measured = apps.Where(app => valueSelector(app) is not null).ToArray();
        if (measured.Length == 0 || weight == 0) return [];
        double Transform(RankedApp app)
        {
            double value = valueSelector(app)!.Value;
            return logarithmic ? Math.Log10(Math.Max(0, value) + 1) : value;
        }
        var ordered = measured.Select(app => (app.Index, Value: Transform(app)))
            .OrderBy(item => item.Value).ThenBy(item => item.Index).ToArray();
        var result = new Dictionary<int, double>();
        if (ordered.Length == 1)
        {
            result[ordered[0].Index] = weight / 2;
            return result;
        }
        for (int start = 0; start < ordered.Length;)
        {
            int end = start + 1;
            while (end < ordered.Length && ordered[end].Value.Equals(ordered[start].Value)) end++;
            double averageRank = (start + end - 1) / 2d;
            double percentile = averageRank / (ordered.Length - 1) * weight;
            for (int index = start; index < end; index++) result[ordered[index].Index] = percentile;
            start = end;
        }
        return result;
    }

    private static double MetadataCompleteness(AppEntry app)
    {
        string?[] fields = [app.Publisher, app.Description, app.Homepage, app.License, app.Version, app.Moniker];
        return (double)fields.Count(value => !string.IsNullOrWhiteSpace(value)) / fields.Length;
    }

    private static bool HasCatalogIcon(AppEntry app) => !string.IsNullOrWhiteSpace(app.Media?.Icon)
        || !string.IsNullOrWhiteSpace(app.StoreIconUrl)
        || (!string.IsNullOrWhiteSpace(app.IconUrl) && app.IconUrl != IconService.DefaultIconPackUri);

    private static double GetSourceWeight(string source, PackageSearchRankingDefinition options) =>
        options.Sources.FirstOrDefault(item => item.Key.Equals(source, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool IsDerived(string id, IReadOnlyList<string> suffixes) =>
        StripNoiseSuffix(Normalize(id), suffixes) != Normalize(id);

    private static string DuplicateKey(AppEntry app, IReadOnlyList<string> suffixes)
    {
        string normalizedId = StripNoiseSuffix(Normalize(app.Id), suffixes);
        string normalizedName = StripNoiseSuffix(Normalize(app.Name), suffixes);
        return normalizedName.Length >= 4 ? normalizedName : normalizedId;
    }

    private static string StripNoiseSuffix(string value, IReadOnlyList<string> suffixes)
    {
        string result = value;
        foreach (string suffix in suffixes)
        {
            string normalizedSuffix = Normalize(suffix);
            if (normalizedSuffix.Length == 0) continue;
            if (result.EndsWith(" " + normalizedSuffix, StringComparison.Ordinal)) result = result[..^(normalizedSuffix.Length + 1)];
            else if (result == normalizedSuffix) result = string.Empty;
        }
        return result.Trim();
    }

    private static bool StartsWithPhrase(string value, string query) => value.StartsWith(query, StringComparison.Ordinal);
    private static bool ContainsPhrase(string value, string query) => value.Contains(query, StringComparison.Ordinal);

    private static bool HasExactWordSequence(string value, string query)
    {
        string[] words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] queryWords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (queryWords.Length == 0 || queryWords.Length > words.Length) return false;
        for (int start = 0; start <= words.Length - queryWords.Length; start++)
            if (words.AsSpan(start, queryWords.Length).SequenceEqual(queryWords)) return true;
        return false;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool pendingSeparator = false;
        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else pendingSeparator = true;
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed record RankedApp(AppEntry Entry, int Index, int Tier, string NameKey, string PublisherKey, string DuplicateKey);
    private readonly record struct NormalizedSignals(double Total);
}
