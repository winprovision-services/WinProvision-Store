using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinProvision.Core.Models;
namespace WinProvision.Store.Services;
public interface IPresentationService
{
    Task<string?> PickOpenAsync();
    Task<string?> PickSaveAsync(string name, string? extension = null);
    Task<string?> PickFolderAsync();
    Task<bool> ConfirmAsync(string title, string message, string primaryButtonText = "Confirmar", string closeButtonText = "Cancelar");
    Task ShowDetailsAsync(AppEntry app, string? currentVersion = null, string? availableVersion = null);
}
public sealed partial class PresentationService : IPresentationService
{
    public XamlRoot? Root { get; set; }
    public nint WindowHandle { get; set; }
    public Func<AppEntry, string?, string?, Task>? DetailsPresenter { get; set; }
    private readonly SemaphoreSlim _dialogs = new(1, 1);
    private ContentDialog? _activeDialog;
    public void CancelDialogs() => _activeDialog?.Hide();
    public async Task<string?> PickOpenAsync()
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add(".json");
        return (await picker.PickSingleFileAsync())?.Path;
    }
    public async Task<string?> PickSaveAsync(string name, string? extension = null)
    {
        var picker = new FileSavePicker { SuggestedFileName = name };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        extension ??= Path.GetExtension(name);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".json";
        if (!extension.StartsWith(".", StringComparison.Ordinal))
            extension = "." + extension;
        string description = extension.ToLowerInvariant() switch
        {
            ".csv" => "Arquivo CSV",
            ".ps1" => "Script PowerShell",
            ".zip" => "Arquivo ZIP",
            _ => "Perfil JSON"
        };
        picker.FileTypeChoices.Add(description, [extension]);
        return (await picker.PickSaveFileAsync())?.Path;
    }
    public async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText = "Confirmar", string closeButtonText = "Cancelar")
    {
        await _dialogs.WaitAsync();
        try
        {
            var dialog = new ContentDialog { XamlRoot = Root, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, PrimaryButtonText = primaryButtonText, CloseButtonText = closeButtonText, DefaultButton = ContentDialogButton.Close };
            _activeDialog = dialog;
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _activeDialog = null; _dialogs.Release(); }
    }
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    public Task ShowDetailsAsync(AppEntry app, string? currentVersion = null, string? availableVersion = null) => DetailsPresenter?.Invoke(app, currentVersion, availableVersion) ?? throw new InvalidOperationException("A janela de detalhes ainda não está pronta.");
}
