namespace WinProvision.Store.Services;

public enum OperationIconLoadState
{
    Loading,
    Loaded,
    Failed,
    Missing
}

public static class OperationIconFallbackResolver
{
    public static bool ShouldShowFallback(OperationIconLoadState state) => state != OperationIconLoadState.Loaded;
}
