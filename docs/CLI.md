# CLI do WinProvision Store

## Cache local do catálogo

`winprovision cache list` (ou apenas `cache`) informa tamanho, arquivos e pastas do catálogo bruto compartilhado pela UI/CLI. `winprovision cache prune` remove arquivos antigos e excedentes; `winprovision cache clear` remove essa cópia local, preservando a pasta compartilhada. Esses comandos não consultam R2 nem preparam o WinGet; não removem instaladores, imagens, perfis ou credenciais. A limpeza seletiva também mantém a pasta compartilhada dentro da política quando configurada.

Pasta adicional/rede, prazo (6/12/24 horas) e limite por cópia (128 MiB a 2 GiB; padrão 512 MiB) são salvos nas Configurações da interface e lidos na próxima execução. A cópia local permite uso sem a pasta de rede. O prazo é persistido entre processos; detalhes/índices com SHA inalterado não precisam ser baixados novamente. Consulte a [política de cache](ARCHITECTURE.md#cache-do-catálogo).

A CLI é o executável `winprovision`. O instalador oferece adicioná-lo ao PATH do usuário. Abra um novo terminal após a instalação.

Na distribuição portátil, mantenha o conteúdo completo da pasta `cli`, incluindo `Microsoft.Management.Deployment.winmd`, junto do executável. Esse arquivo de suporte é necessário à integração COM na publicação Native AOT.

A CLI executa o motor compartilhado do WinProvision em um processo de console, sem iniciar a janela, carregar WinUI 3 ou depender do executável da interface. Ela verifica o WinGet quando uma operação precisa dele e tenta provisioná-lo se estiver ausente, inclusive em uma sessão nova do Windows. Esse preparo exige conexão e pode levar alguns minutos. Ajuda e versão não iniciam downloads.

Instalações e atualizações compartilham o serviço do aplicativo: API COM, API própria e, por último, WinGet CLI. O fallback respeita cancelamento e não reinicia um instalador já iniciado. Se o provisionamento falhar, pacotes compatíveis do catálogo próprio ainda podem ser instalados com verificação de integridade.

Busca e detalhes consultam a COM e o catálogo próprio antes de recorrer ao executável WinGet. Downloads do catálogo próprio verificam SHA-256. Inventário e descoberta de atualizações usam os recursos locais do WinGet; exportação gera um perfil nativo do WinProvision. Recursos sem alternativa compatível dependem de o provisionamento do WinGet terminar com sucesso.

## Comandos

| Comando | Função |
| --- | --- |
| `search <nome>` | Pesquisar aplicativos sem instalar |
| `show --id ID` | Consultar detalhes |
| `list [nome]` | Listar aplicativos instalados |
| `install --id ID` | Instalar após confirmação |
| `download --id ID` | Baixar o instalador para Downloads |
| `upgrade` | Mostrar atualizações em um menu com índices |
| `upgrade --all` | Selecionar todas as atualizações e pedir confirmação |
| `uninstall --id ID` | Desinstalar o identificador exato após confirmação |
| `export` | Exportar a lista reconhecida de aplicativos para JSON |
| `import arquivo.json` | Instalar os aplicativos da lista após confirmação |
| `import arquivo.json --download` | Apenas baixar os instaladores da lista, após confirmação |
| `office` | Baixar a ferramenta, o XML e o script do Office |
| `office --offline` | Baixar também a mídia completa do Office |
| `install --office` | Instalar o Office padrão após confirmação |
| `help [comando]` | Exibir ajuda geral ou específica |

## Exemplos

```powershell
winprovision search Chrome
winprovision search Chrome --json
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
| `--json` | Resultado estruturado em pesquisa, listagem e consulta de atualizações |
| `--no-input` | Consultar sem menus; junto de `--yes`, executar a operação sem interação |
| `--elevation-cache` | Aceito por compatibilidade; o cache de 5 minutos de inatividade já é automático |
| `-y`, `--yes` | Confirmar a operação sem exibir menus; upgrade exige `--id` ou `--all` |
| `--all` | Selecionar todas as atualizações, sem combinar filtros |
| `-i`, `--input` | Arquivo JSON ou URL HTTPS direta para importação |
| `-o`, `--output` | Arquivo de exportação ou pasta de download |
| `--download` | Apenas baixar os aplicativos de uma importação |
| `--offline` | Preparar a mídia completa no comando Office |
| `-h`, `--help` | Ajuda |
| `-V`, `--version` | Versão do executável |

O modo JSON apenas consulta e não inicia instalações. `--all` não combina com filtros nem JSON; aceita `--no-input` junto de `--yes`. Use `--yes` (ou `-y`) em operações para executar sem menus nem confirmação. Em atualizações, informe `--id` ou `--all`; a CLI não escolhe um resultado ambíguo. Sem essa opção, as confirmações interativas são preservadas. Com entrada redirecionada, operações que exigem confirmação falham com código 2 antes de consultar ou instalar; use `--yes` para executar ou `--no-input`/`--json` para apenas consultar. `--yes` não combina com `--json`.

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

Os comandos `office` e `office --offline` apenas baixam. Para instalar, use `install --office`, que pede confirmação.

O XML padrão utiliza Microsoft 365 Família, 64 bits, português do Brasil, canal Current, atualizações automáticas e interface de instalação oculta. Access e Publisher ficam excluídos.

Cada download cria uma pasta própria em Downloads. O conjunto inclui `setup.exe`, `configuration.xml` e `Install-Office.ps1`. No modo offline, inclui também a mídia completa e seu inventário de hashes.

Execute `Install-Office.ps1 -VerifyOnly` para conferir os arquivos sem instalar. O download offline exige internet inicialmente e pode ocupar vários GB. `--offline` não combina com `install --office`.

Consulte [Office online e offline](OFFICE-OFFLINE.md).

## Interação

- `0` ou fim da entrada sai do menu sem confirmar uma operação.
- `Ctrl+C` solicita cancelamento.
- `N` e `P` navegam pelas páginas dos menus.
- Lotes mostram progresso e um resumo de conclusões e falhas.

Instaladores podem pedir autorização administrativa ou interação própria.

## Saída e códigos

Resultados são escritos em stdout; diagnósticos e progresso, em stderr. A saída JSON de consultas contém `id`, `name`, `version`, `availableVersion` e `source`.

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

A CLI não exige inicialização da interface gráfica. O provisionamento de pacotes depende da rede, das permissões e dos serviços disponíveis na sessão do usuário. Um teste em uma sessão comum ou no Windows Sandbox não comprova funcionamento durante o FirstLogon de uma imagem personalizada. Para executar instalações e remoções sem entrada interativa, informe `--yes` explicitamente. Sem essa opção, a CLI preserva as confirmações e rejeita operações com entrada redirecionada.

## Automação

```powershell
winprovision install --id Google.Chrome --yes
winprovision upgrade --all --yes
winprovision uninstall --id Google.Chrome --yes
winprovision import apps.json --yes
winprovision install --office --yes
```

`--yes` elimina os prompts da CLI, preservando os códigos de saída e o cancelamento. Instaladores que precisam de elevação ainda dependem dos privilégios da sessão; a opção não contorna o UAC nem garante que todo instalador de terceiros seja autônomo. A remoção pelo fallback WinGet também solicita modo silencioso.

Antes de usar a COM, o motor verifica sua ativação, mesmo quando `winget --version` já funciona. Em caso de falha, tenta registrar o App Installer para o usuário atual e depois reinstalar o pacote e suas dependências oficiais. A COM é verificada novamente após cada etapa; falha no reparo preserva a cadeia API própria/CLI. O reparo é limitado a uma tentativa por instância do motor, não é iniciado por erros de um pacote ou de conexão ao catálogo e respeita cancelamento. Em SYSTEM, a indisponibilidade da COM preserva o fallback sem tentar registro por usuário.

### Cache de elevação

```powershell
winprovision import apps.json --yes --elevation-cache
winprovision upgrade --all --yes --elevation-cache
```

O cache de 5 minutos é automático, sem opção de desativação ou mudança de duração, e é solicitado apenas quando uma operação precisa do helper elevado. A primeira autorização continua passando pelo UAC. A permissão vale para o processo da CLI e seus descendentes, expira após 5 minutos de inatividade e é invalidada ao encerrar a execução. Não é compartilhada globalmente com o terminal ou com o gsudo instalado no sistema. `--elevation-cache` permanece aceito por compatibilidade e é redundante. Use `--yes` para dispensar as confirmações da CLI.

Solicitações de elevação feitas diretamente pela COM e por instaladores que elevam a si próprios não reutilizam necessariamente esse cache. Consulte a [integração gsudo](../Tool/gsudo/README.md).

### Execução automática no Windows

A opção “Iniciar com o Windows” do aplicativo executa a CLI em segundo plano para atualizar aplicativos, sem abrir a interface. Usa as preferências de atualização salvas, os aplicativos selecionados, as versões ignoradas e as notificações do Windows. O UAC permanece interativo na primeira operação administrativa; após autorização, o Elevator reutiliza o cache de 5 minutos de inatividade entre os aplicativos do lote. O comando interno `--background-update` é reservado à inicialização e às tarefas agendadas. O log fica em `%LOCALAPPDATA%/WinProvisionStoreWinUI3/logs/background-update.log`.
