using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
namespace WinProvision.Store.ViewModels;
[WinRT.GeneratedBindableCustomProperty]
public sealed partial record SettingSearchResult(string Key, string Title, string Description, string Category, string CategoryKey, string SearchTerms)
{
    public string AccessibleName => $"{Title}, {Category}";
}
public sealed partial class SettingsViewModel
{
    private static readonly SettingSearchResult[] Entries =
    [
        new("install-method", "Método de instalação preferencial", "Escolha o método automático, WinGet COM, WinProvision API ou WinGet CLI.", "Preferências gerais", "General", "método instalar instalação pacote automático api com winget cli rota"),
        new("check-updates", "Verificação de atualizações", "Procure uma versão mais recente do WinProvision Store.", "Preferências gerais", "General", "atualização atualizar versão github verificar update"),
        new("open-logs", "Abrir pasta de logs", "Acesse os arquivos de log do aplicativo.", "Preferências gerais", "General", "log logs pasta diagnóstico arquivos"),
        new("reset-preferences", "Restaurar padrões", "Volte às preferências originais do aplicativo.", "Preferências gerais", "General", "restaurar padrões redefinir reset configurações"),
        new("theme", "Tema do aplicativo", "Alterne entre os temas claro, escuro e do sistema.", "Interface e inicialização", "Interface", "tema aparência claro escuro sistema cor"),
        new("startup", "Iniciar com o Windows", "Abra o WinProvision automaticamente ao entrar nesta conta.", "Interface e inicialização", "Interface", "inicialização iniciar login windows automático"),
        new("clear-cache", "Limpar cache local de instaladores", "Remova os instaladores temporários para liberar espaço em disco.", "Armazenamento e cache", "Operations", "cache armazenamento espaço disco temporários instaladores limpar"),
    ];

    public ObservableCollection<SettingSearchResult> SearchResults { get; } = new();
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasNoSearchResults => IsSearching && SearchResults.Count == 0;
    public string SearchResultSummary => $"{SearchResults.Count} opções encontradas. Selecione para abrir essa configuração.";
    partial void OnSearchTextChanged(string value)
    {
        SearchResults.Clear();
        if (IsSearching)
        {
            var terms = Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var entry in Entries)
                if (terms.All(term => Normalize($"{entry.Title} {entry.Description} {entry.Category} {entry.SearchTerms}").Contains(term, StringComparison.Ordinal))) SearchResults.Add(entry);
        }
        OnPropertyChanged(nameof(IsSearching)); OnPropertyChanged(nameof(HasNoSearchResults)); OnPropertyChanged(nameof(SearchResultSummary));
    }
    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Select(char.ToLowerInvariant).ToArray());
}
