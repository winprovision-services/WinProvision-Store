using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;
using WinProvision.Core.Models;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Services;

public static class PackageScreenActionExecutor
{
    public static async Task ExecuteAsync(IPackageScreenProvider provider, string actionId, PackageScreenRow? row = null)
    {
        ICommand? command = provider.GetActionCommand(actionId, row);
        object? parameter = provider.GetActionParameter(actionId, row);
        if (command?.CanExecute(parameter) != true) return;

        PackageScreenActionDefinition action = PackageScreenCatalog.Default.GetAction(actionId);
        if (action.Confirm)
        {
            int? count = provider.GetConfirmationItemCount(actionId, row);
            string messageKey = count switch
            {
                1 => "actions.confirm.message.one",
                > 1 => "actions.confirm.message.many",
                _ => "actions.confirm.message"
            };
            string fallback = count switch
            {
                1 => "Deseja executar “{action}” para 1 item?",
                > 1 => "Deseja executar “{action}” para {count} itens?",
                _ => "Deseja continuar com “{action}”?"
            };
            string message = PackageScreenCatalog.Default.GetText(messageKey, fallback)
                .Replace("{action}", PackageScreenCatalog.Default.GetText(action.Title, action.Title), StringComparison.Ordinal)
                .Replace("{count}", count?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? string.Empty, StringComparison.Ordinal);
            if (!await provider.PresentationService.ConfirmAsync(
                    PackageScreenCatalog.Default.GetText("actions.confirm.title", "Confirmar ação"),
                    message,
                    PackageScreenCatalog.Default.GetText("actions.confirm.continue", "Continuar"),
                    PackageScreenCatalog.Default.GetText("actions.confirm.cancel", "Cancelar")))
                return;
        }

        await provider.ExecuteConfirmedActionAsync(actionId, row);
    }

    public static async Task ExecuteCommandAsync(IPackageScreenProvider provider, string actionId, PackageScreenRow? row)
    {
        ICommand? command = provider.GetActionCommand(actionId, row);
        object? parameter = provider.GetActionParameter(actionId, row);
        if (command?.CanExecute(parameter) != true) return;

        if (command is IAsyncRelayCommand asyncCommand)
            await asyncCommand.ExecuteAsync(parameter);
        else
            command.Execute(parameter);
    }
}
