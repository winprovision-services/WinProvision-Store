using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

public sealed partial class WinGetService
{
    private static async Task<WingetExecutionResult> RunElevatedComWorkerAsync(ComOperationKind operation,
        string packageId, string? installLocation, Action<string>? log, Action<InstallProgressUpdate>? progress,
        CancellationToken token, InstallAttemptState state)
    {
        string worker = Path.Combine(AppContext.BaseDirectory, "cli", "winprovision.exe");
        if (!File.Exists(worker)) worker = Path.Combine(AppContext.BaseDirectory, "winprovision.exe");
        if (!File.Exists(worker)) throw new FileNotFoundException("Worker COM ausente. Reinstale o WinProvision Store.", worker);
        if (!await WorkerSupportsProtocolAsync(worker, token).ConfigureAwait(false))
            return new WingetExecutionResult
            {
                Success = false, ExitCode = -1, FailureReason = WingetFailureReason.InternalError,
                Output = "A CLI auxiliar não suporta o protocolo COM desta versão. Recompile ou reinstale o WinProvision Store."
            };
        string request = ElevatedComProtocol.EncodeRequest(new(operation switch { ComOperationKind.Install => "install", ComOperationKind.Upgrade => "update", _ => "uninstall" },
            packageId, installLocation));
        WingetExecutionResult? result = null;
        Exception? protocolError = null;
        int code;
        try
        {
            code = await GsudoElevationService.RunElevatedAsync(worker, $"{ElevatedComProtocol.Command} {request}", token,
                onLog: line =>
                {
                    try
                    {
                        if (line == ElevatedComProtocol.ReadyMarker)
                            state.Started = true;
                        else if (line.StartsWith(ElevatedComProtocol.ResultPrefix, StringComparison.Ordinal))
                            result = result is null ? ElevatedComProtocol.DecodeResult(line)
                                : throw new InvalidDataException("Resposta COM duplicada.");
                        else if (line.StartsWith(ElevatedComProtocol.ProgressPrefix, StringComparison.Ordinal))
                            progress?.Invoke(ElevatedComProtocol.DecodeProgress(line));
                        else log?.Invoke(line);
                    }
                    catch (Exception ex) { protocolError = ex; }
                }).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC recusado/cancelado: resultado terminal (ElevationCanceled não permite fallback para outro backend).
            return new WingetExecutionResult
            {
                Success = false, ExitCode = 1223, WasElevated = true, InstallerStarted = false,
                FailureReason = WingetFailureReason.ElevationCanceled,
                Output = "Elevação não concluída pelo WinProvision Elevator."
            };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Política bloqueando, helper ausente/adulterado, sem área de trabalho interativa ou UAC sem resposta.
            // Nada foi despachado: falhas internas ainda podem usar a API própria; bloqueio por política é terminal.
            PackageDiagnostics.Write($"ELEVATED COM worker não iniciado: 0x{ex.NativeErrorCode:X} {ex.Message}");
            return new WingetExecutionResult
            {
                Success = false, ExitCode = ex.NativeErrorCode, WasElevated = true, InstallerStarted = false,
                FailureReason = ex.NativeErrorCode == 1625 ? WingetFailureReason.BlockedByPolicy : WingetFailureReason.InternalError,
                Output = ex.Message
            };
        }
        if (code == 0 && result is not null && protocolError is null && state.Started)
        {
            result.WasElevated = true;
            state.InstallerStarted = result.InstallerStarted;
            return result;
        }
        PackageDiagnostics.Write($"ELEVATED COM worker exit={code} ready={state.Started} result={result is not null}");
        // The elevated worker may have dispatched the installer; never start another backend.
        return new WingetExecutionResult
        {
            Success = false, ExitCode = code == 0 ? -1 : code, WasElevated = true, InstallerStarted = state.Started,
            FailureReason = WingetFailureReason.InternalError,
            Output = state.Started
                ? "O worker COM não retornou um resultado válido após inicializar; a operação não será repetida automaticamente."
                : "O worker COM falhou antes de inicializar."
        };
    }

    private static async Task<bool> WorkerSupportsProtocolAsync(string worker, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(worker)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(ElevatedComProtocol.ProbeCommand);
        try
        {
            if (!process.Start()) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0
                && (await output.ConfigureAwait(false)).Trim() == ElevatedComProtocol.ReadyMarker
                && (await error.ConfigureAwait(false)).Length == 0;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            PackageDiagnostics.Warning(ex, "ElevatedComWorkerProbe");
            return false;
        }
    }

    internal static bool CanFallbackFromElevatedWorker(WingetExecutionResult result, bool forceComOnly,
        CancellationToken token) => !result.Success && !result.InstallerStarted
        && result.FailureReason == WingetFailureReason.InternalError
        && !forceComOnly && !token.IsCancellationRequested;

    private async Task<InstallerPreferences?> ResolveInstallerElevationAsync(string id, string source, CancellationToken token)
    {
        if (!source.Equals("winget", StringComparison.OrdinalIgnoreCase)) return null;
        // Read-only COM preflight also covers explicit CLI/API selections. No installer is dispatched here.
        try
        {
            if (!WinGetFactoryHelper.IsComDisabled)
            {
                var resolved = await Task.Run(async () =>
                {
                    var manager = WinGetFactoryHelper.CreateResilientPackageManager();
                    var reference = manager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog);
                    reference.AcceptSourceAgreements = true;
                    var connection = await reference.ConnectAsync().AsTask(token).ConfigureAwait(false);
                    if (connection.Status != ConnectResultStatus.Ok || connection.PackageCatalog is null) return null;
                    var options = WinGetFactoryHelper.CreateFindPackagesOptions();
                    var filter = WinGetFactoryHelper.CreatePackageMatchFilter();
                    filter.Field = PackageMatchField.Id; filter.Option = PackageFieldMatchOption.Equals; filter.Value = id;
                    options.Filters.Add(filter);
                    var matches = await connection.PackageCatalog.FindPackagesAsync(options).AsTask(token).ConfigureAwait(false);
                    var package = IndexedCollectionReader.Read(matches.Matches, token).FirstOrDefault()?.CatalogPackage;
                    var installOptions = WinGetFactoryHelper.CreateInstallOptions();
                    installOptions.PackageInstallMode = PackageInstallMode.Silent;
                    installOptions.AcceptPackageAgreements = true;
                    var installer = package?.DefaultInstallVersion?.GetApplicableInstaller(installOptions);
                    return installer is null ? null : new InstallerPreferences(installer.Scope switch
                    {
                        PackageInstallerScope.User => "user", PackageInstallerScope.System => "machine", _ => null
                    }, ReadInstallerArchitecture(installer),
                        installer.ElevationRequirement is ElevationRequirement.ElevationRequired or ElevationRequirement.ElevatesSelf,
                        installer.ElevationRequirement == ElevationRequirement.ElevationProhibited);
                }, token).ConfigureAwait(false);
                if (resolved is not null) return resolved;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { PackageDiagnostics.Write($"ELEVATION preflight COM: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
        try
        {
            var manifest = await _apiService.GetPackageAsync(id, token, source).ConfigureAwait(false);
            var catalogInstaller = manifest is null ? null : WinProvisionApiService.PickInstaller(manifest);
            return catalogInstaller is null ? null : new InstallerPreferences(catalogInstaller.Scope, catalogInstaller.Architecture,
                catalogInstaller.ElevationRequirement is "elevationRequired" or "elevatesSelf",
                catalogInstaller.ElevationRequirement == "elevationProhibited");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            PackageDiagnostics.Write($"ELEVATION preflight catálogo: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return null;
        }
    }
}
