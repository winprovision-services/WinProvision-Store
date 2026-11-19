using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;

namespace WinProvision.Store.Services;

public interface IOfficeCollectionInstaller
{
    Task<bool> InstallAsync(OfficeInstallRequest request, CancellationToken cancellationToken);
}

public sealed partial class OfficeCollectionInstaller(
    OperationsQueueService queue,
    OfficeDeploymentToolService officeService) : IOfficeCollectionInstaller
{
    public Task<bool> InstallAsync(OfficeInstallRequest request, CancellationToken cancellationToken) =>
        OperationRunner.RunOfficeInstallAsync(queue, officeService, request, cancellationToken);
}
