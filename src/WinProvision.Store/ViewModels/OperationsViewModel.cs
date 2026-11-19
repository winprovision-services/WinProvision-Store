using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OperationViewModel : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly IOperationsPanelCatalog _catalog;
    private readonly IOperationTextProvider _texts;
    private readonly IOperationCommandRegistry _commands;
    private bool _disposed;
    public OperationItem Operation { get; }
    public IRelayCommand ActionCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public ResolvedStatePresentation StatePresentation => OperationStatePresentationResolver.Resolve(State, IsIndeterminate,
        Microsoft.UI.Xaml.Application.Current.RequestedTheme == Microsoft.UI.Xaml.ApplicationTheme.Light, _catalog.Current);
    public Microsoft.UI.Xaml.Media.Brush StateBrush => ResolveStateBrush(StatePresentation.ColorToken, StatePresentation.BackgroundAlpha);
    public Microsoft.UI.Xaml.Media.Brush ProgressBrush => (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[StatePresentation.FillColorToken];
    public Microsoft.UI.Xaml.Media.Brush ProgressTrackBrush => (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[StatePresentation.TrackColorToken];
    public Microsoft.UI.Xaml.Media.Brush DescriptionBackgroundBrush => ResolveStateBrush(StatePresentation.MessageColorToken, StatePresentation.MessageAlpha);
    private static Microsoft.UI.Xaml.Media.Brush ResolveStateBrush(string token, double? alpha)
    {
        var source = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources[token];
        if (alpha is null || source is not Microsoft.UI.Xaml.Media.SolidColorBrush solid) return source;
        byte opacity = (byte)Math.Clamp(Math.Round(alpha.Value * 255), 0, 255);
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(opacity, solid.Color.R, solid.Color.G, solid.Color.B));
    }
    public string DescriptionFontWeightToken => StatePresentation.MessageFontWeight;
    public Microsoft.UI.Xaml.Visibility ProgressVisibility => StatePresentation.ProgressVisible ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public bool PresentationIsIndeterminate => StatePresentation.ProgressMode == OperationProgressMode.Indeterminate;
    public Microsoft.UI.Xaml.Media.Brush TitleBrush => (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorPrimaryBrush"];
    public double FlexibleTitleMaxWidth => Layout.TitleMaxWidthFraction;
    public double FlexibleRegionWidth { get; set; }
    private OperationsPanelLayout Layout => _catalog.Current.Layout;
    private OperationsPanelTheme Theme => _catalog.Current.Theme;
    public double TitleMaxWidth => FlexibleRegionWidth <= 0 ? Layout.TitleMaxWidth : Math.Min(Layout.TitleMaxWidth, Math.Max(Layout.TitleMinWidth, FlexibleRegionWidth * Layout.TitleMaxWidthFraction));
    public void NotifyFlexibleWidthChanged() => OnPropertyChanged(nameof(TitleMaxWidth));
    public double TitleMinWidth => Layout.TitleMinWidth;
    public double DescriptionMinWidth => Layout.DescriptionMinWidth;
    public double RowHeight => Layout.RowHeight;
    public Microsoft.UI.Xaml.CornerRadius RowCornerRadius => new(Theme.RowCornerRadius);
    public Microsoft.UI.Xaml.Thickness RowPadding => new(Layout.RowHorizontalPadding, Layout.RowVerticalPadding, Layout.RowHorizontalPadding, Layout.RowVerticalPadding);
    public Microsoft.UI.Xaml.Thickness RowMargin => new(0, Layout.RowMarginVertical, 0, Layout.RowMarginVertical);
    public double IconColumnWidth => Layout.IconColumnWidth;
    public Microsoft.UI.Xaml.GridLength IconColumnGridLength => new(Layout.IconColumnWidth);
    public double IconSize => Layout.IconSize;
    public double ActionButtonWidth => Layout.ActionButtonWidth;
    public Microsoft.UI.Xaml.GridLength ActionButtonColumnGridLength => new(Layout.ActionButtonWidth);
    public double MoreButtonWidth => Layout.MoreButtonWidth;
    public Microsoft.UI.Xaml.GridLength MoreButtonColumnGridLength => new(Layout.MoreButtonWidth);
    public double ActionButtonHeight => Layout.ActionButtonHeight;
    public double FlexibleColumnSpacing => Layout.FlexibleColumnSpacing;
    public double DescriptionHeight => Layout.DescriptionHeight;
    public Microsoft.UI.Xaml.CornerRadius DescriptionCornerRadius => new(Theme.MessageCornerRadius);
    public Microsoft.UI.Xaml.CornerRadius ProgressCornerRadius => new(Theme.ProgressCornerRadius);
    public Microsoft.UI.Xaml.Thickness DescriptionTextMargin => new(Theme.MessagePaddingHorizontal, Theme.MessagePaddingTop, Theme.MessagePaddingHorizontal, Theme.MessagePaddingBottom);
    public double DescriptionFontSize => Theme.MessageFontSize;
    public string DescriptionFontToken => StatePresentation.MessageFontToken;
    public double DescriptionProgressTrackHeight => Theme.ProgressTrackHeight;
    public double ProgressHeight => Theme.ProgressHeight;
    public double TitleFontSize => Theme.TitleFontSize;
    public double ActionFontSize => Theme.ActionFontSize;
    public double RowIconHeight => Layout.RowIconHeight;
    public string AccessibleDescription => $"{DisplayTitle}. {QueueStatusText}";
    public OperationActionContext ActionContext
    {
        get
        {
            string? path = Operation.Snapshot?.InstallLocation;
            return new(OperationActionScope.Item, State, Kind,
                HasPackage: Operation.Snapshot is { Manager: "winget" or "chocolatey" },
                HasInstallLocation: !string.IsNullOrWhiteSpace(path) && Directory.Exists(path),
                CanCancel: Operation.CanCancel,
                CanReplay: IsFinished && Operation.Snapshot is { Manager: "winget" or "chocolatey" },
                CanReplayElevated: IsFinished && Operation.Snapshot is { Manager: "winget" or "chocolatey", Options.RequiresElevation: false },
                CanReplayInteractive: IsFinished && Operation.Snapshot is { Manager: "winget" or "chocolatey", Options.Interactive: false },
                Manager: Operation.Snapshot?.Manager);
        }
    }
    public bool NeedsInstallLocationResolution => Operation.Snapshot is { Manager: "winget" or "chocolatey" } snapshot
        && string.IsNullOrWhiteSpace(snapshot.InstallLocation);

    public OperationViewModel(OperationItem operation, IUiDispatcher dispatcher, IOperationsPanelCatalog catalog,
        IOperationTextProvider texts, IOperationCommandRegistry commands)
    {
        Operation = operation;
        _dispatcher = dispatcher;
        _catalog = catalog;
        _texts = texts;
        _commands = commands;
        _catalog.Changed += Catalog_Changed;
        _texts.Changed += Texts_Changed;
        CancelCommand = new RelayCommand(Operation.Cancel, () => Operation.CanCancel);
        ActionCommand = new RelayCommand(() => _ = ExecutePrimaryActionAsync());
        Operation.PropertyChanged += OperationChanged;
    }

    private async Task ExecutePrimaryActionAsync()
    {
        ResolvedAction action = OperationActionResolver.Resolve(OperationActionScope.Item, ActionContext,
                new OperationActionCatalog(_catalog.Current.Actions ?? OperationActionCatalog.BuiltIn.Actions))
            .Single(resolved => resolved.Placement == OperationActionPlacement.Primary && resolved.Visible);
        OperationActionContext context = action.CommandId == "cancelOperation" ? ActionContext with { CanCancel = true } : ActionContext;
        await _commands.ExecuteAsync(action.CommandId, context,
            (this, (Func<OperationViewModel, Task>)(_ => Task.CompletedTask)));
    }

    public Guid Id => Operation.Id;
    public string? IconUrl => Operation.IconUrl ?? Operation.Snapshot?.IconUrl;
    public bool HasPackageIcon => !string.IsNullOrWhiteSpace(IconUrl);
    public string? ManagerIconUrl => OperationActionIconRegistry.GetManagerIconUrl(
        OperationStatePresentationCatalog.GetManagerIconId(Operation.Manager, _catalog.Current.ManagerIcons));
    private string AppName => Operation.AppName;
    private OperationKind Kind => Operation.Kind;
    private string StatusText => Operation.StatusText;
    private string KindTextKey => Kind switch
    {
        OperationKind.Install => "install",
        OperationKind.Update => "update",
        OperationKind.Uninstall => "uninstall",
        _ => "download"
    };

    private string OriginalPackageName => Operation.Snapshot is { PackageName.Length: > 0 } snapshot
        && !string.Equals(snapshot.PackageName, Operation.AppName, StringComparison.Ordinal)
            ? snapshot.PackageName
            : Operation.AppName;

    private string DisplayPackageName => Operation.Snapshot is { PackageName.Length: > 0 } snapshot
        && !string.Equals(snapshot.PackageName, Operation.AppName, StringComparison.Ordinal)
            ? snapshot.PackageName
            : PackageNameNormalizer.ToDisplayName(Operation.AppName, Operation.VersionFrom ?? Operation.VersionTo);

    private int _queuePosition;
    public string RowTitle => DisplayTitle;
    private string BaseQueueStatusText => State switch
    {
        OperationState.Queued => Text("operations.message.queuedPosition", ("position", Math.Max(1, _queuePosition).ToString(System.Globalization.CultureInfo.InvariantCulture))),
        OperationState.Running => !string.IsNullOrWhiteSpace(LiveLine) ? LiveLine
            : Operation.StatusMessageKey is { Length: > 0 } statusKey ? _texts.Format(statusKey, StatusArguments)
            : string.IsNullOrWhiteSpace(StatusText) ? DisplayTitle : StatusText,
        OperationState.Failed or OperationState.Completed or OperationState.Canceled or OperationState.Unconfirmed =>
            FailureTextKey is { } failureKey ? Text(failureKey, ("packageName", DisplayPackageName))
                : !string.IsNullOrWhiteSpace(StatusText) ? StatusText
                : Operation.StatusMessageKey is { Length: > 0 } statusKey ? _texts.Format(statusKey, StatusArguments)
                : UserFacingStatusText,
        _ => UserFacingStatusText
    };
    private string RouteLabel => !Operation.Manager.Equals("winget", StringComparison.OrdinalIgnoreCase)
        ? string.Empty
        : Operation.Method switch
        {
            WingetMethod.ComApi => Text("operations.route.com"),
            WingetMethod.WingetExe when Operation.IsFallback => Text("operations.route.cliFallback"),
            WingetMethod.WingetExe => Text("operations.route.cli"),
            _ => string.Empty
        };
    public string QueueStatusText => string.IsNullOrWhiteSpace(RouteLabel)
        ? BaseQueueStatusText : $"{RouteLabel} · {BaseQueueStatusText}";
    public string DescriptionToolTip => IsFinished ? UserFacingStatusText : FullStatusToolTip;
    public string TitleToolTip => string.IsNullOrWhiteSpace(Operation.VersionFrom ?? Operation.VersionTo)
        ? OriginalPackageName
        : Text("operations.tooltip.title.version", ("packageName", OriginalPackageName), ("version", Operation.VersionFrom ?? Operation.VersionTo));
    public string CollapseAutomationText => _texts.GetText("operations.automation.collapse");
    public string HeaderMenuAutomationText => _texts.GetText("operations.automation.headerMenu");
    public string MoreOptionsAutomationText => _texts.GetText("operations.automation.moreOptions");
    private string? FailureTextKey => !Operation.HasFailureReason ? null : Operation.FailureReason switch
    {
        WingetFailureReason.InstallationInProgress => "operations.failure.installationInProgress",
        WingetFailureReason.FilesInUse => "operations.failure.filesInUse",
        WingetFailureReason.DiskFull => "operations.failure.diskFull",
        WingetFailureReason.RebootRequiredBeforeInstall => "operations.failure.rebootRequiredBeforeInstall",
        WingetFailureReason.UserScopeElevationConflict => $"operations.failure.userScopeElevationConflict.{KindTextKey}",
        WingetFailureReason.NoPackageFound => "operations.failure.noPackageFound",
        WingetFailureReason.PackageNotInCatalog => "operations.failure.packageNotInCatalog",
        WingetFailureReason.ElevationRequired => "operations.failure.elevationRequired",
        WingetFailureReason.ElevationCanceled => $"operations.failure.elevationCanceled.{KindTextKey}",
        WingetFailureReason.OperationCanceled => $"operations.failure.operationCanceled.{KindTextKey}",
        WingetFailureReason.BlockedByPolicy => "operations.failure.blockedByPolicy",
        WingetFailureReason.NoApplicableInstallers => "operations.failure.noApplicableInstallers",
        WingetFailureReason.NoApplicableUpgrade => "operations.failure.noApplicableUpgrade",
        WingetFailureReason.PackageAgreementsNotAccepted => "operations.failure.packageAgreementsNotAccepted",
        WingetFailureReason.DownloadError => "operations.failure.downloadError",
        WingetFailureReason.InstallerHashMismatch => "operations.failure.installerHashMismatch",
        WingetFailureReason.InstallError => "operations.failure.installError",
        WingetFailureReason.CatalogError => "operations.failure.catalogError",
        WingetFailureReason.InternalError => $"operations.failure.internalError.{KindTextKey}",
        _ => $"operations.failure.fallback.{KindTextKey}"
    };
    public void RefreshStateBrushes()
    {
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(ProgressBrush));
        OnPropertyChanged(nameof(DescriptionBackgroundBrush));
    }
    public string FullStatusToolTip => string.IsNullOrWhiteSpace(Operation.GetFullLog())
        ? _texts.GetText("operations.message.noLogsAvailable") : Operation.GetFullLog();
    private IReadOnlyDictionary<string, string?> StatusArguments => new Dictionary<string, string?>
    {
        ["packageName"] = DisplayPackageName,
        ["version"] = Operation.VersionFrom ?? Operation.VersionTo,
        ["position"] = Math.Max(1, _queuePosition).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["value"] = Operation.StatusMessageValue
    };
    public void SetQueuePosition(int position)
    {
        if (_queuePosition == position) return;
        _queuePosition = position;
        OnPropertyChanged(nameof(QueueStatusText));
        OnPropertyChanged(nameof(DescriptionToolTip));
    }

    /// <summary>Título da operação (ex.: 'Atualizando Boto3...').</summary>
    public string DisplayTitle => State switch
    {
        OperationState.Running => Text($"operations.title.running.{KindTextKey}", ("packageName", DisplayPackageName)),
        OperationState.Queued => Text($"operations.title.queued.{KindTextKey}", ("packageName", DisplayPackageName)),
        OperationState.Completed when Operation.RebootRequired => Text("operations.title.completed.reboot", ("packageName", DisplayPackageName)),
        OperationState.Completed => Text($"operations.title.completed.{KindTextKey}", ("packageName", DisplayPackageName)),
        OperationState.Unconfirmed => Text("operations.title.unconfirmed", ("packageName", DisplayPackageName)),
        OperationState.Failed => Text($"operations.title.failed.{KindTextKey}", ("packageName", DisplayPackageName)),
        OperationState.Canceled => Text($"operations.title.canceled.{KindTextKey}", ("packageName", DisplayPackageName)),
        _ => Text("operations.title.fallback", ("packageName", DisplayPackageName))
    };

    public string ButtonText => OperationActionResolver.Resolve(OperationActionScope.Item,
            new OperationActionContext(OperationActionScope.Item, State, Kind, CanCancel: Operation.CanCancel),
            new OperationActionCatalog(_catalog.Current.Actions ?? OperationActionCatalog.BuiltIn.Actions))
        .First(action => action.Placement == OperationActionPlacement.Primary && action.Visible) is { } action
            ? _texts.GetText(action.TextKey) : string.Empty;

    /// <summary>Resumo curto para a interface; logs detalhados permanecem em DetailText.</summary>
    public string UserFacingStatusText => State switch
    {
        OperationState.Queued => _texts.GetText("operations.message.queued"),
        OperationState.Running => string.IsNullOrWhiteSpace(StatusText) ? _texts.GetText($"operations.message.running.{KindTextKey}") : StatusText,
        OperationState.Completed when Operation.RebootRequired => _texts.GetText("operations.message.completed.reboot"),
        OperationState.Completed => _texts.GetText($"operations.message.completed.{KindTextKey}"),
        OperationState.Failed => _texts.GetText("operations.message.failed"),
        OperationState.Canceled => _texts.GetText("operations.message.canceled"),
        OperationState.Unconfirmed => _texts.GetText("operations.message.unconfirmed"),
        _ => string.Empty
    };
    public string TerminalDescription => State switch
    {
        OperationState.Completed when Operation.RebootRequired => _texts.GetText("operations.message.completed.reboot"),
        OperationState.Completed => _texts.GetText($"operations.message.completed.{KindTextKey}"),
        OperationState.Failed => _texts.GetText("operations.message.failed"),
        OperationState.Canceled => _texts.GetText("operations.message.canceled"),
        OperationState.Unconfirmed => _texts.GetText("operations.message.unconfirmed"),
        _ => QueueStatusText
    };


    public string LiveLine => Operation.LiveLine;
    public double Progress => Operation.Progress;
    public double ProgressFillWidth => Math.Max(0, (FlexibleRegionWidth - TitleMaxWidth - Layout.FlexibleColumnSpacing) * Math.Clamp(Progress, 0, 100) / 100d);
    public double IndeterminateProgressWidth => Math.Max(Layout.DescriptionMinWidth * .25, FlexibleRegionWidth * .25);
    public bool IsIndeterminate => Operation.IsIndeterminate;
    public bool IsFinished => Operation.IsFinished;
    public OperationState State => Operation.State;
    public WingetMethod Method => Operation.Method;

    private void OperationChanged(object? sender, PropertyChangedEventArgs args) =>
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            OnPropertyChanged(args.PropertyName);
            if (args.PropertyName is nameof(OperationItem.State) or nameof(OperationItem.IsIndeterminate))
            {
                OnPropertyChanged(nameof(StatePresentation)); OnPropertyChanged(nameof(PresentationIsIndeterminate));
                OnPropertyChanged(nameof(ProgressVisibility)); OnPropertyChanged(nameof(ProgressTrackBrush));
            }
            if (args.PropertyName is nameof(OperationItem.LiveLine) or nameof(OperationItem.Progress) or nameof(OperationItem.IsIndeterminate)
                or nameof(OperationItem.StatusMessageKey) or nameof(OperationItem.StatusMessageValue)
                or nameof(OperationItem.FailureReason) or nameof(OperationItem.HasFailureReason))
            {
                OnPropertyChanged(nameof(QueueStatusText));
                OnPropertyChanged(nameof(AccessibleDescription));
            }
            if (args.PropertyName is nameof(OperationItem.Method) or nameof(OperationItem.IsFallback))
            {
                OnPropertyChanged(nameof(QueueStatusText));
                OnPropertyChanged(nameof(AccessibleDescription));
            }
            if (args.PropertyName is nameof(OperationItem.State))
            {
                OnPropertyChanged(nameof(StateBrush));
                OnPropertyChanged(nameof(ProgressBrush));
                OnPropertyChanged(nameof(StatePresentation)); OnPropertyChanged(nameof(ProgressTrackBrush));
                OnPropertyChanged(nameof(ProgressVisibility)); OnPropertyChanged(nameof(PresentationIsIndeterminate));
                OnPropertyChanged(nameof(DescriptionFontWeightToken)); OnPropertyChanged(nameof(DescriptionFontToken));
                OnPropertyChanged(nameof(DescriptionBackgroundBrush));
                OnPropertyChanged(nameof(AccessibleDescription));
                OnPropertyChanged(nameof(DescriptionToolTip));
            }
            if (args.PropertyName is nameof(OperationItem.State) or nameof(OperationItem.StatusText) or nameof(OperationItem.RebootRequired))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(UserFacingStatusText));
                OnPropertyChanged(nameof(QueueStatusText));
                OnPropertyChanged(nameof(ButtonText));
            }
            CancelCommand.NotifyCanExecuteChanged();
        });

    public void Dispose()
    {
        _disposed = true;
        Operation.PropertyChanged -= OperationChanged;
        _catalog.Changed -= Catalog_Changed;
        _texts.Changed -= Texts_Changed;
    }

    private void Texts_Changed(object? sender, EventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            foreach (string property in TextProperties) OnPropertyChanged(property);
        });
    }

    private string Text(string key, params (string Name, string? Value)[] values) =>
        _texts.Format(key, values.ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal));

    private void Catalog_Changed(object? sender, EventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            foreach (string property in MetricProperties) OnPropertyChanged(property);
        });
    }

    private static readonly string[] MetricProperties =
    [
        nameof(FlexibleTitleMaxWidth), nameof(TitleMaxWidth), nameof(TitleMinWidth), nameof(DescriptionMinWidth),
        nameof(RowHeight), nameof(RowCornerRadius), nameof(RowPadding), nameof(RowMargin), nameof(IconColumnWidth),
        nameof(IconColumnGridLength), nameof(IconSize), nameof(ActionButtonWidth), nameof(ActionButtonColumnGridLength),
        nameof(MoreButtonWidth), nameof(MoreButtonColumnGridLength), nameof(ActionButtonHeight),
        nameof(ButtonText),
        nameof(FlexibleColumnSpacing), nameof(DescriptionHeight), nameof(DescriptionCornerRadius), nameof(ProgressCornerRadius),
        nameof(DescriptionTextMargin), nameof(DescriptionFontSize), nameof(DescriptionFontToken), nameof(DescriptionFontWeightToken),
        nameof(StatePresentation), nameof(StateBrush), nameof(DescriptionBackgroundBrush), nameof(ProgressBrush),
        nameof(ProgressTrackBrush), nameof(ProgressVisibility), nameof(PresentationIsIndeterminate),
        nameof(DescriptionProgressTrackHeight), nameof(ProgressHeight), nameof(TitleFontSize), nameof(ActionFontSize),
        nameof(RowIconHeight), nameof(ManagerIconUrl)
    ];

    private static readonly string[] TextProperties =
    [
        nameof(RowTitle), nameof(QueueStatusText), nameof(DescriptionToolTip), nameof(TitleToolTip),
        nameof(CollapseAutomationText), nameof(HeaderMenuAutomationText), nameof(MoreOptionsAutomationText),
        nameof(DisplayTitle), nameof(ButtonText), nameof(UserFacingStatusText), nameof(TerminalDescription),
        nameof(AccessibleDescription), nameof(FullStatusToolTip)
    ];
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OperationsViewModel : ObservableObject, IDisposable
{
    private OperationPanelBehavior Behavior => OperationPanelBehaviorResolver.Resolve(_catalog.Current);
    public OperationPanelBehavior PanelBehavior => Behavior;
    public int CompletedOperationsAutoDismissSeconds => Behavior.AutoCloseDelaySeconds;
    public OperationsPanelConfiguration PanelConfiguration => _catalog.Current;
    public IOperationsPanelCatalog PanelCatalog => _catalog;
    private readonly OperationsQueueService _queue;
    private readonly IOperationsPanelCatalog _catalog;
    private readonly IOperationTextProvider _texts;
    private readonly IPackageBackend _backend;
    private readonly IOperationCommandRegistry _commands;
    private readonly IOperationConditionEvaluator _conditions;
    private readonly IPresentationService _presentation;
    private readonly IInstalledPackagesService _installedPackages;
    private readonly HistoryStore _history;
    private readonly List<OperationItem> _headerBatch = [];
    private System.Threading.Timer? _autoDismissTimer;
    private bool _pointerInsidePanel;
    private bool _operationPopupOpen;
    private bool _keyboardFocusInsidePanel;
    private OperationItem? _selectedOutput;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;
    public System.Collections.ObjectModel.ObservableCollection<OperationViewModel> Items { get; } = new();
    [ObservableProperty] public partial bool HasPending { get; set; }
    [ObservableProperty] public partial bool HasOperations { get; set; }
    [ObservableProperty] public partial bool HasFinishedOperations { get; set; }
    [ObservableProperty] public partial bool HasSuccessfulOperations { get; set; }
    [ObservableProperty] public partial bool HasFailedOperations { get; set; }
    [ObservableProperty] public partial bool IsQueueExpanded { get; set; } = true;
    public bool IsQueueCollapsed => !IsQueueExpanded;
    public IRelayCommand ToggleQueueCommand { get; }
    [ObservableProperty] public partial string OutputTitle { get; set; } = "";
    [ObservableProperty] public partial string OutputText { get; set; } = "";
    public bool ShowFailureBadge => HasFailedOperations && !IsQueueExpanded;
    public FluentIcons.Common.Icon ExpanderIcon => IsQueueExpanded ? FluentIcons.Common.Icon.ChevronUp : FluentIcons.Common.Icon.ChevronDown;
    public string HeaderText => _headerBatch.Count == 0
        ? _texts.GetText("operations.header.title")
        : _headerBatch.Any(item => item.State == OperationState.Failed)
            ? _texts.FormatPlural("operations.header.completed.failed", _headerBatch.Count(item => item.State == OperationState.Completed), new Dictionary<string, string?>
            {
                ["total"] = _headerBatch.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["failed"] = _headerBatch.Count(item => item.State == OperationState.Failed)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
            : _texts.FormatPlural("operations.header.completed", _headerBatch.Count(item => item.State is OperationState.Completed or OperationState.Canceled),
                new Dictionary<string, string?> { ["total"] = _headerBatch.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    public string HeaderAutomationText => _texts.GetText("operations.automation.headerMenu");
    public string GetText(string key) => _texts.GetText(key);
    public IReadOnlyList<ResolvedAction> ResolveActions(OperationActionScope scope, OperationActionContext context) =>
        OperationActionResolver.Resolve(scope, context,
            new OperationActionCatalog(_catalog.Current.Actions ?? OperationActionCatalog.BuiltIn.Actions), _conditions);
    public int GetQueuePosition(OperationViewModel item)
    {
        int position = 0;
        foreach (OperationViewModel candidate in Items)
        {
            if (candidate.State is OperationState.Queued) position++;
            if (candidate.Id == item.Id) return Math.Max(1, position);
        }
        return 0;
    }
    public IRelayCommand CancelAllCommand { get; }
    public IAsyncRelayCommand RetryFailedCommand { get; }
    public IRelayCommand ClearCommand { get; }
    public IRelayCommand ClearSuccessfulCommand { get; }
    public IRelayCommand<OperationViewModel> RemoveCommand { get; }
    public OperationsViewModel(OperationsQueueService queue, IUiDispatcher dispatcher, IPackageBackend backend,
        IPresentationService presentation, IInstalledPackagesService installedPackages, HistoryStore history,
        IOperationsPanelCatalog catalog, IOperationTextProvider texts, IOperationCommandRegistry commands,
        IOperationConditionEvaluator conditions)
    {
        _queue = queue; _dispatcher = dispatcher; _backend = backend; _presentation = presentation;
        _installedPackages = installedPackages; _history = history; _catalog = catalog; _texts = texts; _commands = commands; _conditions = conditions;
        CancelAllCommand = new RelayCommand(backend.CancelAll);
        ToggleQueueCommand = new RelayCommand(() => IsQueueExpanded = !IsQueueExpanded);
        RetryFailedCommand = new AsyncRelayCommand(RetryFailedAsync);
        ClearCommand = new RelayCommand(queue.ClearFinished);
        ClearSuccessfulCommand = new RelayCommand(queue.ClearSuccessful);
        RemoveCommand = new RelayCommand<OperationViewModel>(item => { if (item is not null) queue.Remove(item.Operation); });
        RegisterCommands();
        queue.PropertyChanged += Changed;
        catalog.Changed += Catalog_Changed;
        texts.Changed += Texts_Changed;
        Refresh();
    }
    partial void OnHasFailedOperationsChanged(bool value) => OnPropertyChanged(nameof(ShowFailureBadge));
    partial void OnIsQueueExpandedChanged(bool value) { OnPropertyChanged(nameof(ShowFailureBadge)); OnPropertyChanged(nameof(ExpanderIcon)); OnPropertyChanged(nameof(IsQueueCollapsed)); }
    public void SelectOutput(OperationViewModel item)
    {
        _selectedOutput = item.Operation;
        RefreshOutputText();
    }
    public async Task ExecuteResolvedActionAsync(string commandId, OperationViewModel? item,
        Func<OperationViewModel, Task> showOutput)
    {
        OperationActionContext context = item is null
            ? new(OperationActionScope.Header, HasFailed: HasFailedOperations, HasSucceeded: HasSuccessfulOperations,
                HasFinished: HasFinishedOperations, HasCancelable: HasPending)
            : new(OperationActionScope.Item, item.State, item.Operation.Kind,
                HasPackage: item.Operation.Snapshot is { Manager: "winget" or "chocolatey" },
                HasInstallLocation: !string.IsNullOrWhiteSpace(item.Operation.Snapshot?.InstallLocation),
                CanCancel: item.Operation.CanCancel,
                CanReplay: item.IsFinished && item.Operation.Snapshot is not null,
                CanReplayElevated: item.IsFinished && item.Operation.Snapshot is { Options.RequiresElevation: false },
                CanReplayInteractive: item.IsFinished && item.Operation.Snapshot is { Options.Interactive: false },
                Manager: item.Operation.Snapshot?.Manager);
        await _commands.ExecuteAsync(commandId, context, item is null ? showOutput : (item, showOutput));
    }

    private void RegisterCommands()
    {
        static bool Yes(OperationActionContext _) => true;
        static bool Package(OperationActionContext context) => context.HasPackage;
        static bool InstallPath(OperationActionContext context) => context.HasInstallLocation;
        static bool Replay(OperationActionContext context) => context.CanReplay;
        static bool Cancel(OperationActionContext context) => context.CanCancel;
        static bool Finished(OperationActionContext context) => context.State is OperationState.Completed or OperationState.Failed or OperationState.Canceled or OperationState.Unconfirmed;
        _commands.Register("showOperationOutput", context => context.Scope == OperationActionScope.Item, target =>
            target is ValueTuple<OperationViewModel, Func<OperationViewModel, Task>> pair ? pair.Item2(pair.Item1) : Task.CompletedTask);
        _commands.Register("showPackageDetails", Package, target => target is ValueTuple<OperationViewModel, Func<OperationViewModel, Task>> pair ? ShowPackageDetailsAsync(pair.Item1.Operation) : Task.CompletedTask);
        _commands.Register("showPackageOptions", Package, target => target is ValueTuple<OperationViewModel, Func<OperationViewModel, Task>> pair ? ShowPackageDetailsAsync(pair.Item1.Operation, true) : Task.CompletedTask);
        _commands.Register("openInstallLocation", InstallPath, target =>
        {
            if (target is ValueTuple<OperationViewModel, Func<OperationViewModel, Task>> pair && pair.Item1.Operation.Snapshot?.InstallLocation is { } path)
                Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
            return Task.CompletedTask;
        });
        _commands.Register("replayOperation", Replay, target => target is ValueTuple<OperationViewModel, Func<OperationViewModel, Task>> pair ? RetryOperationAsync(pair.Item1.Operation) : Task.CompletedTask);
        _commands.Register("replayOperationVariant:elevated", Replay, target => target is ValueTuple<OperationViewModel, Func<OperationViewModel, Task>> pair && pair.Item1.Operation.Snapshot is { } snapshot ? ReplayWithOptionsAsync(pair.Item1.Operation, snapshot.Options with { RequiresElevation = true }) : Task.CompletedTask);
        _commands.Register("replayOperationVariant:interactive", Replay, target => target is ValueTuple<OperationViewModel, Func<OperationViewModel, Task>> pair && pair.Item1.Operation.Snapshot is { } snapshot ? ReplayWithOptionsAsync(pair.Item1.Operation, snapshot.Options with { Interactive = true }) : Task.CompletedTask);
        _commands.Register("cancelOperation", Cancel, target => { if (target is ValueTuple<OperationViewModel, Func<OperationViewModel, Task>> pair) pair.Item1.Operation.Cancel(); return Task.CompletedTask; });
        _commands.Register("removeOperation", Finished, target => { if (target is ValueTuple<OperationViewModel, Func<OperationViewModel, Task>> pair) RemoveCommand.Execute(pair.Item1); return Task.CompletedTask; });
        _commands.Register("replayFailed", context => context.Scope == OperationActionScope.Header && context.HasFailed, _ => RetryFailedCommand.ExecuteAsync(null));
        _commands.Register("clearSucceeded", context => context.Scope == OperationActionScope.Header && context.HasSucceeded, _ => { ClearSuccessfulCommand.Execute(null); return Task.CompletedTask; });
        _commands.Register("clearFinished", context => context.Scope == OperationActionScope.Header && context.HasFinished, _ => { ClearCommand.Execute(null); return Task.CompletedTask; });
        _commands.Register("cancelAll", context => context.Scope == OperationActionScope.Header && context.HasCancelable, _ => { CancelAllCommand.Execute(null); return Task.CompletedTask; });
        _commands.Register("separator", Yes, _ => Task.CompletedTask);
    }
    private void RefreshOutputText()
    {
        if (_selectedOutput is not { } operation) return;
        OutputTitle = _texts.Format("operations.output.title", new Dictionary<string, string?> { ["packageName"] = operation.AppName });
        string log = operation.GetFullLog();
        OutputText = string.IsNullOrWhiteSpace(log) ? _texts.GetText("operations.message.noLogsAvailable") : log;
    }
    private async Task RetryFailedAsync()
    {
        foreach (OperationItem item in _queue.Operations.Where(item => item.State == OperationState.Failed).ToArray())
            await RetryOperationAsync(item);
    }
    public Task RetryOperationAsync(OperationItem item)
    {
        return RetryOneAsync(item);
    }
    public void SetAutoDismissPointerInside(bool value) { _pointerInsidePanel = value; UpdateAutoDismissTimer(); }
    public void SetAutoDismissPopupOpen(bool value) { _operationPopupOpen = value; UpdateAutoDismissTimer(); }
    public void SetAutoDismissKeyboardFocus(bool value) { _keyboardFocusInsidePanel = value; UpdateAutoDismissTimer(); }
    public async Task ReplayWithOptionsAsync(OperationItem item, PackageInstallOptions options)
    {
        if (!item.IsFinished || item.Snapshot is not { } snapshot) return;
        try { await OperationReplayService.ReplayAsync(_backend, snapshot with { Options = options }); }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "Operations.RetryVariant"); }
    }
    private async Task RetryOneAsync(OperationItem item)
        {
            if (!item.IsFinished || item.Snapshot is not { } snapshot) return;
            try
            {
                await OperationReplayService.ReplayAsync(_backend, snapshot);
            }
            catch (Exception exception) { WinProvisionLog.Warning(exception, "Operations.RetryFailed"); }
        }
    public async Task ShowPackageDetailsAsync(OperationItem item, bool showOptions = false)
    {
        if (item.Snapshot is not { } snapshot || snapshot.Manager is not ("winget" or "chocolatey")) return;
        var app = new AppEntry { Id = snapshot.PackageId, Name = snapshot.PackageName,
            Source = snapshot.Manager == "chocolatey" ? "chocolatey" : snapshot.Source ?? "winget",
            Version = snapshot.VersionTo ?? snapshot.VersionFrom ?? string.Empty, IconUrl = item.IconUrl ?? string.Empty };
        await _presentation.ShowDetailsAsync(app, snapshot.VersionFrom, snapshot.VersionTo, showOptions);
    }
    public async Task ResolveInstallLocationAsync(OperationItem item)
    {
        if (item.Snapshot is not { Manager: "winget" or "chocolatey" } snapshot
            || !string.IsNullOrWhiteSpace(snapshot.InstallLocation)) return;
        try
        {
            IReadOnlyList<InstalledPackageInfo> installed = await _installedPackages.ListAsync();
            string? path = installed.FirstOrDefault(package => package.Id.Equals(snapshot.PackageId, StringComparison.OrdinalIgnoreCase)
                && package.Source.Contains(snapshot.Manager, StringComparison.OrdinalIgnoreCase))?.InstallLocation;
            if (!string.IsNullOrWhiteSpace(path))
            {
                item.Snapshot = snapshot with { InstallLocation = path };
                _history.UpdateSnapshot(item.Id, item.Snapshot);
            }
        }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "Operations.ResolveInstallLocation"); }
    }
    public Task ResolveInstallLocationIfNeededAsync(OperationViewModel item) =>
        item.NeedsInstallLocationResolution ? ResolveInstallLocationAsync(item.Operation) : Task.CompletedTask;
    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        bool added = args.PropertyName == nameof(OperationsQueueService.Operations);
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            if (added) StopAutoDismissTimer();
            Refresh();
        });
    }
    private void Refresh()
    {
        bool hadOperations = HasOperations;
        OperationItem[] operations = OperationOrderingResolver.Resolve(_queue.Operations, Behavior.OrderingMode);
        OperationItem[] additions = operations.Where(item => !_headerBatch.Contains(item)).ToArray();
        if (additions.Length > 0 && _headerBatch.Count > 0 && _headerBatch.All(item => item.IsFinished)) _headerBatch.Clear();
        foreach (OperationItem item in additions) _headerBatch.Add(item);
        int previousCount = Items.Count;
        var ids = operations.Select(item => item.Id).ToHashSet();
        foreach (var item in Items.Where(item => !ids.Contains(item.Id)).ToArray()) { Items.Remove(item); item.Dispose(); }
        foreach (var item in operations) if (!Items.Any(vm => vm.Id == item.Id)) Items.Add(new OperationViewModel(item, _dispatcher, _catalog, _texts, _commands));
        int queuePosition = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            int current = Items.IndexOf(Items.Single(vm => vm.Id == operations[index].Id));
            if (current != index) Items.Move(current, index);
            OperationViewModel vm = Items[index];
            vm.SetQueuePosition(vm.State == OperationState.Queued ? ++queuePosition : 0);
        }
        var pending = operations.Count(item => !item.IsFinished);
        HasFinishedOperations = operations.Any(item => item.IsFinished);
        HasSuccessfulOperations = operations.Any(item => item.State == OperationState.Completed);
        HasPending = pending > 0; HasOperations = operations.Length > 0;
        HasFailedOperations = _queue.HasFailedOperations;
        OnPropertyChanged(nameof(HeaderText));
        if (operations.Length > previousCount && pending > 0) IsQueueExpanded = true;
        if (hadOperations && !HasOperations) IsQueueExpanded = true;
        UpdateAutoDismissTimer();
    }
    private bool IsAutoDismissPaused => (Behavior.PauseOnPointer && _pointerInsidePanel)
        || (Behavior.PauseOnPopup && _operationPopupOpen)
        || (Behavior.PauseOnFocus && _keyboardFocusInsidePanel);
    internal bool IsAutoDismissPausedForSmoke => IsAutoDismissPaused;
    private void UpdateAutoDismissTimer()
    {
        bool eligible = _queue.Operations.Any(item => item.State == OperationState.Completed)
            && _queue.Operations.All(item => item.IsFinished);
        if (!eligible)
        {
            StopAutoDismissTimer();
            return;
        }
        if (IsAutoDismissPaused)
        {
            StopAutoDismissTimer();
            return;
        }
        if (_autoDismissTimer is not null) return;
        _autoDismissTimer = new System.Threading.Timer(_ => _dispatcher.Post(() =>
        {
            _autoDismissTimer?.Dispose();
            _autoDismissTimer = null;
            if (!_disposed && !IsAutoDismissPaused
                && _queue.Operations.Any(item => item.State == OperationState.Completed)
                && _queue.Operations.All(item => item.IsFinished))
                _queue.ClearSuccessful();
        }), null, TimeSpan.FromSeconds(CompletedOperationsAutoDismissSeconds), Timeout.InfiniteTimeSpan);
    }
    private void StopAutoDismissTimer()
    {
        _autoDismissTimer?.Dispose();
        _autoDismissTimer = null;
    }
    private void Catalog_Changed(object? sender, EventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            foreach (string property in new[] { nameof(CompletedOperationsAutoDismissSeconds), nameof(PanelConfiguration), nameof(PanelBehavior) }) OnPropertyChanged(property);
            StopAutoDismissTimer();
            Refresh();
            UpdateAutoDismissTimer();
        });
    }
    private void Texts_Changed(object? sender, EventArgs e)
    {
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            OnPropertyChanged(nameof(HeaderText));
            OnPropertyChanged(nameof(HeaderAutomationText));
            RefreshOutputText();
        });
    }
    public void Dispose() { _disposed = true; StopAutoDismissTimer(); _queue.PropertyChanged -= Changed; _catalog.Changed -= Catalog_Changed; _texts.Changed -= Texts_Changed; foreach (var item in Items) item.Dispose(); Items.Clear(); }
}

