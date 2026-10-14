# Interface WinUI 3

Use **Atualizações** como referência visual, conforme o [README de padronização visual](docs/VISUAL-DESIGN.md). O guia detalha os recursos compartilhados, medidas, estados e validação das páginas.

Use controles nativos de Microsoft.UI.Xaml. Compartilhe estilos e recursos em `App.xaml` e utilize `ThemeResource` para cores. Mantenha textos em português, foco por teclado, rótulos de acessibilidade, legibilidade e adaptação da navegação em janelas menores.

Construa a apresentação com controles nativos WinUI preservando o comportamento. Estado e comandos pertencem aos ViewModels; seleção de arquivos e diálogos pertencem a serviços de apresentação.

Verifique as páginas nos temas claro/escuro e em largura compacta. Não exiba caminhos técnicos ou detalhes de implementação na experiência normal, exceto logs de diagnóstico solicitados pelo usuário.
