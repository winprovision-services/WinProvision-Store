using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class AppUpdateWorkflowTests
{
    [Fact]
    public void CheckDownloadAndApplyFollowTheDeclaredStateSequence()
    {
        var workflow = new AppUpdateWorkflow();

        workflow.BeginCheck();
        Assert.Equal(AppUpdateWorkflowState.Checking, workflow.State);
        workflow.CompleteCheck(updateAvailable: true);
        Assert.Equal(AppUpdateWorkflowState.UpdateAvailable, workflow.State);
        workflow.BeginDownload();
        Assert.Equal(AppUpdateWorkflowState.Downloading, workflow.State);
        workflow.CompleteDownload();
        Assert.Equal(AppUpdateWorkflowState.ReadyToApply, workflow.State);
        workflow.BeginApply();
        Assert.Equal(AppUpdateWorkflowState.Applying, workflow.State);
        workflow.CompleteApply();
        Assert.Equal(AppUpdateWorkflowState.ReadyToCheck, workflow.State);
    }

    [Fact]
    public void CancelCheckAndDownloadReturnToRetryableStates()
    {
        var workflow = new AppUpdateWorkflow();

        workflow.BeginCheck();
        workflow.CancelCheck();
        Assert.Equal(AppUpdateWorkflowState.ReadyToCheck, workflow.State);

        workflow.BeginCheck();
        workflow.CompleteCheck(updateAvailable: true);
        workflow.BeginDownload();
        workflow.CancelDownload();
        Assert.Equal(AppUpdateWorkflowState.UpdateAvailable, workflow.State);
    }

    [Fact]
    public void DecliningApplyConfirmationKeepsTheValidatedUpdateReady()
    {
        var workflow = new AppUpdateWorkflow();
        workflow.BeginCheck();
        workflow.CompleteCheck(updateAvailable: true);
        workflow.BeginDownload();
        workflow.CompleteDownload();
        workflow.BeginApply();
        workflow.CancelApply();

        Assert.Equal(AppUpdateWorkflowState.ReadyToApply, workflow.State);
    }

    [Fact]
    public void ApplyFailureAllowsTheAvailableUpdateToBeDownloadedAgain()
    {
        var workflow = new AppUpdateWorkflow();
        workflow.BeginCheck();
        workflow.CompleteCheck(updateAvailable: true);
        workflow.BeginDownload();
        workflow.CompleteDownload();
        workflow.BeginApply();
        workflow.FailApply();

        Assert.Equal(AppUpdateWorkflowState.UpdateAvailable, workflow.State);
    }

    [Fact]
    public void InvalidTransitionIsRejected()
    {
        var workflow = new AppUpdateWorkflow();

        Assert.Throws<InvalidOperationException>(workflow.BeginDownload);
        Assert.Equal(AppUpdateWorkflowState.ReadyToCheck, workflow.State);
    }
}
