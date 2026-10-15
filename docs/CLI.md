# CLI do WinProvision Store

A CLI é o executável `winprovision`. O instalador oferece adicioná-lo ao PATH do usuário. Abra um novo terminal após a instalação.

Na distribuição portátil, mantenha o conteúdo completo da pasta `cli`, incluindo `Microsoft.Management.Deployment.winmd`, junto do executável. Esse arquivo de suporte é necessário à integração COM na publicação Native AOT.

A CLI verifica o WinGet quando uma operação precisa dele e tenta provisioná-lo se estiver ausente, inclusive em uma sessão nova do Windows. Esse preparo exige conexão e pode levar alguns minutos. Ajuda e versão não iniciam downloads.

Instalações e atualizações compartilham o serviço do aplicativo: API COM, API própria e, por último, WinGet CLI. O fallback respeita cancelamento e não reinicia um instalador já iniciado. Se o provisionamento falhar, pacotes compatíveis do catálogo próprio ainda podem ser instalados com verificação de integridade.

Busca e detalhes consultam a COM e o catálogo próprio antes de recorrer ao executável WinGet. Downloads do catálogo próprio verificam SHA-256. Inventário e descoberta de atualizações usam os recursos locais do WinGet; exportação conserva o formato oficial. Recursos sem alternativa compatível dependem de o provisionamento do WinGet terminar com sucesso.

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
| `--no-input` | Consultar sem menus nas operações de leitura |
| `--all` | Selecionar todas as atualizações, sem combinar filtros |
| `-i`, `--input` | Arquivo JSON ou URL HTTPS direta para importação |
| `-o`, `--output` | Arquivo de exportação ou pasta de download |
| `--download` | Apenas baixar os aplicativos de uma importação |
| `--offline` | Preparar a mídia completa no comando Office |
| `-h`, `--help` | Ajuda |
| `-V`, `--version` | Versão do executável |

O modo JSON apenas consulta e não inicia instalações. `--all` não combina com filtros, JSON ou `--no-input`. Operações de instalação e remoção não aceitam confirmação automática.

## Listas JSON

A importação aceita uma lista com identificadores completos:

```json
[
  { "id": "Google.Chrome", "source": "winget" },
  { "id": "Valve.Steam", "source": "winget" }
]
```

A fonte omitida significa `winget`. Também são aceitos arquivos de exportação com `Sources`, `SourceDetails.Name` e `Packages.PackageIdentifier`.

A lista inteira é validada antes das operações. Duplicatas são removidas. A importação utiliza as versões atuais disponíveis; versões registradas em arquivos externos não são fixadas.

O limite é de 2 MiB e 1.000 entradas. URLs devem ser HTTPS diretas, sem redirecionamentos. Um arquivo de coleção ou backup não é tratado automaticamente como lista de aplicativos.

A exportação inclui os aplicativos que podem ser identificados nas fontes consultadas. O nome padrão é `winprovision-apps.json`, na pasta atual. Arquivos existentes são preservados; escolha outro destino para uma nova exportação.

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
