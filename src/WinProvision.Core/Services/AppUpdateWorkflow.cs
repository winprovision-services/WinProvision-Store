namespace WinProvision.Core.Services;

public enum AppUpdateWorkflowState
{
    ReadyToCheck,
    Checking,
    UpdateAvailable,
    Downloading,
    ReadyToApply,
    Applying
}

public sealed class AppUpdateWorkflow
{
    public AppUpdateWorkflowState State { get; private set; } = AppUpdateWorkflowState.ReadyToCheck;

    public void BeginCheck()
    {
        RequireState(AppUpdateWorkflowState.ReadyToCheck, AppUpdateWorkflowState.UpdateAvailable);
        State = AppUpdateWorkflowState.Checking;
    }

    public void CompleteCheck(bool updateAvailable)
    {
        RequireState(AppUpdateWorkflowState.Checking);
        State = updateAvailable
            ? AppUpdateWorkflowState.UpdateAvailable
            : AppUpdateWorkflowState.ReadyToCheck;
    }

    public void CancelCheck() => Transition(AppUpdateWorkflowState.Checking, AppUpdateWorkflowState.ReadyToCheck);

    public void BeginDownload()
    {
        RequireState(AppUpdateWorkflowState.UpdateAvailable);
        State = AppUpdateWorkflowState.Downloading;
    }

    public void CompleteDownload() => Transition(AppUpdateWorkflowState.Downloading, AppUpdateWorkflowState.ReadyToApply);

    public void CancelDownload() => Transition(AppUpdateWorkflowState.Downloading, AppUpdateWorkflowState.UpdateAvailable);

    public void FailDownload() => Transition(AppUpdateWorkflowState.Downloading, AppUpdateWorkflowState.UpdateAvailable);

    public void BeginApply() => Transition(AppUpdateWorkflowState.ReadyToApply, AppUpdateWorkflowState.Applying);

    public void CancelApply() => Transition(AppUpdateWorkflowState.Applying, AppUpdateWorkflowState.ReadyToApply);

    public void FailApply() => Transition(AppUpdateWorkflowState.Applying, AppUpdateWorkflowState.UpdateAvailable);

    public void CompleteApply() => Transition(AppUpdateWorkflowState.Applying, AppUpdateWorkflowState.ReadyToCheck);

    private void Transition(AppUpdateWorkflowState from, AppUpdateWorkflowState to)
    {
        RequireState(from);
        State = to;
    }

    private void RequireState(params AppUpdateWorkflowState[] allowedStates)
    {
        if (!allowedStates.Contains(State))
            throw new InvalidOperationException($"A transição não é válida a partir do estado {State}.");
    }
}
