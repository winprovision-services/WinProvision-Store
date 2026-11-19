# WinProvision Store

Aplicativo para Windows que reúne pesquisa de aplicativos, instalações, atualizações, programas instalados, coleções, configuração do Office e uma interface de linha de comando.

Conheça o aplicativo no [site do WinProvision Store](https://winprovision-services.github.io/WinProvision-Store/).

O WinProvision Store foi desenvolvido com auxílio de inteligência artificial e pode conter erros. Se encontrar algum problema, relate-o nas [issues do projeto](https://github.com/winprovision-services/WinProvision-Store/issues).

![Tela inicial do WinProvision Store no tema escuro](docs/screenshots/inicio-escuro.png)

Veja mais telas do aplicativo na [galeria de capturas](docs/SCREENSHOTS.md).

## Instalação

Enquanto a publicação no WinGet está pendente, instale a versão mais recente para Windows x64 pelo PowerShell:

```powershell
$p="$env:TEMP\WinProvision-Setup.exe"; curl.exe -fL --retry 3 -o $p https://github.com/winprovision-services/WinProvision-Store/releases/latest/download/WinProvision.Store-Setup.exe; if($LASTEXITCODE -eq 0){Start-Process $p -Wait -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /MERGETASKS="clipath,desktopicon"'}
```

O instalador usa escopo de máquina e adiciona a CLI ao PATH do sistema por padrão; exige privilégios administrativos e pode ser executado por SYSTEM. O comando baixa o instalador oficial do GitHub e instala silenciosamente, sem reiniciar o Windows, com atalho na área de trabalho e a CLI no PATH. Esta forma curta não confere o SHA-256. Para baixar manualmente o instalador, a versão portátil ou os hashes, consulte as [releases](https://github.com/winprovision-services/WinProvision-Store/releases/latest).

Após a instalação, abra outro terminal para carregar o PATH atualizado:

```powershell
winprovision --version
```

## Recursos

**Gerenciadores de pacotes:** a interface usa WinGet e Chocolatey; a Microsoft Store aparece como origem do WinGet. Chocolatey é instalado sob demanda, com confirmação e autorização administrativa, ao ativar o gerenciador. A busca Chocolatey consulta o feed público Community; inventário, atualizações e operações usam o CLI instalado no sistema. As áreas do catálogo e serviços próprios de provisionamento estão suspensas na interface; as implementações e os contratos da API permanecem no repositório.

- Pesquise aplicativos por nome, consulte seus detalhes e copie o link público do pacote quando disponível.
- Instale, atualize, remova e liste aplicativos pelas fontes configuradas no WinGet.
- Veja ícones publicados na CDN do WinProvision. Em Descobrir, o app também pode buscar imagens dos manifestos oficiais do WinGet e conferir o SHA-256 publicado.
- Use detalhes do pacote, histórico de operações, temas, navegação por teclado, notificações e limites de operações simultâneas.
- Coleções, provisionamento do Windows, Office, conta e catálogo próprio estão temporariamente suspensos na interface; o compartilhamento LAN de instaladores está disponível nas Configurações de Desenvolvedor.
- Exporte e importe listas JSON pela CLI; consulte detalhes em JSON com `winprovision show --id ID --json`.
- Pesquise no histórico de operações por aplicativo, resultado, método ou texto do log.
- Agende verificações e atualizações WinGet pela CLI, em intervalos fixos ou agendas diária/semanal, com execução imediata e notificações do Windows.
- Configure notificações por categoria, pasta padrão e nomes dos instaladores nas Configurações.
- Em **Configurações de Desenvolvedor**, compartilhe uma pasta de instaladores na LAN com autenticação ou sincronize instaladores para cache local verificado por SHA-256 e instale por manifestos WinGet. HTTP não criptografa o token; use somente em uma rede confiável.

Instaladores podem solicitar autorização administrativa ou interação adicional. A disponibilidade de aplicativos depende das fontes consultadas. A preparação de mídia offline exige conexão durante o download.

## Catálogo remoto

O app lê o catálogo v2 assinado publicado em `https://cdn.winprovisionstore.com.br/winget/v2/`. A assinatura do estado, os hashes do SQLite Brotli/descomprimido e os vínculos entre resumo, app.json, detalhe e instalador são verificados antes do uso. O banco aceito fica em cache local e é aberto somente para leitura; a busca usa FTS5. O formato e os campos estão descritos em [DATA-FORMATS.md](docs/DATA-FORMATS.md#catálogo-remoto-v2).

## Linha de comando

O [WinProvision Viper](src/WinProvision.Viper/README.md) é um módulo HTTP(S) experimental e independente, com biblioteca e executável próprios para downloads segmentados e simultâneos. A biblioteca atende os downloads verificados de instaladores do catálogo, inclusive nas instalações pela API própria; o executável `winprovision-viper.exe` continua separado da CLI `winprovision`.

```powershell
winprovision search Chrome
winprovision install --id Google.Chrome
winprovision upgrade --all
winprovision export --output apps.json
winprovision import apps.json
winprovision --install --office
winprovision --office --offline
```

Consulte os comandos e opções em [CLI](docs/CLI.md).

## Desenvolvimento

O projeto utiliza .NET 10, C# 14, WinUI 3 e CommunityToolkit.Mvvm. Requer Windows e o SDK indicado em `global.json`. A publicação Native AOT requer as ferramentas MSVC C++ x64/x86 e o Windows SDK. A criação do instalador requer Inno Setup 6.3 ou superior.

```powershell
.\scripts\build.ps1
.\scripts\run.ps1
.\scripts\publish.ps1
.\scripts\package.ps1
```

A solução para abrir no Visual Studio é `WinProvision.Store.slnx` (a versão `.sln` permanece disponível). Para executar a interface, use `Debug | x64` e o perfil de inicialização compartilhado **WinProvision Store**. Se o Visual Studio tiver guardado a CLI como projeto inicial desta solução, escolha esse perfil na lista ao lado do botão Iniciar. A publicação padrão é x64 e autossuficiente, em `artifacts/publish/win-x64`; o worker usa o mesmo RID e passa por `--selftest` antes de ser aceito. Para ARM64, publique com `.\scripts\publish.ps1 -RuntimeIdentifier win-arm64` e empacote com `.\scripts\package.ps1 -RuntimeIdentifier win-arm64`; o instalador ARM64 fica em `artifacts/installer/win-arm64`.

## Validação

```powershell
dotnet test .\tests\WinProvision.Core.Tests\WinProvision.Core.Tests.csproj
.\scripts\smoke-test.ps1
.\scripts\updates-smoke-test.ps1 -Mode com
.\scripts\check-repository.ps1
```

O smoke verifica navegação, bindings e temas com dados de teste. Testes simulados não comprovam instalações reais. Saídas de desenvolvimento em `artifacts/validation` não fazem parte da distribuição.

`updates-smoke-test.ps1` abre a aba Atualizações com os serviços reais, consulta e recarrega a lista sem instalar pacotes, e grava versões, tempos e captura em `artifacts/validation/updates-real-<modo>`. Os modos `com`, `auto` e `cli` permitem conferir cada caminho; `-MinimumExpectedCount 1` exige resultados em uma máquina com atualizações conhecidas. `-SkipBuild` reutiliza o build de validação já compilado.

## Organização

| Pasta | Conteúdo |
| --- | --- |
| `src/WinProvision.Store` | Interface e integração com o Windows |
| `src/WinProvision.Core` | Modelos e funcionalidades compartilhadas do cliente |
| `src/WinProvision.Cli` | Executável de linha de comando |
| `src/WinProvision.PackageManagement` | Integração de pacotes compartilhada pelo app e pela CLI |
| `tests` | Testes automatizados e validação da interface |
| `Tool/gsudo` | Binário, licença e manifesto do helper distribuído |
| `scripts` | Compilação, execução, publicação e verificações |
| `docs` | Documentação do projeto |

## Dados e privacidade

O aplicativo mantém preferências, coleções, histórico, logs e caches no perfil do usuário. Consultas online e o backup opcional envolvem comunicação de dados. A [política de privacidade](PRIVACY.md) explica quais dados são usados, armazenados e transmitidos.

## Inspiração e agradecimentos

O WinProvision Store tem como fonte de inspiração o UniGetUI. Agradecemos à Devolutions e a Martí Climent pelo trabalho no projeto e pela inspiração para desenvolver esta aplicação.

## Documentação e participação

Consulte a [documentação](docs/README.md), as [orientações de contribuição](CONTRIBUTING.md), o [código de conduta](CODE_OF_CONDUCT.md), a [política de segurança](SECURITY.md) e o [histórico](CHANGELOG.md).

O código do projeto usa a [licença MIT](LICENSE). Os componentes utilizados mantêm suas próprias condições, descritas nos [avisos de terceiros](THIRD-PARTY-NOTICES.md).
