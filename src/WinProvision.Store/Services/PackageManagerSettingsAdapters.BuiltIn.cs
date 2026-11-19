using System.Diagnostics;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public abstract class PackageManagerSettingsAdapterBase : IPackageManagerSettingsAdapter
{
    protected readonly ApplicationPreferencesService Preferences;
    protected readonly PackageInstallOptionsService InstallOptions;
    protected readonly Dictionary<string, string> Draft = new(StringComparer.Ordinal);
    protected PackageManagerSettingsStatus Status { get; set; } = new();
    public PackageManagerDescriptor Definition { get; }
    protected PackageManagerSettingsAdapterBase(string id, string displayName, string description, int sortOrder,
        ApplicationPreferencesService preferences, PackageInstallOptionsService installOptions)
    {
        Preferences = preferences; InstallOptions = installOptions;
        PackageManagerDescriptor catalogDefinition = PackageManagerCatalog.Default.GetDescriptor(id);
        Definition = new PackageManagerDescriptor
        {
            Id = id, Source = catalogDefinition.Source.Length == 0 ? id : catalogDefinition.Source,
            DisplayName = string.IsNullOrWhiteSpace(catalogDefinition.DisplayName) ? displayName : catalogDefinition.DisplayName,
            Description = description, DisplayNameKey = catalogDefinition.DisplayNameKey,
            DescriptionKey = catalogDefinition.DescriptionKey, IconKey = catalogDefinition.IconKey,
            IconGlyph = catalogDefinition.IconGlyph, IconUri = catalogDefinition.IconUri,
            ParentId = catalogDefinition.ParentId,
            ShowInSettings = true, CanToggle = true, HasDetailsPage = catalogDefinition.HasDetailsPage, SortOrder = sortOrder,
            Check = catalogDefinition.Check
        };
    }

    public abstract Task<PackageManagerSettingsDescriptor> GetDescriptorAsync(CancellationToken cancellationToken = default);
    public abstract Task CheckAsync(CancellationToken cancellationToken = default);
    public virtual Task CheckAsync(IProgress<string>? progress, CancellationToken cancellationToken = default) => CheckAsync(cancellationToken);
    public virtual void SetEnabled(bool enabled) => Preferences.SetPackageManagerEnabled(Definition.Id, enabled);
    public virtual Task SetEnabledAsync(bool enabled, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SetEnabled(enabled);
        return Task.CompletedTask;
    }
    public abstract void SetField(string fieldId, string value);
    public abstract Task ApplyAsync(CancellationToken cancellationToken = default);
    public abstract Task ResetAsync(CancellationToken cancellationToken = default);
    public virtual Task<IReadOnlyList<PackageManagerSettingsSource>> ListSourcesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageManagerSettingsSource>>([]);
    public virtual Task AddSourceAsync(string name, string url, CancellationToken cancellationToken = default) => throw new NotSupportedException("Este gerenciador não oferece fontes configuráveis.");
    public virtual Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException("Este gerenciador não oferece fontes configuráveis.");
    public virtual Task ResetSourcesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Este gerenciador não permite redefinir fontes.");
    public virtual Task<string?> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default) => throw new NotSupportedException($"A ação '{actionId}' não é oferecida por este gerenciador.");

    public bool Enabled => Preferences.GetPackageManagerEnabled(Definition.Id);
    protected PackageManagerSettingsDescriptor Descriptor(IReadOnlyList<PackageManagerSettingsField> fields,
        PackageManagerSettingsSources? sources = null, PackageManagerSettingsActions? actions = null,
        IReadOnlyList<PackageManagerSettingsNote>? notes = null) => new()
    {
        Id = Definition.Id, DisplayName = Definition.DisplayName, Description = Definition.Description,
        Enabled = Enabled, Status = Status, Fields = fields,
        Sources = sources ?? new PackageManagerSettingsSources(),
        Actions = actions ?? new PackageManagerSettingsActions(), Notes = notes ?? Array.Empty<PackageManagerSettingsNote>()
    };

    protected PackageManagerSettingsField Toggle(string id, string label, bool value, string section = "advanced",
        bool defaultValue = false, string? description = null) =>
        new() { Id = id, Type = "toggle", Section = section, Label = label, Description = description,
            Value = Read(id, value.ToString()), DefaultValue = defaultValue.ToString() };
    protected PackageManagerSettingsField Text(string id, string label, string? value, string placeholder,
        string? defaultValue = "", string? description = null) =>
        new() { Id = id, Type = "text", Section = "install", Label = label, Description = description,
            Value = Read(id, value ?? string.Empty), DefaultValue = defaultValue, Placeholder = placeholder };
    protected PackageManagerSettingsField PathField(string id, string label, string? value, string pathKind,
        string placeholder, string section = "install", string? defaultValue = "", string? description = null) =>
        new() { Id = id, Type = "path", PathKind = pathKind, Section = section, Label = label,
            Description = description, Value = Read(id, value ?? string.Empty), DefaultValue = defaultValue,
            Placeholder = placeholder };
    protected PackageManagerSettingsField Choice(string id, string label, string? value,
        IReadOnlyList<PackageManagerSettingsChoice> choices, string section = "install",
        string? description = null, string? defaultValue = null) =>
        new() { Id = id, Type = "choice", Section = section, Label = label, Description = description,
            Value = Read(id, value ?? string.Empty), DefaultValue = defaultValue, Choices = choices };
    protected string Read(string id, string fallback) => Draft.TryGetValue(id, out string? value) ? value : fallback;
    protected static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class WingetPackageManagerSettingsAdapter : PackageManagerSettingsAdapterBase
{
    private readonly WingetBootstrapper _winget;
    private readonly WingetSourcesService _sources;
    private readonly InstallationPreferencesService _installation;
    private readonly IAppLog _appLog;
    private readonly AppLogTaskExecutor? _taskExecutor;
    private readonly WingetExecutor _wingetExecutor;
    private DateTimeOffset? _checkedAt;

    public WingetPackageManagerSettingsAdapter(WingetBootstrapper winget, WingetSourcesService sources,
        InstallationPreferencesService installation, ApplicationPreferencesService preferences, PackageInstallOptionsService options,
        IAppLog? appLog = null, AppLogTaskExecutor? taskExecutor = null, WingetExecutor? wingetExecutor = null)
        : base("winget", "WinGet", "Gerenciador principal do WinProvision Store", 0, preferences, options)
    { _winget = winget; _sources = sources; _installation = installation; _appLog = appLog ?? AppLogRuntime.Log; _taskExecutor = taskExecutor; _wingetExecutor = wingetExecutor ?? new WingetExecutor(); }

    private static readonly string DefaultLabel = PackageManagerCatalog.Default.GetText("managerPreferences.default", "Padrão");
    private static readonly IReadOnlyList<PackageManagerSettingsChoice> Architectures = new PackageManagerSettingsChoice[] { new("default", DefaultLabel), new("x86", "x86"), new("x64", "x64"), new("arm", "ARM"), new("arm64", "ARM64") };
    private static readonly IReadOnlyList<PackageManagerSettingsChoice> Scopes = new PackageManagerSettingsChoice[] { new("default", DefaultLabel), new("user", "Usuário"), new("machine", "Computador") };
    private static readonly IReadOnlyList<PackageManagerSettingsChoice> ComModes = new PackageManagerSettingsChoice[] { new("automatic", DefaultLabel), new("com", "Usar API COM"), new("cli", "Usar linha de comando (CLI)") };

    public override async Task<PackageManagerSettingsDescriptor> GetDescriptorAsync(CancellationToken cancellationToken = default)
    {
        PackageInstallOptions value = InstallOptions.GetGlobalDefaults();
        IReadOnlyList<PackageManagerSettingsField> fields = new PackageManagerSettingsField[]
        {
            Toggle("requiresElevation", "Executar como administrador", value.RequiresElevation, "install", false),
            Toggle("interactive", "Instalação interativa", value.Interactive, "install", false),
            Toggle("allowPrerelease", "Permitir versões de pré-lançamento", value.AllowPrerelease, "install", false),
            Toggle("uninstallPrevious", "Desinstalar versão anterior ao atualizar", value.UninstallPrevious, "install", false),
            Choice("architecture", "Arquitetura para instalar", value.Architecture ?? "default", Architectures, defaultValue: "default"),
            Choice("scope", "Escopo da instalação", value.Scope ?? "default", Scopes, defaultValue: "default"),
            PathField("installLocation", "Local de instalação", value.InstallLocation, "folder",
                PackageManagerCatalog.Default.GetText("managerPreferences.defaultPath", "Padrão do pacote")),
            Text("customArguments", "Argumentos de instalação personalizados", value.CustomArguments, "Argumentos do instalador", description: "Vazio por padrão; use apenas argumentos confiáveis. São enviados à linha de comando do WinGet."),
            Text("updateArguments", "Argumentos de atualização personalizados", value.UpdateArguments, "Argumentos do WinGet", description: "Vazio por padrão; use apenas argumentos confiáveis. São enviados à linha de comando do WinGet."),
            Toggle("ignoreUpdateNotifications", "Ignorar pacotes nas notificações de atualização", Preferences.IgnoreWinGetUpdateNotifications, defaultValue: false),
            Choice("comApi", "API COM do WinGet", LoadComApiPolicy(), ComModes, "advanced",
                "No modo padrão, o app escolhe automaticamente entre a API COM e a linha de comando quando necessário.", "automatic"),
            Toggle("forceInstallLocation", "Forçar o local salvo ao atualizar", Preferences.ForceWinGetInstallLocationOnUpdate, defaultValue: false)
        };
        return Descriptor(fields, new PackageManagerSettingsSources { Supported = true, CanAdd = true, CanRemove = true, CanRefresh = true, CanReset = true },
            new PackageManagerSettingsActions { Supported = true, Items = new[]
            {
                new PackageManagerSettingsAction("openLogs", "Visualizar registros", "link"),
                new PackageManagerSettingsAction("resetSources", "Redefinir fontes", "button", Confirm: true, Section: "sources")
            } },
            new[] { new PackageManagerSettingsNote("info", "O modo interativo exige um terminal. A elevação pode solicitar confirmação do Windows.") })
            with { Status = Status with { CheckedAt = _checkedAt } };
    }

    public override async Task CheckAsync(CancellationToken cancellationToken = default)
        => await CheckAsync(null, cancellationToken);

    public override async Task CheckAsync(IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        progress?.Report("Verificando se o WinGet está disponível…");
        WingetBootstrapResult bootstrap = await _winget.EnsureOnceAsync(line => progress?.Report(line), cancellationToken,
            retryFailureImmediately: true);
        if (!bootstrap.IsUsable)
        {
            string message = string.IsNullOrWhiteSpace(bootstrap.ErrorMessage)
                ? "Não foi possível provisionar o WinGet. Verifique a conexão e tente novamente."
                : $"Não foi possível provisionar o WinGet: {bootstrap.ErrorMessage}";
            Status = new() { State = "error", Message = message, Executable = WingetLocator.ExecutablePath };
            _checkedAt = DateTimeOffset.Now;
            return;
        }
        string version = await RunVersionAsync(cancellationToken);
        Status = new() { State = "ready", Message = "Disponível e pronto para ser usado", Version = version, Executable = WingetLocator.ExecutablePath, ExecutableEditable = false };
        _checkedAt = DateTimeOffset.Now;
    }

    public override void SetField(string fieldId, string value) => Draft[fieldId] = value;
    public override Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PackageInstallOptions previous = InstallOptions.GetGlobalDefaults();
        string com = Read("comApi", LoadComApiPolicy());
        PackageInstallOptions install = previous with
        {
            RequiresElevation = Bool("requiresElevation", previous.RequiresElevation), Interactive = Bool("interactive", previous.Interactive),
            AllowPrerelease = Bool("allowPrerelease", previous.AllowPrerelease), UninstallPrevious = Bool("uninstallPrevious", previous.UninstallPrevious), Architecture = OptionalChoice(Read("architecture", previous.Architecture ?? "default")),
            Scope = OptionalChoice(Read("scope", previous.Scope ?? "default")), InstallLocation = NullIfEmpty(Read("installLocation", previous.InstallLocation ?? "")),
            CustomArguments = NullIfEmpty(Read("customArguments", previous.CustomArguments ?? "")),
            UpdateArguments = NullIfEmpty(Read("updateArguments", previous.UpdateArguments ?? ""))
        };
        InstallOptions.SaveGlobalDefaults(install);
        Preferences.SetIgnoreWinGetUpdateNotifications(Bool("ignoreUpdateNotifications", Preferences.IgnoreWinGetUpdateNotifications));
        Preferences.SetForceWinGetInstallLocationOnUpdate(Bool("forceInstallLocation", Preferences.ForceWinGetInstallLocationOnUpdate));
        _installation.SetPreferredWinGetEngine(com == "com" ? WinGetEnginePreference.ComApi
            : com == "cli" ? WinGetEnginePreference.WingetCli : WinGetEnginePreference.Automatic);
        Draft.Clear(); return Task.CompletedTask;
    }
    public override Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstallOptions.SaveGlobalDefaults(new PackageInstallOptions());
        Preferences.SetIgnoreWinGetUpdateNotifications(false);
        Preferences.SetForceWinGetInstallLocationOnUpdate(false);
        _installation.SetPreferredWinGetEngine(WinGetEnginePreference.WingetCli);
        Draft.Clear(); return Task.CompletedTask;
    }
    public override async Task<IReadOnlyList<PackageManagerSettingsSource>> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        (await _sources.ListAsync(cancellationToken)).Select(ParseSource).ToArray();
    public override Task AddSourceAsync(string name, string url, CancellationToken cancellationToken = default) => _sources.AddAsync(name, url, cancellationToken);
    public override Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default) => _sources.RemoveAsync(sourceId, cancellationToken);
    public override async Task ResetSourcesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(["source", "reset", "--force"], cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
    }
    public override async Task<string?> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default) => actionId switch
    {
        "openLogs" => await PackageManagerLogReader.ReadAsync("winget", _appLog, cancellationToken),
        "resetSources" => await ResetAndReturnAsync(cancellationToken),
        _ => await base.ExecuteActionAsync(actionId, cancellationToken)
    };
    private async Task<string?> ResetAndReturnAsync(CancellationToken token) { await ResetSourcesAsync(token); return null; }
    private async Task<string> RunVersionAsync(CancellationToken token) { var r = await RunAsync(["--version"], token); return r.ExitCode == 0 ? r.Output.Trim() : "Versão não informada"; }
    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] args, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        WingetExecutionResult result = await _wingetExecutor.ExecuteCliAsync(args, null, timeout.Token).ConfigureAwait(false);
        return (result.ExitCode, result.StandardOutput, result.StandardError);
    }
    private static string ClassifyTask(string[] args) => args.FirstOrDefault()?.ToLowerInvariant() switch
    {
        "source" when args.Length > 1 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase) => "ListSources",
        "source" when args.Length > 1 && args[1].Equals("update", StringComparison.OrdinalIgnoreCase) => "UpdateSources",
        "search" or "find" => "Search",
        "list" when args.Any(argument => argument.Equals("--upgrade-available", StringComparison.OrdinalIgnoreCase)) => "ListUpdates",
        "list" => "ListInstalled",
        _ => "Other"
    };
    private string LoadComApiPolicy() => _installation.PreferredWinGetEngine switch
    {
        WinGetEnginePreference.ComApi => "com",
        WinGetEnginePreference.Automatic => "automatic",
        _ => "cli"
    };
    private bool Bool(string id, bool fallback) => bool.TryParse(Read(id, fallback.ToString()), out bool value) && value;
    private static string? OptionalChoice(string value) => value.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : NullIfEmpty(value);
    private static string SourceId(string line) => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? line.Trim();
    private static PackageManagerSettingsSource ParseSource(string line)
    {
        string[] parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string id = parts.FirstOrDefault() ?? line.Trim();
        string? url = parts.Length > 1 ? parts[1] : null;
        bool removable = !id.Equals("winget", StringComparison.OrdinalIgnoreCase) && !id.Equals("msstore", StringComparison.OrdinalIgnoreCase);
        return new PackageManagerSettingsSource(id, id, url, removable);
    }
}

public sealed class ChocolateyPackageManagerSettingsAdapter : PackageManagerSettingsAdapterBase
{
    private readonly ChocolateyService _chocolatey;
    private readonly IPresentationService? _presentation;
    private readonly IAppLog _appLog;
    private readonly AppLogTaskExecutor? _taskExecutor;
    private DateTimeOffset? _checkedAt;
    public ChocolateyPackageManagerSettingsAdapter(ChocolateyService chocolatey,
        ApplicationPreferencesService preferences, PackageInstallOptionsService options, IPresentationService? presentation = null,
        IAppLog? appLog = null, AppLogTaskExecutor? taskExecutor = null)
        : base("chocolatey", "Chocolatey", "Gerenciador de pacotes da comunidade", 2, preferences, options)
    { _chocolatey = chocolatey; _presentation = presentation; _appLog = appLog ?? AppLogRuntime.Log; _taskExecutor = taskExecutor; }

    public override async Task<PackageManagerSettingsDescriptor> GetDescriptorAsync(CancellationToken cancellationToken = default)
    {
        PackageInstallOptions value = InstallOptions.GetGlobalDefaults("chocolatey");
        var fields = new PackageManagerSettingsField[]
        {
            Toggle("requiresElevation", "Executar como administrador", value.RequiresElevation, "install", true),
            Toggle("interactive", "Instalação interativa", value.Interactive, "install", false),
            Toggle("ignoreChecksums", "Ignorar verificação de hash", value.IgnoreSecurityHash, "install", false,
                "O Chocolatey recebe --ignore-checksums nesta operação."),
            Toggle("allowPrerelease", "Permitir versões de pré-lançamento", value.AllowPrerelease, "install", false),
            Toggle("uninstallPrevious", "Desinstalar versão anterior ao atualizar", value.UninstallPrevious, "install", false),
            Choice("architecture", "Arquitetura para instalar", value.Architecture ?? "default",
                new[] { new PackageManagerSettingsChoice("default", PackageManagerCatalog.Default.GetText("managerPreferences.default", "Padrão")), new PackageManagerSettingsChoice("x86", "x86") }, defaultValue: "default"),
            Text("customArguments", "Argumentos de instalação personalizados", value.CustomArguments, "Argumentos do Chocolatey", description: "Vazio por padrão; use apenas argumentos confiáveis. São enviados ao comando de instalação."),
            Text("updateArguments", "Argumentos de atualização personalizados", value.UpdateArguments, "Argumentos do Chocolatey", description: "Vazio por padrão; use apenas argumentos confiáveis. São enviados ao comando de atualização."),
            Text("uninstallArguments", "Argumentos de desinstalação personalizados", value.UninstallArguments, "Argumentos do Chocolatey", description: "Vazio por padrão; use apenas argumentos confiáveis. São enviados ao comando de desinstalação."),
            PathField("executablePath", "Executável do Chocolatey", Preferences.ChocolateyExecutablePath, "file",
                "Detectado automaticamente", "advanced", description: "Vazio para usar a instalação encontrada no sistema.")
        };
        string? path = Preferences.ChocolateyExecutablePath;
        return Descriptor(fields, new PackageManagerSettingsSources { Supported = true, CanAdd = true, CanRemove = true, CanRefresh = true },
            new PackageManagerSettingsActions { Supported = true, Items = new[] { new PackageManagerSettingsAction("openLogs", "Visualizar registros", "link") } },
            new[] { new PackageManagerSettingsNote("info", "Chocolatey não expõe escopo ou pasta de instalação gerais."), new PackageManagerSettingsNote("info", "Notificações e idade mínima de atualização não estão disponíveis no fluxo Chocolatey atual.") }) with { Status = Status with { Executable = path ?? Status.Executable, ExecutableEditable = true, CheckedAt = _checkedAt } };
    }
    public override async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        try { var result = await _chocolatey.CheckStatusAsync(cancellationToken); Status = new() { State = result.Available ? "ready" : "unavailable", Message = result.Available ? "Disponível e pronto para ser usado" : "Não instalado", Version = result.Version, Executable = result.Path, ExecutableEditable = true }; }
        finally { _checkedAt = DateTimeOffset.Now; }
    }
    public override async Task SetEnabledAsync(bool enabled, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled)
        {
            SetEnabled(false);
            return;
        }

        if (_chocolatey.ExecutablePath is { } existing && File.Exists(existing))
        {
            string fullPath = Path.GetFullPath(existing);
            _chocolatey.SetExecutablePathOverride(fullPath);
            Preferences.SetChocolateyExecutablePath(fullPath);
            var existingStatus = await _chocolatey.CheckStatusAsync(cancellationToken);
            if (!existingStatus.Available)
                throw new InvalidOperationException($"Chocolatey não respondeu pelo caminho '{fullPath}'.");
            Status = new() { State = "ready", Message = "Disponível e pronto para ser usado", Version = existingStatus.Version, Executable = fullPath, ExecutableEditable = true };
            _checkedAt = DateTimeOffset.Now;
            SetEnabled(true);
            return;
        }

        if (_presentation is null)
            throw new InvalidOperationException("Não foi possível confirmar a instalação do Chocolatey nesta janela.");
        bool confirmed = await _presentation.ConfirmAsync("Instalar Chocolatey CLI?",
            "O Chocolatey será instalado para todo o computador e pode exigir autorização de administrador. Deseja continuar?",
            "Instalar Chocolatey", "Cancelar");
        if (!confirmed) throw new OperationCanceledException("Instalação do Chocolatey cancelada.", cancellationToken);

        progress?.Report("Baixando o instalador oficial do Chocolatey…");
        string installedPath = await _chocolatey.InstallCliAsync(cancellationToken,
            message => progress?.Report(message)).ConfigureAwait(false);
        _chocolatey.SetExecutablePathOverride(installedPath);
        Preferences.SetChocolateyExecutablePath(installedPath);
        var status = await _chocolatey.CheckStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Available)
            throw new InvalidOperationException($"Chocolatey foi instalado, mas não respondeu pelo caminho '{installedPath}'.");
        Status = new() { State = "ready", Message = "Disponível e pronto para ser usado", Version = status.Version, Executable = installedPath, ExecutableEditable = true };
        _checkedAt = DateTimeOffset.Now;
        SetEnabled(true);
    }
    public override void SetField(string fieldId, string value) => Draft[fieldId] = value;
    public override async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); PackageInstallOptions old = InstallOptions.GetGlobalDefaults("chocolatey");
        InstallOptions.SaveGlobalDefaults(old with
        {
            RequiresElevation = Bool("requiresElevation", old.RequiresElevation), Interactive = Bool("interactive", old.Interactive), IgnoreSecurityHash = Bool("ignoreChecksums", old.IgnoreSecurityHash),
            AllowPrerelease = Bool("allowPrerelease", old.AllowPrerelease), UninstallPrevious = Bool("uninstallPrevious", old.UninstallPrevious),
            Architecture = Read("architecture", old.Architecture ?? "default") == "default" ? null : Read("architecture", old.Architecture ?? "x86"),
            CustomArguments = NullIfEmpty(Read("customArguments", old.CustomArguments ?? "")), UpdateArguments = NullIfEmpty(Read("updateArguments", old.UpdateArguments ?? "")),
            UninstallArguments = NullIfEmpty(Read("uninstallArguments", old.UninstallArguments ?? ""))
        }, "chocolatey");
        string? executable = NullIfEmpty(Read("executablePath", Preferences.ChocolateyExecutablePath ?? string.Empty));
        Preferences.SetChocolateyExecutablePath(executable);
        _chocolatey.SetExecutablePathOverride(executable);
        Draft.Clear();
        await CheckAsync(cancellationToken);
    }
    public override async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstallOptions.SaveGlobalDefaults(new PackageInstallOptions(RequiresElevation: true), "chocolatey");
        Preferences.SetChocolateyExecutablePath(null);
        _chocolatey.SetExecutablePathOverride(null);
        Draft.Clear();
        await CheckAsync(cancellationToken);
    }
    public override async Task<IReadOnlyList<PackageManagerSettingsSource>> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        (await _chocolatey.ListSourcesAsync(cancellationToken)).Select(line =>
        {
            string[] parts = line.Split('|');
            string name = parts[0].Trim();
            string? url = parts.Length > 1 ? parts[1].Trim() : null;
            return new PackageManagerSettingsSource(name, name, url, true);
        }).ToArray();
    public override Task AddSourceAsync(string name, string url, CancellationToken cancellationToken = default) => _chocolatey.AddSourceAsync(name, url, cancellationToken);
    public override Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default) => _chocolatey.RemoveSourceAsync(sourceId, cancellationToken);
    public override async Task<string?> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default)
    {
        switch (actionId)
        {
            case "openLogs":
                return await PackageManagerLogReader.ReadAsync("chocolatey", _appLog, cancellationToken);
            default: return await base.ExecuteActionAsync(actionId, cancellationToken);
        }
    }
    private bool Bool(string id, bool fallback) => bool.TryParse(Read(id, fallback.ToString()), out bool value) && value;
}

