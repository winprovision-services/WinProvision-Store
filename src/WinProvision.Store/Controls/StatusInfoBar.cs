using CommunityToolkit.WinUI.Behaviors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Xaml.Interactivity;

namespace WinProvision.Store.Controls;

/// <summary>Enfileira mensagens da página em uma InfoBar empilhada do biblioteca de interface.</summary>
public sealed partial class StatusInfoBar : InfoBar
{
    public static readonly DependencyProperty StatusMessageProperty = DependencyProperty.Register(
        nameof(StatusMessage), typeof(string), typeof(StatusInfoBar), new PropertyMetadata(string.Empty, OnStatusMessageChanged));

    private readonly StackedNotificationsBehavior _notificationQueue = new();
    private bool _isReady;
    private string? _lastQueuedMessage;

    public string StatusMessage
    {
        get => (string)GetValue(StatusMessageProperty);
        set => SetValue(StatusMessageProperty, value);
    }

    public StatusInfoBar()
    {
        IsIconVisible = false;
        Interaction.GetBehaviors(this).Add(_notificationQueue);
        Loaded += StatusInfoBar_Loaded;
        Unloaded += StatusInfoBar_Unloaded;
    }

    private static void OnStatusMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not StatusInfoBar bar)
            return;

        if (args.NewValue is not string message || string.IsNullOrWhiteSpace(message))
        {
            bar._lastQueuedMessage = null;
            if (bar._isReady && bar._notificationQueue.AssociatedObject is not null)
                bar._notificationQueue.Clear();
            return;
        }

        if (bar._isReady && bar._notificationQueue.AssociatedObject is not null
            && !string.Equals(bar._lastQueuedMessage, message, StringComparison.Ordinal))
            bar.ShowNotification(message);
    }

    private void StatusInfoBar_Loaded(object sender, RoutedEventArgs args)
    {
        _isReady = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!string.IsNullOrWhiteSpace(StatusMessage)
                && !string.Equals(_lastQueuedMessage, StatusMessage, StringComparison.Ordinal))
                ShowNotification(StatusMessage);
        });
    }

    private void StatusInfoBar_Unloaded(object sender, RoutedEventArgs args) => _isReady = false;

    private void ShowNotification(string message)
    {
        _lastQueuedMessage = message;
        var (severity, title) = Classify(message);
        _ = _notificationQueue.Show(new Notification
        {
            Message = message.Trim(),
            Severity = severity,
            Title = title,
            IsIconVisible = false
        });
    }

    private static (InfoBarSeverity Severity, string Title) Classify(string message)
    {
        if (ContainsAny(message, "não foi possível", "falha", "falhou", "erro", "não conseguiu"))
            return (InfoBarSeverity.Error, "Falha");
        if (ContainsAny(message, "cancelad", "selecione", "não há", "ainda não", "somente "))
            return (InfoBarSeverity.Warning, "Atenção");
        if (ContainsAny(message, "copiado", "salvo", "importado", "exportado", "instalado", "removido", "adicionado", "sincronizado", "atualizad", "limpa", "entrou na fila", "vinculada"))
            return (InfoBarSeverity.Success, "Concluído");
        return (InfoBarSeverity.Informational, "Informação");
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
