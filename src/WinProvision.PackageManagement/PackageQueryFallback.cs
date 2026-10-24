namespace WinProvision.PackageManagement;

public static class PackageQueryFallback
{
    public static async Task<T> RunAsync<T>(IEnumerable<Func<CancellationToken, Task<T>>> methods,
        Func<T, bool> accept, Action<string> log, CancellationToken token)
    {
        Exception? last = null;
        T? lastResult = default;
        bool hasResult = false;
        bool lastAttemptFailed = false;
        foreach (var method in methods)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                T result = await method(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lastResult = result;
                hasResult = true;
                lastAttemptFailed = false;
                if (accept(result)) return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is not InvalidDataException && exception is
                (InvalidOperationException or NotSupportedException or IOException or HttpRequestException
                or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception or TimeoutException))
            {
                last = exception;
                lastAttemptFailed = true;
                log($"Método indisponível: {exception.Message.Replace("\r", " ").Replace("\n", " ")}");
            }
        }
        if (lastAttemptFailed && last is not null)
            throw new InvalidOperationException("Não foi possível concluir a consulta pelos métodos disponíveis.", last);
        if (hasResult) return lastResult!;
        throw new InvalidOperationException("Nenhum método disponível para a consulta.");
    }
}
