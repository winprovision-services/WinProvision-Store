using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinProvision.Store.Controls;
using WinProvision.Core.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store;

internal static partial class SmokeTest
{
    private static void VerifyOperationSettingsRenderer()
    {
        var texts = new Dictionary<string, string>
        {
            ["choice.first"] = "Primeira opção",
            ["choice.default"] = "Opção padrão",
            ["operations.defaultSuffix"] = " (padrão)",
            ["operations.folder.noFolder"] = "Downloads; perguntar a cada lote",
            ["operations.folder.choose"] = "Escolher pasta",
            ["operations.folder.default"] = "Usar padrão",
            ["operations.state.enabled"] = "Ativado",
            ["operations.state.disabled"] = "Desativado",
            ["action.run"] = "Executar",
            ["operations.option.2"] = "Opção padrão"
        };
        ElevationSettingChoice[] choices =
        [
            new("1", "choice.first"),
            new("2", "choice.default")
        ];
        var firstComboDefinition = new ElevationSettingDefinition(
            "synthetic-combo-one", "synthetic-group", "preference", "normal",
            JsonDocument.Parse("\"2\"").RootElement.Clone(), null, null, true, false,
            "title", "description", null, "combo", choices, StorageKey: "syntheticStorage");
        var secondComboDefinition = firstComboDefinition with { Key = "another-synthetic-combo" };
        var firstCombo = new SettingsCatalogSettingOption(firstComboDefinition, "Título", "Descrição", null, "2", texts);
        var secondCombo = new SettingsCatalogSettingOption(secondComboDefinition, "Título", "Descrição", null, "2", texts);
        var toggleDefinition = new ElevationSettingDefinition(
            "synthetic-toggle", "synthetic-group", "preference", "normal",
            JsonDocument.Parse("true").RootElement.Clone(), null, null, true, false,
            "title", "description", null, "toggle", StorageKey: "syntheticStorage");
        var toggle = new SettingsCatalogSettingOption(toggleDefinition, "Título", "Descrição", null, "true", texts);
        var firstToggleCardDefinition = toggleDefinition with { Key = "invented-toggle-card-a", Control = "toggle-card" };
        var secondToggleCardDefinition = firstToggleCardDefinition with { Key = "invented-toggle-card-b" };
        var firstToggleCard = new SettingsCatalogSettingOption(firstToggleCardDefinition, "Título", "Descrição", null, "true", texts);
        var secondToggleCard = new SettingsCatalogSettingOption(secondToggleCardDefinition, "Título", "Descrição", null, "true", texts);
        var firstActionDefinition = new ElevationSettingDefinition(
            "invented-action-a", "synthetic-group", "info", "normal", default, null, null, true, false,
            "title", "description", null, "action", ActionKey: "syntheticAction", ActionTextKey: "action.run");
        var secondActionDefinition = firstActionDefinition with { Key = "invented-action-b" };
        var firstAction = new SettingsCatalogSettingOption(firstActionDefinition, "Título", "Descrição", null, "", texts);
        var secondAction = new SettingsCatalogSettingOption(secondActionDefinition, "Título", "Descrição", null, "", texts);
        var descriptionProviders = new SettingsCatalogDescriptionProviderRegistry();
        string measured = "10,5 MB em 2 arquivos";
        descriptionProviders.Register("synthetic-measurement", () => measured);
        var firstMeasuredDefinition = firstActionDefinition with
        {
            Key = "invented-measured-action-a",
            DescriptionProviderId = "synthetic-measurement"
        };
        var secondMeasuredDefinition = firstMeasuredDefinition with { Key = "invented-measured-action-b" };
        var firstMeasuredAction = new SettingsCatalogSettingOption(firstMeasuredDefinition, "Título", measured, null, "", texts);
        var secondMeasuredAction = new SettingsCatalogSettingOption(secondMeasuredDefinition, "Título", measured, null, "", texts);
        var folderDefinition = new ElevationSettingDefinition(
            "synthetic-folder", "synthetic-group", "preference", "normal",
            JsonDocument.Parse("\"\"").RootElement.Clone(), null, null, true, false,
            "title", "description", null, "folder", StorageKey: "syntheticStorage",
            SelectTextKey: "operations.folder.choose", ResetTextKey: "operations.folder.default");
        var folder = new SettingsCatalogSettingOption(folderDefinition, "Título", "Descrição", null, "", texts);
        var noDescriptionDefinition = toggleDefinition with { Key = "invented-no-description" };
        var noDescription = new SettingsCatalogSettingOption(noDescriptionDefinition, "Título", "", null, "true", texts);

        Check(firstCombo.IsCombo && secondCombo.IsCombo
            && firstCombo.Options.SequenceEqual(secondCombo.Options)
            && firstCombo.Options.Single(option => option.Value == "2").Label == "Opção padrão (padrão)",
            "Operações: combos sintéticos com chaves distintas geram as mesmas opções");
        Check(toggle.IsToggle && toggle.ToggleValue && toggle.StateText == "Ativado",
            "Operações: toggle sintético deriva estado e rótulo de suas características");
        Check(firstToggleCard.IsToggleCard && secondToggleCard.IsToggleCard
            && firstToggleCard.StateText == secondToggleCard.StateText,
            "Catálogo: toggle-card usa características iguais com chaves sintéticas distintas");
        Check(firstAction.IsAction && secondAction.IsAction
            && firstAction.ActionButtonText == secondAction.ActionButtonText,
            "Catálogo: linhas de ação sintéticas usam título de botão e controle declarados");
        Check(firstMeasuredDefinition.DescriptionProviderId == secondMeasuredDefinition.DescriptionProviderId
            && descriptionProviders.TryGet(firstMeasuredDefinition.DescriptionProviderId, out string firstDescription)
            && descriptionProviders.TryGet(secondMeasuredDefinition.DescriptionProviderId, out string secondDescription)
            && firstDescription == secondDescription,
            "Catálogo: ações sintéticas usam o mesmo provedor de descrição sem depender da chave");
        measured = "Medindo…";
        Check(descriptionProviders.TryGet(firstMeasuredDefinition.DescriptionProviderId, out string updatedDescription)
            && updatedDescription == "Medindo…",
            "Catálogo: descrições de estado dinâmico atualizam sem alterar a configuração");
        Check(folder.IsFolder && folder.Description == "Downloads; perguntar a cada lote"
            && folder.SelectFolderText == "Escolher pasta" && folder.ResetFolderText == "Usar padrão",
            "Operações: seletor de pasta sintético usa descrição e ações localizadas");
        Check(!noDescription.HasDescription && noDescription.DisplayDescription is null,
            "Catálogo: descrições vazias não reservam espaço em cartões sintéticos");
    }

    private static async Task VerifySharedSettingsLayoutsAsync(MainWindow window)
    {
        var pageHost = (ContentControl)window.RootElement.FindName("PageHost");
        object? previousContent = pageHost.Content;
        try
        {
            static object FindApplicationResource(string key)
            {
                foreach (ResourceDictionary dictionary in Application.Current.Resources.MergedDictionaries.Reverse())
                {
                    if (dictionary.ContainsKey(key))
                        return dictionary[key];
                }
                throw new KeyNotFoundException($"Recurso de interface ausente: {key}");
            }

            Style textBoxStyle = (Style)FindApplicationResource("SettingsInlineTextBoxStyle");
            Style comboBoxStyle = (Style)FindApplicationResource("SettingsInlineComboBoxStyle");
            Style buttonStyle = (Style)FindApplicationResource("SettingsInlineButtonStyle");
            Style packageManagerTextBoxStyle = (Style)FindApplicationResource("PackageManagerSettingsTextBoxStyle");
            var input = new TextBox
            {
                Style = textBoxStyle,
                Width = 220,
                Text = "C:\\Tools\\winget.exe",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
            };
            var packageManagerInput = new TextBox
            {
                Style = packageManagerTextBoxStyle,
                Width = 220,
                Text = "C:\\Tools\\winget.exe",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
            };
            var combo = new ComboBox
            {
                Style = comboBoxStyle,
                Width = 220,
                ItemsSource = new[] { "Padrão", "Alternativa" },
                SelectedIndex = 0
            };
            var action = new Button
            {
                Style = buttonStyle,
                Content = "Abrir"
            };
            var navigationWithoutDescription = new SettingsNavigationCard
            {
                Title = "Configuração sintética sem descrição",
                Description = string.Empty,
                Width = 240
            };
            var navigationWithDescription = new SettingsNavigationCard
            {
                Title = "Configuração sintética com um título suficientemente longo para quebrar em várias linhas",
                Description = "Descrição sintética que também pode ocupar mais de uma linha.",
                Width = 240
            };
            var layout = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    navigationWithoutDescription,
                    navigationWithDescription,
                    input,
                    packageManagerInput,
                    combo,
                    action
                }
            };
            pageHost.Content = layout;
            pageHost.UpdateLayout();
            await Task.Delay(50);
            layout.UpdateLayout();

            var emptyDescription = (TextBlock)navigationWithoutDescription.FindName("DescriptionTextBlock");
            Check(emptyDescription.Visibility == Visibility.Collapsed
                && navigationWithoutDescription.HasDescription == false
                && navigationWithoutDescription.ActualHeight >= 64,
                "Configurações: navegação sintética sem descrição não reserva altura e respeita altura mínima");
            Check(navigationWithDescription.ActualHeight > navigationWithoutDescription.ActualHeight,
                "Configurações: cartão de navegação cresce com título e descrição sem cortar conteúdo");
            Check(input.ActualHeight >= 36
                && input.VerticalContentAlignment == VerticalAlignment.Center
                && input.Padding.Top == input.Padding.Bottom,
                "Configurações: campo sintético, inclusive monoespaçado, centraliza o texto com padding simétrico");
            Check(packageManagerInput.ActualHeight >= 36
                && packageManagerInput.VerticalContentAlignment == VerticalAlignment.Center
                && packageManagerInput.Padding.Top == packageManagerInput.Padding.Bottom,
                "Gerenciadores: campo de texto sintético reutiliza a altura e o alinhamento compartilhados");
            Check(combo.ActualHeight >= 36 && combo.VerticalAlignment == VerticalAlignment.Center
                && action.ActualHeight >= 36 && action.VerticalAlignment == VerticalAlignment.Center,
                "Configurações: combo e botão sintéticos têm altura mínima e alinhamento vertical comum");
            SizeInt32 originalSize = window.AppWindow.Size;
            OverlappedPresenter presenter = window.AppWindow.Presenter as OverlappedPresenter
                ?? throw new InvalidOperationException("A janela de smoke não usa um apresentador redimensionável.");
            try
            {
                window.AppWindow.Resize(new SizeInt32(900, 700));
                await Task.Delay(120);
                window.RootElement.UpdateLayout();
                layout.UpdateLayout();
                CheckNavigationTextFits();
                presenter.Maximize();
                await Task.Delay(180);
                window.RootElement.UpdateLayout();
                layout.UpdateLayout();
                CheckNavigationTextFits();
            }
            finally
            {
                presenter.Restore();
                window.AppWindow.Resize(originalSize);
                await Task.Delay(120);
                window.RootElement.UpdateLayout();
            }

            void CheckNavigationTextFits()
            {
                Button navigationButton = (Button)navigationWithDescription.FindName("NavigationButton");
                TextBlock title = FindVisualElements<TextBlock>(navigationWithDescription)
                    .Single(textBlock => textBlock.Text == navigationWithDescription.Title);
                TextBlock description = (TextBlock)navigationWithDescription.FindName("DescriptionTextBlock");
                Windows.Foundation.Rect titleBounds = title.TransformToVisual(navigationButton)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, title.ActualWidth, title.ActualHeight));
                Windows.Foundation.Rect descriptionBounds = description.TransformToVisual(navigationButton)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, description.ActualWidth, description.ActualHeight));
                Check(description.Visibility == Visibility.Visible
                    && titleBounds.Y >= 0 && titleBounds.Bottom <= navigationButton.ActualHeight + 1
                    && descriptionBounds.Y >= 0 && descriptionBounds.Bottom <= navigationButton.ActualHeight + 1,
                    "Configurações: título e descrição de navegação não são cortados em janela compacta ou maximizada");
            }
        }
        finally
        {
            pageHost.Content = previousContent;
            pageHost.UpdateLayout();
            await Task.Delay(50);
        }
    }
}
