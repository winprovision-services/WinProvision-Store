# CLI do WinProvision Store

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
| `install --id ID` | Instalar diretamente |
| `download --id ID` | Baixar o instalador para Downloads |
| `upgrade` | Atualizar todos os aplicativos com atualização disponível |
| `upgrade --all` | Atualizar todos os aplicativos diretamente |
| `uninstall --id ID` | Desinstalar o identificador exato após confirmação |
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
| `-y`, `--yes` | Dispensar confirmação na desinstalação; compatibilidade nas demais operações |
| `--all` | Selecionar todas as atualizações, sem combinar filtros |
| `-i`, `--input` | Arquivo JSON ou URL HTTPS direta para importação |
| `-o`, `--output` | Arquivo de exportação ou pasta de download |
| `--download` | Apenas baixar os aplicativos de uma importação |
| `--offline` | Preparar a mídia completa no comando Office |
| `-h`, `--help` | Ajuda |
| `-V`, `--version` | Versão do executável |

Instalação, importação e atualização executam diretamente, sem menus ou confirmação da CLI. `upgrade` atualiza todos os resultados; use `--id ID` para limitar a um aplicativo ou um nome para filtrar. `--json` e `upgrade --no-input` permanecem apenas consultas. A desinstalação pede confirmação; `uninstall --id ID --yes` confirma explicitamente em automações. `--yes` continua aceito nas demais operações por compatibilidade. `--yes` não combina com `--json`.

`import arquivo.json` instala. `import arquivo.json --download` apenas baixa; remova `--download` para instalar. O terminal mostra o aplicativo em execução, conclusão ou falha e o resumo do lote. Detalhes técnicos ficam em `%LOCALAPPDATA%/WinProvisionStoreWinUI3/logs/cli`; `winprovision logs` mostra essa pasta. Os diagnósticos são separados por processo e têm dados sensíveis ocultados. Instaladores ainda podem exigir autorização do Windows.

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

A apresentação interativa da CLI usa Spectre.Console: barra verde, spinner azul e resultado fixo por aplicativo. A barra mede aplicativos concluídos no lote; operações sem porcentagem disponível usam animação indeterminada. A barra temporária desaparece ao terminar ou cancelar, preservando o histórico do terminal. Com saída redirecionada ou terminal sem suporte, usa texto simples, sem códigos ANSI. JSON não passa pelo renderizador de progresso.

- `0` ou fim da entrada sai do menu sem confirmar uma operação.
- `Ctrl+C` solicita cancelamento.
- Lotes mostram progresso e um resumo de conclusões e falhas.

Instaladores podem pedir autorização administrativa ou interação própria.

## Saída e códigos

Resultados são escritos em stdout; status de consulta e falhas resumidas, em stderr. Diagnósticos técnicos são gravados em arquivo. A saída JSON de consultas contém `id`, `name`, `version`, `availableVersion` e `source`.

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

A CLI não exige inicialização da interface gráfica. O provisionamento de pacotes depende da rede, das permissões e dos serviços disponíveis na sessão do usuário. Um teste em uma sessão comum ou no Windows Sandbox não comprova funcionamento durante o FirstLogon de uma imagem personalizada. Instalações e atualizações não leem entrada interativa. Para remoções com entrada redirecionada, informe `--yes` explicitamente.

## Automação

```powershell
winprovision install --id Google.Chrome --yes
winprovision upgrade --all --yes
winprovision uninstall --id Google.Chrome --yes
winprovision import apps.json --yes
winprovision install --office --yes
```

A execução direta preserva os códigos de saída e o cancelamento. `--yes` dispensa a confirmação da desinstalação. Instaladores que precisam de elevação ainda dependem dos privilégios da sessão; a opção não contorna o UAC nem garante que todo instalador de terceiros seja autônomo. A remoção pelo fallback WinGet também solicita modo silencioso.

Antes de usar a COM, o motor verifica sua ativação, mesmo quando `winget --version` já funciona. Em caso de falha, tenta registrar o App Installer para o usuário atual e depois reinstalar o pacote e suas dependências oficiais. A COM é verificada novamente após cada etapa; falha no reparo preserva a cadeia API própria/CLI. O reparo é limitado a uma tentativa por instância do motor, não é iniciado por erros de um pacote ou de conexão ao catálogo e respeita cancelamento. Em SYSTEM, a indisponibilidade da COM preserva o fallback sem tentar registro por usuário.

### Cache de elevação

```powershell
winprovision import apps.json --yes --elevation-cache
winprovision upgrade --all --yes --elevation-cache
```

O cache de 5 minutos é automático, sem opção de desativação ou mudança de duração, e é solicitado apenas quando uma operação precisa do helper elevado. A primeira autorização continua passando pelo UAC. A permissão vale para o processo da CLI e seus descendentes, expira após 5 minutos de inatividade e é invalidada ao encerrar a execução. Não é compartilhada globalmente com o terminal ou com o gsudo instalado no sistema. `--elevation-cache` permanece aceito por compatibilidade e é redundante. Use `--yes` para dispensar a confirmação da desinstalação.

Solicitações de elevação feitas diretamente pela COM e por instaladores que elevam a si próprios não reutilizam necessariamente esse cache. Consulte a [integração gsudo](../Tool/gsudo/README.md).

### Execução automática no Windows

A opção “Iniciar com o Windows” do aplicativo executa a CLI em segundo plano para atualizar aplicativos, sem abrir a interface. Usa as preferências de atualização salvas, os aplicativos selecionados, as versões ignoradas e as notificações do Windows. O UAC permanece interativo na primeira operação administrativa; após autorização, o Elevator reutiliza o cache de 5 minutos de inatividade entre os aplicativos do lote. O comando interno `--background-update` é reservado à inicialização e às tarefas agendadas. O log fica em `%LOCALAPPDATA%/WinProvisionStoreWinUI3/logs/background-update.log`.
