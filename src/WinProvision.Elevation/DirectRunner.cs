using System.Diagnostics;

namespace WinProvision.Elevation;

public sealed class DirectRunner : IElevatedRunner
{
    public async Task<ElevationResult> RunAsync(ElevatedCommand command, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (cancellationToken.IsCancellationRequested)
            return new(ElevationStatus.Cancelled, null, string.Empty, string.Empty);
        if (command.ExpectedSha256 is not null)
        {
            try
            {
                if (!await ElevationIntegrityVerifier.VerifyAsync(command.ExecutablePath, command.ExpectedSha256, cancellationToken).ConfigureAwait(false))
                    return new(ElevationStatus.Rejected, null, string.Empty, string.Empty, "O SHA-256 do arquivo não corresponde ao valor esperado.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(ElevationStatus.Cancelled, null, string.Empty, string.Empty);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new(ElevationStatus.Rejected, null, string.Empty, string.Empty, exception.Message);
            }
        }
        var startInfo = ProcessRunner.CreateStartInfo(command.ExecutablePath, command.WorkingDirectory);
        foreach (string argument in command.Arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            return await ProcessRunner.RunAsync(startInfo, progress, cancellationToken, waitAfterStart: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(ElevationStatus.Cancelled, null, string.Empty, string.Empty);
        }
    }
}
