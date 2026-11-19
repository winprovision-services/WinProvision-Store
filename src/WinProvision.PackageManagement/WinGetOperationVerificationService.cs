using System.Diagnostics;
using System.Runtime.InteropServices;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

internal sealed class WinGetOperationVerificationService
{
    private readonly WingetExecutor _wingetExecutor;
    private readonly WinGetUpdateDiscoveryService _discovery;
    private readonly Action<string> _diagnostic;

    internal WinGetOperationVerificationService(WingetExecutor wingetExecutor, WinGetUpdateDiscoveryService discovery,
        Action<string> diagnostic)
    {
        _wingetExecutor = wingetExecutor;
        _discovery = discovery;
        _diagnostic = diagnostic;
    }

    internal async Task<WingetExecutionResult> VerifyInstallResultAsync(string packageId, string source,
        string? requestedVersion, WingetExecutionResult result, CancellationToken cancellationToken)
    {
        if (result.InstallationVerified || result.IsUnconfirmed || (!result.Success && !result.InstallerStarted))
            return result;

        string effectiveSource = result.EffectiveSource ?? source;
        bool installerReportedSuccess = result.Success;
        string? lastError = null;
        List<UpgradablePackage>? lastInventory = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                lastInventory = await _wingetExecutor.ListInstalledPackagesAsync(timeout.Token, effectiveSource)
                    .ConfigureAwait(false);
                var matchingPackages = lastInventory.Where(item =>
                    item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                    && item.Source.Equals(effectiveSource, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (installerReportedSuccess && matchingPackages.Length == 1
                    && (string.IsNullOrWhiteSpace(requestedVersion)
                        || VersionsMatch(matchingPackages[0].CurrentVersion, requestedVersion)))
                    return ConfirmInstallResult(packageId, effectiveSource, requestedVersion, result, lastInventory);
                lastError = matchingPackages.Length > 1
                    ? $"O inventário retornou mais de uma entrada para {packageId}."
                    : matchingPackages.Length == 1 && !string.IsNullOrWhiteSpace(requestedVersion)
                        ? $"A versão encontrada ({matchingPackages[0].CurrentVersion}) difere da versão solicitada ({requestedVersion})."
                        : $"O pacote {packageId} ainda não foi localizado na origem {effectiveSource}.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = $"Não foi possível consultar o inventário após a instalação ({exception.GetType().Name}).";
                _diagnostic($"INSTALL VERIFY falhou packageId=\"{packageId}\" source=\"{effectiveSource}\": {exception}");
            }

            if (attempt < 2)
                await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 250 : 750), cancellationToken)
                    .ConfigureAwait(false);
        }

        if (lastInventory is not null)
            return ConfirmInstallResult(packageId, effectiveSource, requestedVersion, result, lastInventory);
        if (installerReportedSuccess || result.InstallerStarted)
            return MarkInstallUnconfirmed(result, lastError
                ?? "O inventário não confirmou a identidade e a versão do pacote.");
        return result;
    }

    internal static WingetExecutionResult ConfirmInstallResult(string packageId, string source,
        string? requestedVersion, WingetExecutionResult result, IReadOnlyList<UpgradablePackage> installed)
    {
        source = result.EffectiveSource ?? source;
        var matches = installed.Where(item =>
            item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)
            && item.Source.Equals(source, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1
            && (string.IsNullOrWhiteSpace(requestedVersion)
                || VersionsMatch(matches[0].CurrentVersion, requestedVersion)))
        {
            if (!result.Success)
                return MarkInstallUnconfirmed(result,
                    "O WinGet informou falha, mas o pacote aparece no inventário; o resultado da operação é inconclusivo.");
            result.InstallationVerified = true;
            result.VerificationStatus = OperationVerificationStatus.Confirmed;
            return result;
        }

        if (!result.Success && matches.Length == 0 && !result.InstallerStarted)
            return result;

        string observed = matches.Length switch
        {
            0 => "não encontrado",
            1 when !string.IsNullOrWhiteSpace(requestedVersion) => $"versão {matches[0].CurrentVersion}",
            1 => matches[0].CurrentVersion,
            _ => "identidade ambígua"
        };
        return MarkInstallUnconfirmed(result,
            $"A instalação não foi confirmada para {packageId} (esperado: {requestedVersion ?? "qualquer versão"}; encontrado: {observed}).");
    }

    private static WingetExecutionResult MarkInstallUnconfirmed(WingetExecutionResult result, string reason)
    {
        string originalOutput = result.Output;
        result.Success = false;
        result.VerificationStatus = OperationVerificationStatus.Unconfirmed;
        result.FailureReason = WingetFailureReason.Unknown;
        result.Output = string.IsNullOrWhiteSpace(originalOutput)
            ? $"Resultado não confirmado. {reason}"
            : $"Resultado não confirmado. {reason}{Environment.NewLine}{originalOutput}";
        return result;
    }

    internal static UpgradablePackage? ResolveCurrentUpdateInstallation(UpgradablePackage offered,
        IReadOnlyList<UpgradablePackage> inventory)
    {
        bool SameInstallation(UpgradablePackage item) =>
            (offered.InstalledScope is null || offered.InstalledScope.Equals(item.InstalledScope, StringComparison.OrdinalIgnoreCase))
            && (offered.InstalledArchitecture is null || offered.InstalledArchitecture.Equals(item.InstalledArchitecture, StringComparison.OrdinalIgnoreCase));
        string identity = offered.InstalledRegistryId ?? offered.Id;
        var exact = inventory.Where(item => SameInstallation(item)
            && ((item.InstalledRegistryId ?? item.Id).Equals(identity, StringComparison.OrdinalIgnoreCase)
                || item.Id.Equals(offered.Id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count > 0) return SelectUpdateInstallation(exact);

        string publisher = offered.InstalledPublisher ?? offered.Publisher;
        string name = NormalizeInstalledName(offered.Name, offered.CurrentVersion);
        var byName = inventory.Where(item => SameInstallation(item)
            && NormalizeInstalledName(item.Name, item.CurrentVersion) == name
            && (string.IsNullOrWhiteSpace(publisher) || string.IsNullOrWhiteSpace(item.Publisher)
                || NormalizeForMatching(item.Publisher) == NormalizeForMatching(publisher))).ToList();
        return SelectUpdateInstallation(byName);
    }

    internal static bool IsUpdateSatisfied(string installedVersion, string targetVersion) =>
        TryParseNumericVersion(installedVersion, out var installed)
        && TryParseNumericVersion(targetVersion, out var target)
        && CompareNumericVersions(installed, target) >= 0;

    private static bool TryParseNumericVersion(string? value, out int[] parts)
    {
        parts = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] tokens = value.Trim().TrimStart('v', 'V').Split('.');
        if (tokens.Length < 2 || tokens.Any(token => token.Length == 0 || !token.All(char.IsAsciiDigit))) return false;
        try { parts = tokens.Select(token => int.Parse(token, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); return true; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static int CompareNumericVersions(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        for (int i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            int comparison = (i < left.Count ? left[i] : 0).CompareTo(i < right.Count ? right[i] : 0);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static string NormalizeInstalledName(string name, string? version)
    {
        string normalized = NormalizeForMatching(name);
        string major = version?.Split('.')[0] ?? string.Empty;
        return major.Length > 0 && major.All(char.IsAsciiDigit)
            && normalized.EndsWith(" " + major, StringComparison.Ordinal)
            ? normalized[..^(major.Length + 1)] : normalized;
    }

    private static string NormalizeForMatching(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string normalized = System.Text.RegularExpressions.Regex.Replace(text.Trim(),
            @"\s*\((?:64-bit|32-bit|x64|x86|arm64|arm|x64\s+pt-BR|x86\s+pt-BR|pt-BR|en-US)[^)]*\)", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized,
            @"(?:\s+|-)(?:64-bit|32-bit|x64|x86|arm64|arm)$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized,
            @"\s+(?:version|v)?\s*\d+(?:\.\d+)+.*$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var chars = normalized.Where(character => char.IsLetterOrDigit(character)
            || char.IsWhiteSpace(character) || character is '+' or '#' or '-').ToArray();
        return string.Join(" ", new string(chars).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static UpgradablePackage? SelectUpdateInstallation(IReadOnlyList<UpgradablePackage> registrations)
    {
        if (registrations.Count == 1) return registrations[0];
        if (registrations.Count == 0 || registrations.Any(item => string.IsNullOrWhiteSpace(item.InstalledArchitecture)
                || string.IsNullOrWhiteSpace(item.InstalledScope))
            || registrations.Select(item => item.InstalledScope).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1
            || registrations.Select(item => item.InstalledArchitecture).Distinct(StringComparer.OrdinalIgnoreCase).Count() != registrations.Count)
            return null;
        string native = RuntimeInformation.OSArchitecture.ToString();
        var preferred = registrations.SingleOrDefault(item => native.Equals(item.InstalledArchitecture, StringComparison.OrdinalIgnoreCase));
        if (preferred is not null) return preferred;
        var latest = registrations.OrderByDescending(item => Version.TryParse(item.CurrentVersion, out var version) ? version : new Version(0, 0)).ToArray();
        return latest.Length > 0 ? latest[0] : null;
    }

    private static bool IsStrictlyNewerNumericVersion(string? current, string? target) =>
        TryParseNumericVersion(current, out var installed) && TryParseNumericVersion(target, out var offered)
        && CompareNumericVersions(installed, offered) > 0;

    internal async Task<WingetExecutionResult> VerifySuccessfulUpgradeAsync(
        UpgradablePackage offered, WingetExecutionResult result, CancellationToken cancellationToken)
    {
        if (!result.Success || result.UpdateAlreadySatisfied || result.UpdateVersionVerified) return result;
        if (result.RebootRequired)
            return MarkUpdateUnverified(result, "A atualização aguarda reinicialização; a versão instalada ainda não foi confirmada.");
        // Conte sucessos reportados pelo motor mesmo quando a conferência abaixo revela
        // que a versão não mudou. Sem isso, um motor que retorna sucesso sem atualizar
        // poderia ser acionado indefinidamente a cada tentativa do usuário.
        _discovery.RecordSuccessfulUpgrade(offered, result);
        if (string.IsNullOrWhiteSpace(offered.AvailableVersion))
            return MarkUpdateUnverified(result, "A operação terminou, mas não havia uma versão-alvo conhecida para conferir.");

        try
        {
            // A listagem é uma consulta nova pelo processo CLI. Não usamos o objeto COM
            // da operação nem o inventário já carregado, que podem refletir cache anterior.
            using var verifyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            verifyTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            var installed = await _wingetExecutor.ListInstalledPackagesAsync(
                verifyTimeout.Token, WinGetUpdateDiscoveryService.NormalizeUpdateSource(offered.Source)).ConfigureAwait(false);
            result = ConfirmUpgradeResult(offered, result, installed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MarkUpdateUnverified(result,
                $"Não foi possível confirmar a versão instalada ({exception.GetType().Name}).");
        }

        return result;
    }

    internal static WingetExecutionResult ConfirmApiUpgradeResult(UpgradablePackage offered,
        WingetExecutionResult result, IReadOnlyList<UpgradablePackage> inventory)
    {
        if (HasConfirmedApiVersion(offered, result, inventory))
        {
            result.UpdateVersionVerified = true;
            return result;
        }
        string actual = string.Join(", ", inventory.Where(item =>
            NormalizeInstalledName(item.Name, item.CurrentVersion)
                == NormalizeInstalledName(offered.Name, offered.CurrentVersion))
            .Select(item => $"{item.CurrentVersion} ({item.InstalledScope ?? "escopo desconhecido"}, {item.InstalledArchitecture ?? "arquitetura desconhecida"})"));
        return MarkUpdateUnverified(result,
            $"O inventário do Windows não confirmou uma instalação única na versão {offered.AvailableVersion} ou superior. Encontrada: {(actual.Length == 0 ? "não localizada" : actual)}.");
    }

    private static bool HasConfirmedApiVersion(UpgradablePackage offered, WingetExecutionResult result,
        IReadOnlyList<UpgradablePackage> inventory)
    {
        string identity = offered.InstalledRegistryId ?? offered.Id;
        var matches = inventory.Where(item =>
            (item.InstalledRegistryId ?? item.Id).Equals(identity, StringComparison.OrdinalIgnoreCase)
            && (offered.InstalledScope is null || item.InstalledScope == offered.InstalledScope)).ToArray();
        if (matches.Length == 0 && !string.IsNullOrWhiteSpace(result.ApiUpdateProductCode))
        {
            matches = inventory.Where(item =>
                item.Id.Equals(result.ApiUpdateProductCode, StringComparison.OrdinalIgnoreCase)
                && (result.ApiUpdateProductCodeFromInstaller || offered.InstalledScope is null || item.InstalledScope == offered.InstalledScope)
                && (offered.InstalledArchitecture is null || item.InstalledArchitecture == offered.InstalledArchitecture)).ToArray();
        }
        // MSI major upgrades can replace the ProductCode/uninstall key.
        if (matches.Length == 0)
            matches = inventory.Where(item =>
                NormalizeInstalledName(item.Name, item.CurrentVersion) == NormalizeInstalledName(offered.Name, offered.CurrentVersion)
                && (offered.InstalledScope is null || item.InstalledScope == offered.InstalledScope)
                && (offered.InstalledArchitecture is null || item.InstalledArchitecture == offered.InstalledArchitecture)
                && (string.IsNullOrWhiteSpace(offered.InstalledPublisher ?? offered.Publisher) || string.IsNullOrWhiteSpace(item.Publisher)
                    || NormalizeForMatching(item.Publisher) == NormalizeForMatching(offered.InstalledPublisher ?? offered.Publisher))).ToArray();
        return matches.Length == 1 && IsUpdateSatisfied(matches[0].CurrentVersion, offered.AvailableVersion);
    }

    internal static WingetExecutionResult ConfirmUpgradeResult(UpgradablePackage offered,
        WingetExecutionResult result, IReadOnlyList<UpgradablePackage> installed)
    {
        string source = WinGetUpdateDiscoveryService.NormalizeUpdateSource(offered.Source);
        var matches = installed.Where(item =>
            item.Id.Equals(offered.Id, StringComparison.OrdinalIgnoreCase)
            && WinGetUpdateDiscoveryService.NormalizeUpdateSource(item.Source).Equals(source, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1 && VersionsMatch(matches[0].CurrentVersion, offered.AvailableVersion))
        {
            result = ConfirmKnownRuntimeVersion(offered, result);
            result.UpdateVersionVerified = result.Success;
            return result;
        }

        string actual = matches.Length switch
        {
            0 => "não encontrado",
            1 => matches[0].CurrentVersion,
            _ => "identidade ambígua no inventário"
        };
        return MarkUpdateUnverified(result,
            $"A versão instalada não corresponde ao alvo {offered.AvailableVersion} (encontrada: {actual}).");
    }

    private static WingetExecutionResult ConfirmKnownRuntimeVersion(
        UpgradablePackage offered, WingetExecutionResult result)
    {
        if (!offered.Id.Equals("Microsoft.PowerShell", StringComparison.OrdinalIgnoreCase))
            return result;

        string executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(executable)) return result;
        try
        {
            var file = FileVersionInfo.GetVersionInfo(executable);
            string runtimeVersion = $"{file.FileMajorPart}.{file.FileMinorPart}.{file.FileBuildPart}";
            if (RuntimeVersionMatches(runtimeVersion, offered.AvailableVersion)) return result;
            return MarkUpdateUnverified(result,
                $"O executável principal ainda informa {runtimeVersion}, embora o inventário informe {offered.AvailableVersion}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return MarkUpdateUnverified(result,
                $"Não foi possível conferir o executável principal ({exception.GetType().Name}).");
        }
    }

    internal static bool RuntimeVersionMatches(string? actual, string? target)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(target)) return false;
        if (!TryNormalizeVersion(actual, out var normalizedActual)
            || !TryNormalizeVersion(target, out var normalizedTarget)) return false;
        return normalizedActual.Major == normalizedTarget.Major
            && normalizedActual.Minor == normalizedTarget.Minor
            && normalizedActual.Build == normalizedTarget.Build;
    }

    internal static WingetExecutionResult MarkUpdateUnverified(WingetExecutionResult result, string reason)
    {
        result.Success = false;
        result.InstallerStarted = true;
        result.VerificationStatus = OperationVerificationStatus.Unconfirmed;
        result.FailureReason = WingetFailureReason.Unknown;
        result.Output = $"A atualização foi executada, mas não foi confirmada. {reason}";
        return result;
    }

    internal static bool VersionsMatch(string? actual, string? target)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(target)) return false;
        actual = actual.Trim().TrimStart('v', 'V');
        target = target.Trim().TrimStart('v', 'V');
        if (IsVersionPlaceholder(actual) || IsVersionPlaceholder(target)) return false;
        if (TryNormalizeVersion(actual, out var normalizedActual)
            && TryNormalizeVersion(target, out var normalizedTarget))
            return normalizedActual.Equals(normalizedTarget);
        return actual.Equals(target, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVersionPlaceholder(string value) => value.Trim() is "-" or "?"
        || value.Contains("unknown", StringComparison.OrdinalIgnoreCase)
        || value.Contains("desconhec", StringComparison.OrdinalIgnoreCase)
        || value.Equals("n/a", StringComparison.OrdinalIgnoreCase)
        || value.Equals("na", StringComparison.OrdinalIgnoreCase)
        || value.Contains("not available", StringComparison.OrdinalIgnoreCase)
        || value.Contains("indispon", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeVersion(string value, out Version version)
    {
        version = null!;
        string[] parts = value.Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
            return false;
        string normalized = string.Join('.', parts.Concat(Enumerable.Repeat("0", 4 - parts.Length)));
        if (!Version.TryParse(normalized, out var parsed)) return false;
        version = parsed;
        return true;
    }
}
