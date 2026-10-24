using CommunityToolkit.Mvvm.ComponentModel;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProvisioningWallpaperOption(string id, string fileName, string url, long sizeBytes, string? etag, bool isLocalChoice = false) : ObservableObject
{
    public string Id { get; } = id;
    public string FileName { get; } = fileName;
    public string Description => IsLocalChoice ? "Usar uma imagem deste computador" : $"{SizeBytes / 1024 / 1024} MB";
    public string Url { get; } = url;
    public long SizeBytes { get; } = sizeBytes;
    public string? ETag { get; } = etag;
    public bool IsLocalChoice { get; } = isLocalChoice;
    [ObservableProperty] public partial bool IsSelected { get; set; }
}
