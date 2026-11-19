using WinProvision.Core.Models;
using WinProvision.Store.Services;

namespace WinProvision.Store;

internal sealed class FakePackageManagerSettingsAdapter : IPackageManagerSettingsAdapter
{
    private string _value = "original";
    private string? _pending;
    public string? AppliedValue { get; private set; }
    public bool Enabled { get; private set; } = true;
    public PackageManagerDescriptor Definition { get; }
    public FakePackageManagerSettingsAdapter(PackageManagerDescriptor? definition = null) =>
        Definition = definition ?? new PackageManagerDescriptor
    {
        Id = "test.fake",
        Source = "test.fake",
        DisplayName = "Adaptador falso",
        Description = "Gerenciador criado só para o teste de extensibilidade.",
        ShowInSettings = true,
        CanToggle = true,
        HasDetailsPage = true,
        SortOrder = 99,
        Check = new PackageManagerCheckDefinition { Kind = "none" }
    };

    public Task<PackageManagerSettingsDescriptor> GetDescriptorAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PackageManagerSettingsDescriptor
        {
            Id = Definition.Id,
            DisplayName = Definition.DisplayName,
            Description = Definition.Description,
            Enabled = Enabled,
            Status = new PackageManagerSettingsStatus { State = "ready", Message = "Disponível", Version = "1.0", Executable = "fake.exe" },
            Fields = new[] { new PackageManagerSettingsField { Id = "value", Type = "text", Section = "install", Label = "Valor de teste", Value = _value, DefaultValue = "original" } },
            Sources = new PackageManagerSettingsSources { Supported = false },
            Actions = new PackageManagerSettingsActions { Supported = false }
        });
    }

    public Task CheckAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void SetEnabled(bool enabled) => Enabled = enabled;
    public void SetField(string fieldId, string value)
    {
        if (fieldId != "value") throw new KeyNotFoundException(fieldId);
        _pending = value;
    }
    public Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _value = _pending ?? _value;
        AppliedValue = _value;
        _pending = null;
        return Task.CompletedTask;
    }
    public Task ResetAsync(CancellationToken cancellationToken = default) { _pending = null; _value = "original"; return Task.CompletedTask; }
    public Task<IReadOnlyList<PackageManagerSettingsSource>> ListSourcesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PackageManagerSettingsSource>>(Array.Empty<PackageManagerSettingsSource>());
    public Task AddSourceAsync(string name, string url, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ResetSourcesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<string?> ExecuteActionAsync(string actionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
