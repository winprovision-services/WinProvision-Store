using System.Text;

namespace WinProvision.Store.Services;

public interface IOfficeConfigurationExportService
{
    Task<string?> ExportAsync(string xml, CancellationToken cancellationToken = default);
}

public sealed partial class OfficeConfigurationExportService(IPresentationService presentation) : IOfficeConfigurationExportService
{
    public async Task<string?> ExportAsync(string xml, CancellationToken cancellationToken = default)
    {
        string? path = await presentation.PickSaveAsync("configuration.xml", ".xml");
        if (path is null)
            return null;
        await File.WriteAllTextAsync(path, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
        return path;
    }
}
