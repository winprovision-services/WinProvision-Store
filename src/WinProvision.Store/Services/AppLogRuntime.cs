using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

internal static class AppLogRuntime
{
    private static readonly Lazy<State> Current = new(CreateState, LazyThreadSafetyMode.ExecutionAndPublication);

    public static AppLogSettingsStore Settings => Current.Value.Settings;
    public static IAppLog Log => Current.Value.Log;

    public static Task FlushAsync(CancellationToken cancellationToken = default) =>
        Log.FlushAsync(cancellationToken);

    public static async ValueTask DisposeAsync()
    {
        State state = Current.Value;
        await state.Log.FlushAsync().ConfigureAwait(false);
        await state.Sink.DisposeAsync().ConfigureAwait(false);
    }

    private static State CreateState()
    {
        var settings = new AppLogSettingsStore();
        var sink = new JsonLinesFileLogSink(AppLogPaths.GetLogDirectory(), () => settings.CurrentOptions);
        var log = new AppLogService(sink, () => settings.CurrentOptions);
        log.MinimumLevel = settings.Current.MinimumLevel;
        return new State(settings, sink, log);
    }

    private sealed record State(AppLogSettingsStore Settings, JsonLinesFileLogSink Sink, IAppLog Log);
}
