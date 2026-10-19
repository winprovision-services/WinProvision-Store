using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;
using WinProvision.Viper;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OfficeViperProgressTests
{
    [Fact]
    public void StructuredProgressUpdatesTheLineActuallyDisplayedByTheQueue()
    {
        using var item = new OperationItem("Office", OperationKind.Install) { State = OperationState.Running, LiveLine = "Preparando..." };
        OperationRunner.ReportInstallProgress(item, new(InstallProgressPhase.Downloading, 53.99, WingetMethod.OwnApi));
        Assert.False(item.IsIndeterminate);
        Assert.Equal(53.99, item.Progress);
        Assert.Equal(item.StatusText, item.LiveLine);
        Assert.Contains(53.99.ToString("0.00"), item.LiveLine);
        OperationRunner.ReportInstallProgress(item, new(InstallProgressPhase.Preparing));
        Assert.True(item.IsIndeterminate);
        Assert.Equal(item.StatusText, item.LiveLine);
        Assert.DoesNotContain("%", item.LiveLine);
    }

    [Fact]
    public void PercentageUsesBytesAcrossUnequalFilesAndKeepsVerificationSeparate()
    {
        var updates = new List<InstallProgressUpdate>();
        var progress = new OfficeViperProgress(new Dictionary<string, long?> { ["a"] = 100, ["b"] = 300 }, updates.Add);
        progress.ForFile("a").Report(new(ViperPhase.Downloading, 50, 100, 0, 1));
        Assert.Equal(12.5, updates.Last().Percent);
        progress.ForFile("a").Report(new(ViperPhase.Completed, 100, 100, 0, 0));
        Assert.Equal(25, updates.Last().Percent);
        progress.ForFile("b").Report(new(ViperPhase.Completed, 300, 300, 0, 0));
        Assert.Equal(InstallProgressPhase.Preparing, updates.Last().Phase);
        Assert.Null(updates.Last().Percent);
    }

    [Fact]
    public void TransferLengthOverridesPreliminaryHeadSize()
    {
        InstallProgressUpdate? update = null;
        var progress = new OfficeViperProgress(new Dictionary<string, long?> { ["a"] = 100, ["b"] = 100 }, value => update = value);
        progress.ForFile("a").Report(new(ViperPhase.Completed, 300, 300, 0, 0));
        Assert.Equal(75, update!.Percent);
    }

    [Fact]
    public void UnknownSizeDoesNotProduceInventedPercentage()
    {
        InstallProgressUpdate? update = null;
        var progress = new OfficeViperProgress(new Dictionary<string, long?> { ["a"] = 100, ["b"] = null }, value => update = value);
        progress.ForFile("a").Report(new(ViperPhase.Completed, 100, 100, 0, 0));
        Assert.NotNull(update);
        Assert.Null(update.Percent);
        Assert.Equal(InstallProgressPhase.Downloading, update.Phase);
    }
}
