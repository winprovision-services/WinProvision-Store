# Movimento nativo WinUI 3

`Controls/FluentMotion.cs` fornece a propriedade anexada `Kind`, aplicada no XAML. Cada controle recebe sua própria coleção de transições; não existem timers de animação nem cache de navegação adicional.

- `Content`: `ContentThemeTransition` no host de páginas, com deslocamento vertical de 16 px.
- `Items`: `AddDeleteThemeTransition` e `RepositionThemeTransition` nas listas e grades de Coleções, Atualizações, Instalados e Mais. Reposicionamento sem cascata. ItemsRepeater da pesquisa mantém a virtualização e não recebe animações individuais por resultado.
- `Entrance` e `Panel`: entrada de estados vazios/carregamento, notificações e painel de operações, com deslocamentos de 12/24 px.
- Detalhes mantém seu storyboard de abertura/fechamento com opacity e TranslateTransform, sem forçar animações dependentes de layout. A preferência do Windows desativa o storyboard e preserva a restauração do foco ao fechar.

`UISettings.AnimationsEnabled` controla a aplicação das transições. Mudanças da preferência em tempo de execução são recebidas na thread da UI a partir do Windows 10 2004, com proteção de versão para manter o mínimo 17763. A inscrição é removida ao fechar a janela. Em versões anteriores, a preferência é lida na criação/carregamento dos controles e ao abrir/fechar Detalhes.

A implementação está em [FluentMotion.cs](../src/WinProvision.Store/Controls/FluentMotion.cs) e respeita os recursos de apresentação do cliente.

Validação: `scripts/build.ps1`, `scripts/smoke-test.ps1` e teste do executável publicado Native AOT. Os checks de movimento verificam coleções nativas, a configuração do Windows e remoção de transições ao desativar o comportamento. Capturas estáticas comprovam o estado final da apresentação; não medem fluidez ou FPS.

A validação utiliza `tests/WinProvision.Store.Smoke`. Os recursos de teste só entram em compilações explicitamente marcadas para desenvolvimento.
