# CLI do WinProvision Store

A CLI é o executável `winprovision`. O instalador instala para todos os usuários e adiciona a CLI ao PATH do sistema por padrão, com autorização administrativa. `/CURRENTUSER` mantém suporte a instalações por usuário e registra o PATH nesse escopo. Abra um novo terminal após a instalação.

Na distribuição portátil, mantenha o conteúdo completo da pasta `cli`, incluindo `Microsoft.Management.Deployment.winmd`, junto do executável. Esse arquivo de suporte é necessário à integração COM na publicação Native AOT.

A CLI executa o motor compartilhado do WinProvision em um processo de console, sem iniciar a janela, carregar WinUI 3 ou depender do executável da interface. Ela verifica o WinGet quando uma operação precisa dele e tenta provisioná-lo se estiver ausente, inclusive em uma sessão nova do Windows. Esse preparo exige conexão e pode levar alguns minutos. Ajuda e versão não iniciam downloads.

Instalações e atualizações compartilham o serviço do aplicativo: API COM, API própria e, por último, WinGet CLI. O fallback respeita cancelamento e não reinicia um instalador já iniciado. Se o provisionamento falhar, pacotes compatíveis do catálogo próprio ainda podem ser instalados com verificação de integridade.

Busca e detalhes consultam a COM e o catálogo próprio antes de recorrer ao executável WinGet. Downloads do catálogo próprio verificam SHA-256. Inventário e descoberta de atualizações usam os recursos locais do WinGet; exportação gera um perfil nativo do WinProvision. Recursos sem alternativa compatível dependem de o provisionamento do WinGet terminar com sucesso.

## Comandos

| Comando | Função |
| --- | --- |
| `search <nome>` | Pesquisar aplicativos sem instalar |
| `show --id ID` | Consultar detalhes; `--json` retorna um envelope estruturado |
| `list [nome]` | Listar aplicativos instalados |
| `install --id ID` | Instalar diretamente |
| `download --id ID` | Baixar o instalador para a pasta configurada ou Downloads |
| `upgrade` | Atualizar todos os aplicativos com atualização disponível |
| `upgrade --all` | Atualizar todos os aplicativos diretamente |
| `uninstall --id ID` | Desinstalar o identificador exato diretamente |
| `export` | Exportar a lista reconhecida de aplicativos para JSON |
| `import arquivo.json` | Instalar os aplicativos da lista diretamente |
| `import arquivo.json --download` | Apenas baixar os instaladores da lista |
| `office` | Baixar a ferramenta, o XML e o script do Office |
| `office --offline` | Baixar também a mídia completa do Office |
| `install --office` | Instalar o Office padrão diretamente |
| `help [comando]` | Exibir ajuda geral ou específica |

## Exemplos

```powershell
winprovision search Chrome
winprovision search Chrome --json
winprovision show --id Google.Chrome --json
winprovision install --id Google.Chrome
winprovision download --id Google.Chrome --output D:\Instaladores
winprovision upgrade --all
winprovision uninstall --id Google.Chrome
winprovision export --output apps.json
winprovision import apps.json
winprovision import apps.json --download
winprovision import --input https://exemplo.com/apps.json
winprovision --install --office
winprovision --office --offline
winprovision help import
winprovision --version
```

## Opções

| Opção | Uso |
| --- | --- |
| `--id ID` | Identificador completo do aplicativo |
| `-s`, `--source` | Fonte: `winget` ou `msstore` |
| `-q`, `--query` | Consulta por nome; também aceita texto posicional |
| `-e`, `--exact` | Correspondência exata |
| `--json` | Resultado estruturado em pesquisa, listagem, consulta de atualizações e detalhes |
| `--no-input` | Em `upgrade`, apenas consultar; demais comandos executam sem menus |
| `--elevation-cache` | Aceito por compatibilidade; o cache de 5 minutos de inatividade já é automático |
| `--all` | Selecionar todas as atualizações, sem combinar filtros |
| `-i`, `--input` | Arquivo JSON ou URL HTTPS direta para importação |
| `-o`, `--output` | Arquivo de exportação ou pasta de download |
| `--download` | Apenas baixar os aplicativos de uma importação |
| `--offline` | Preparar a mídia completa no comando Office |
| `-h`, `--help` | Ajuda |
| `-V`, `--version` | Versão do executável |

Instalação, importação, atualização e desinstalação executam diretamente, sem menus ou confirmação da CLI. `upgrade` atualiza todos os resultados; use `--id ID` para limitar a um aplicativo ou um nome para filtrar. `--json` e `upgrade --no-input` permanecem apenas consultas. As operações também executam diretamente com entrada redirecionada. `--yes` e `-y` foram removidos; retire essas opções dos scripts existentes.

`import arquivo.json` instala. `import arquivo.json --download` apenas baixa; remova `--download` para instalar. O terminal mostra o aplicativo em execução, conclusão ou falha e o resumo do lote. Detalhes técnicos ficam em `%LOCALAPPDATA%/WinProvisionStoreWinUI3/logs/cli`; `winprovision logs` mostra essa pasta. Os diagnósticos são separados por processo e têm dados sensíveis ocultados. Instaladores ainda podem exigir autorização do Windows.

Downloads avulsos respeitam a pasta e o formato de nomes salvos em **Configurações → Operações e downloads**. `--output` tem prioridade; sem configuração, usa Downloads. Nomes repetidos recebem um número, sem substituir arquivos existentes. Versão e arquitetura são usadas quando conhecidas; caso contrário, preserva-se o nome nativo junto do ID. Lotes da CLI continuam sequenciais. Downloads e mídia do Office mantêm seus destinos e layout próprios.

## Listas JSON

A importação aceita uma lista com identificadores completos:

```json
[
  { "id": "Google.Chrome", "source": "winget" },
  { "id": "Valve.Steam", "source": "winget" }
]
```

A fonte omitida significa `winget`. Também são aceitos arquivos de exportação com `Sources`, `SourceDetails.Name` e `Packages.PackageIdentifier`.

Também são aceitos perfis nativos com `apps` e backups locais ou de nuvem com `tabs`, exportados pelo próprio aplicativo. `pinnedVersion` e `officeOptions` dos perfis nativos são preservados na execução. Metadados de provisionamento são preservados nas coleções, mas não são executados pela importação de aplicativos.

A lista inteira é validada antes das operações. Entradas repetidas com as mesmas opções são executadas uma vez. Opções conflitantes entre coleções interrompem a importação antes de qualquer instalação. Para listas simples e exportações WinGet, são usadas as versões atuais.

O limite é de 16 MiB e 10.000 aplicativos. URLs devem usar HTTPS; são aceitos até cinco redirecionamentos, sempre em HTTPS. Links de Gist são resolvidos para o arquivo de perfil; se houver vários JSONs sem um perfil identificado, informe a URL raw específica.

A exportação escreve um perfil nativo com `schemaVersion`, `createdAt`, `name` e `apps`, aceito pelas coleções do app e pela CLI. Ela inclui os aplicativos que podem ser identificados nas fontes consultadas. O nome padrão é `winprovision-apps.json`, na pasta atual. Arquivos existentes são preservados; escolha outro destino para uma nova exportação.

## Office

Os comandos `office` e `office --offline` apenas baixam. Para instalar, use `install --office`, que instala diretamente.

O XML padrão utiliza Microsoft 365 Família, 64 bits, português do Brasil, canal Current, atualizações automáticas e interface de instalação oculta. Access e Publisher ficam excluídos.

Cada download cria uma pasta própria em Downloads. O conjunto inclui `setup.exe`, `configuration.xml` e `Install-Office.ps1`. No modo offline, inclui também a mídia completa e seu inventário de hashes.

Execute `Install-Office.ps1 -VerifyOnly` para conferir os arquivos sem instalar. O download offline exige internet inicialmente e pode ocupar vários GB. `--offline` não combina com `install --office`.

Consulte [Office online e offline](OFFICE-OFFLINE.md).

## Interação

Todos os comandos públicos compartilham o renderizador Spectre.Console: títulos e spinner azuis, resultados verdes, falhas vermelhas e cancelamento amarelo. Pesquisa, listagem e consulta de atualizações usam tabelas com aplicativo, ID, versão e origem; atualizações distinguem a versão instalada da disponível. Ajuda, detalhes, versão, logs e destinos de arquivos usam painéis. Lotes usam o mesmo cabeçalho e resumo de conclusões, falhas e reinicializações. Falhas e cancelamento são enviados a stderr; o JSON de consultas permanece exclusivo em stdout. Redirecionamentos permanecem sem ANSI; scripts que precisam de campos estáveis devem usar `--json` nas consultas compatíveis.

A apresentação interativa da CLI usa Spectre.Console: barra verde, spinner azul e resultado fixo por aplicativo. A barra mede aplicativos concluídos no lote; operações sem porcentagem disponível usam animação indeterminada. A barra temporária desaparece ao terminar ou cancelar, preservando o histórico do terminal. Com saída redirecionada ou terminal sem suporte, usa texto simples, sem códigos ANSI. JSON não passa pelo renderizador de progresso.

- `Ctrl+C` solicita cancelamento.
- Lotes mostram progresso e um resumo de conclusões e falhas.

Instaladores podem pedir autorização administrativa ou interação própria.

## Saída e códigos

Resultados são escritos em stdout; status de consulta e falhas resumidas, em stderr. Diagnósticos técnicos são gravados em arquivo. A saída JSON de pesquisa, listagem e atualizações contém `id`, `name`, `version`, `availableVersion` e `source`. `show --json` retorna um objeto com `id`, `source` e `details`; `details` preserva como texto a resposta detalhada da fonte consultada.

Somente falhas exibem uma causa curta em português (por exemplo, falta de espaço, erro de download ou hash divergente). Causas conhecidas usam a classificação compartilhada; falhas sem causa identificada mostram o código retornado, quando disponível. Saída bruta de serviços, caminhos de exceções e detalhes técnicos permanecem nos logs, acessíveis por `winprovision logs`. Sucesso, cancelamento, JSON e códigos de saída mantêm seu comportamento.

| Código | Significado |
| --- | --- |
| `0` | Sucesso, saída normal ou consulta vazia |
| `1` | Falha de consulta ou operação |
| `2` | Comando ou opção inválida |
| `130` | Cancelamento |

## Formas alternativas

Os aliases `find`, `info`, `ls` e `update` continuam disponíveis. A CLI também aceita `list update`, `list --upgrade-available` e o separador inicial `--`.

As formas `--install`, `--search`, `--import`, `--download`, `--export`, `--upgrade`, `--update`, `--uninstall` e `--office` são aceitas. `-id` equivale a `--id`; `-all`, a `--all`. Em remoção, `-d` identifica o aplicativo; em download, indica a pasta de destino.

Prefira os comandos principais e as opções longas em novos scripts.

## Execução no primeiro logon

A CLI não exige inicialização da interface gráfica. O provisionamento de pacotes depende da rede, das permissões e dos serviços disponíveis na sessão do usuário. Um teste em uma sessão comum ou no Windows Sandbox não comprova funcionamento durante o FirstLogon de uma imagem personalizada. Instalações, atualizações e remoções não leem entrada interativa, inclusive com entrada redirecionada.

## Automação

```powershell
winprovision install --id Google.Chrome
winprovision upgrade --all
winprovision uninstall --id Google.Chrome
winprovision import apps.json
winprovision install --office
```

A execução direta preserva os códigos de saída e o cancelamento. Instaladores que precisam de elevação ainda dependem dos privilégios da sessão; a execução direta não contorna o UAC nem garante que todo instalador de terceiros seja autônomo. A remoção pelo fallback WinGet também solicita modo silencioso.

Antes de usar a COM, o motor verifica sua ativação, mesmo quando `winget --version` já funciona. Em caso de falha, tenta registrar o App Installer para o usuário atual e depois reinstalar o pacote e suas dependências oficiais. A COM é verificada novamente após cada etapa; falha no reparo preserva a cadeia API própria/CLI. O reparo é limitado a uma tentativa por instância do motor, não é iniciado por erros de um pacote ou de conexão ao catálogo e respeita cancelamento. Em SYSTEM, a indisponibilidade da COM preserva o fallback sem tentar registro por usuário.

### Cache de elevação

```powershell
winprovision import apps.json --elevation-cache
winprovision upgrade --all --elevation-cache
```

O cache de 5 minutos é automático, sem opção de desativação ou mudança de duração, e é solicitado apenas quando uma operação precisa do helper elevado. A primeira autorização continua passando pelo UAC. A permissão vale para o processo da CLI e seus descendentes, expira após 5 minutos de inatividade e é invalidada ao encerrar a execução. Não é compartilhada globalmente com o terminal ou com o gsudo instalado no sistema. `--elevation-cache` permanece aceito por compatibilidade e é redundante. A desinstalação também executa diretamente.

Solicitações de elevação feitas diretamente pela COM e por instaladores que elevam a si próprios não reutilizam necessariamente esse cache. Consulte a [integração gsudo](../Tool/gsudo/README.md).

### Execução automática no Windows

Configure verificações e instalações em **Configurações → Manutenção agendada**. Escolha apenas avisar ou instalar, todos os aplicativos compatíveis ou somente os selecionados, execução ao entrar no Windows, intervalo fixo de 15 minutos a 24 horas, agenda diária ou semanal. As tarefas periódicas usam o Agendador de Tarefas do Windows e executam com a interface fechada enquanto esta conta estiver conectada. O intervalo de 24 horas corresponde a uma execução diária no horário da configuração.

O botão **Executar agora** usa a política salva, inclusive com a agenda desativada. Primeiro salve as opções desejadas. A página mostra a última execução registrada e a próxima prevista; suspensão, desligamento ou saída da conta podem alterar a execução efetiva. Operações simultâneas são impedidas pelo mutex do executor.

As notificações nativas do Windows podem ser desativadas nessa página. Verificações sem atualizações não notificam; consultas periódicas com o mesmo conjunto de versões não repetem o aviso. O comando interno `--background-update-now` é reservado ao botão de execução imediata. Ambos os comandos de segundo plano executam sem perguntas da CLI e preservam o UAC quando necessário.

A opção “Iniciar com o Windows” do aplicativo executa a CLI em segundo plano para atualizar aplicativos, sem abrir a interface. Usa as preferências de atualização salvas, os aplicativos selecionados, as versões ignoradas e as notificações do Windows. O UAC permanece interativo na primeira operação administrativa; após autorização, o Elevator reutiliza o cache de 5 minutos de inatividade entre os aplicativos do lote. O comando interno `--background-update` é reservado à inicialização e às tarefas agendadas. O log fica em `%LOCALAPPDATA%/WinProvisionStoreWinUI3/logs/background-update.log`.
