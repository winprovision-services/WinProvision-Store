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
        new("notification-categories", "Notificações por categoria", "Controle avisos de atualizações, erros, sucessos e resumos de lotes.", "Notificações", "Notifications", "notificações avisos erros sucesso resumo lotes toast windows"),
        new("concurrency", "Operações simultâneas", "Limite instalações, atualizações, remoções e downloads concorrentes.", "Operações e downloads", "Execution", "paralelo simultâneas concorrência fila limites instalações"),
        new("installer-downloads", "Pasta e nomes dos instaladores", "Escolha o destino padrão e o formato dos arquivos baixados.", "Operações e downloads", "Execution", "download pasta destino nome arquivo versão arquitetura instalador"),
        new("winget-sources", "Fontes WinGet", "Liste, adicione e remova fontes de pacotes WinGet.", "Operações e downloads", "Execution", "fonte repositório winget origem adicionar remover listar"),
        new("portable-settings", "Exportar e importar configurações", "Transfira preferências de operações e downloads em um arquivo JSON.", "Operações e downloads", "Execution", "exportar importar migrar preferências configurações"),
        new("installer-host-warning", "Aviso de domínio do instalador", "Peça confirmação se o domínio do instalador mudar entre versões WinGet.", "Operações e downloads", "Execution", "segurança domínio host url instalador mudança alteração atualização"),
        new("scheduled-maintenance", "Manutenção agendada", "Verificações periódicas, atualizações automáticas, repetição e notificações do Windows.", "Manutenção agendada", "Maintenance", "cli agenda horario intervalo repetir notificar automatico atualização atualizações"),
        new("elevation-cache", "WinProvision Elevator", "Permita ou bloqueie pedidos, gerencie a sessão e verifique a integridade.", "WinProvision Elevator", "Elevator", "gsudo elevator administrador elevação uac autorização cache privilégios bloquear integridade"),
        new("install-method", "Método de instalação preferencial", "Escolha o método automático, WinGet COM, WinProvision API ou WinGet CLI.", "Preferências gerais", "General", "método instalar instalação pacote automático api com winget cli rota"),
        new("check-updates", "Verificação de atualizações", "Procure uma versão mais recente do WinProvision Store.", "Preferências gerais", "General", "atualização atualizar versão github verificar update"),
        new("open-logs", "Abrir pasta de logs", "Acesse os arquivos de log do aplicativo.", "Preferências gerais", "General", "log logs pasta diagnóstico arquivos"),
        new("reset-preferences", "Restaurar padrões", "Volte às preferências originais do aplicativo.", "Preferências gerais", "General", "restaurar padrões redefinir reset configurações"),
        new("theme", "Tema do aplicativo", "Alterne entre os temas claro, escuro e do sistema.", "Interface e inicialização", "Interface", "tema aparência claro escuro sistema cor"),
        new("startup", "Iniciar com o Windows", "Execute atualizações automáticas pela CLI ao entrar nesta conta, sem abrir a interface.", "Interface e inicialização", "Interface", "inicialização iniciar login windows automático"),
        new("clear-cache", "Armazenamento e cache", "Meça o espaço, ajuste limites e limpe imagens, catálogo ou instaladores reutilizáveis separadamente.", "Armazenamento e cache", "Operations", "cache armazenamento espaço disco temporários instaladores limpar imagens ícones limite tamanho megabytes revalidar memória"),
        new("refresh-catalog", "Atualizar catálogo", "Busque aplicativos e versões recentes sem apagar o cache de instaladores e imagens.", "Armazenamento e cache", "Operations", "catálogo atualizar sincronizar busca versões índices"),
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
