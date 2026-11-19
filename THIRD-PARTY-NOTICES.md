# Componentes de terceiros

O código do WinProvision Store é distribuído sob a licença MIT em `LICENSE`. As dependências e os materiais de terceiros mantêm suas próprias licenças e atribuições.

- WiX DTF Compression/Cab 5.0.2: licença MS-RL, aviso e fontes correspondentes em `src/WinProvision.Core/ThirdParty/WiX-DTF`. Esses arquivos acompanham a distribuição.
- SQLitePCLRaw 2.1.11 e biblioteca nativa `SQLitePCLRaw.lib.e_sqlite3` 2.1.14: licença Apache 2.0, incluída em `src/WinProvision.Core/ThirdParty/SQLitePCLRaw` e na distribuição. O mecanismo SQLite incorporado é disponibilizado em domínio público pelos seus autores; informações em https://www.sqlite.org/copyright.html. O provedor Microsoft.Data.Sqlite 10.0.0 é distribuído sob MIT.
- gsudo 2.6.1, de Gerardo Grignoli e colaboradores, usado como base do WinProvision Elevator 2.6.1.2: licença MIT original em `Tool/gsudo/LICENSE.txt`. O helper e a licença acompanham a interface e a CLI; origem, versão e hash estão em [Elevação de processos](docs/Elevation.md) e `Tool/gsudo/manifest.json`.
- Spectre.Console e Spectre.Console.Ansi 0.57.2: licença MIT, para apresentação da CLI; licença incluída nos pacotes NuGet. Projeto: https://github.com/spectreconsole/spectre.console.
- Pacotes NuGet: os componentes Microsoft, CommunityToolkit.Mvvm e demais dependências declaradas nos arquivos de projeto mantêm as licenças de seus respectivos pacotes.
- Microsoft.WindowsPackageManager.InProcCom 1.29.380: SDK oficial Microsoft sob MIT. As DLLs nativas verificadas e o `NOTICE.txt` do pacote acompanham cada host em `Tool/winget-com`.
- Materiais de marca: a licença da fonte Inter usada no wordmark está em `src/WinProvision.Store/Assets/ThirdParty/Inter-OFL.txt`.
- FluentIcons.WinUI 2.2.344: controle WinUI para Fluent UI System Icons sob MIT; licença do controle em `Tools/FluentUI/LICENSE.txt` e licença dos ícones da Microsoft em `Tools/FluentUI/FluentSystemIcons-LICENSE.txt`.
- O cabeçalho do painel de detalhes e o banner sem capturas foram adaptados de `PackageDetailsWindow.axaml` do UniGetUI, copyright (c) 2025 Martí Climent, sob MIT. O código original está em https://github.com/Devolutions/UniGetUI/blob/main/LICENSE; a adaptação WinUI mantém as ações e os dados próprios do WinProvision Store.
- A organização em abas e cartões e o mapeamento de opções por operação foram adaptados de `InstallOptionsControl.axaml` e seus modelos relacionados do UniGetUI, copyright (c) 2025 Martí Climent, sob MIT. O código original está em https://github.com/Devolutions/UniGetUI/blob/main/LICENSE; as opções foram conectadas aos serviços e fluxos próprios do WinProvision Store.
- A seleção de ações para o menu de contexto da vitrine foi adaptada da seção `GenerateContextMenu` de `DiscoverSoftwarePage.cs` do UniGetUI, copyright (c) 2025 Martí Climent, sob MIT. As ações usam os serviços existentes do WinProvision Store.
- O fluxo de preparação e cópia do comando manual teve como referência `ManualInstallHelper.cs` do UniGetUI, copyright (c) 2025 Martí Climent, sob MIT. O WinProvision Store mostra o comando no PowerShell e aguarda o usuário colá-lo e executá-lo.
- A composição de resultados Descobrir, com filtros laterais, seleção e modos de visualização, foi adaptada de `DiscoverSoftwarePage.cs` e `AbstractPackagesPage.axaml` do UniGetUI, copyright (c) 2025 Martí Climent, sob MIT. A adaptação WinUI mantém modelos, pesquisa e operações do WinProvision Store.
- A ordem da barra de ações, a tabela e o estado vazio da página Coleção de Pacotes foram adaptados de `PackageBundlesPage.cs` e `AbstractPackagesPage.axaml` do UniGetUI, copyright (c) 2025 Martí Climent, sob MIT. Os comandos continuam ligados aos serviços e formatos próprios do WinProvision Store.

Os ícones funcionais e de navegação da interface usam Fluent UI System Icons. As imagens e marcas dos aplicativos do catálogo permanecem fornecidas pelas respectivas fontes.

O SDK WebView2 mantém o texto de licença original em `src/WinProvision.Store/Assets/ThirdParty/WebView2-LICENSE.txt`. O Microsoft Edge WebView2 Runtime é um componente externo do sistema, com termos próprios; não é relicenciado como MIT por este projeto.

- A ilustração da coleção vazia `src/WinProvision.Store/Assets/Empty_inbox.png` foi copiada de `src/UniGetUI.Avalonia/Assets/Images/Empty_inbox.png` do UniGetUI, copyright (c) 2025 Martí Climent, sob MIT; consulte o [LICENSE](https://github.com/Devolutions/UniGetUI/blob/main/LICENSE) do repositório de origem.
- Os símbolos SVG originais da página Coleção foram copiados de `src/SharedAssets/Assets/Symbols` do UniGetUI, copyright (c) 2025 Martí Climent, sob MIT; a licença está no [LICENSE](https://github.com/Devolutions/UniGetUI/blob/main/LICENSE) do repositório de origem.
## Chocolatey logo

`src/WinProvision.Store/Assets/Icons/chocolatey.svg` is the Chocolatey brand icon from the Simple Icons collection, discovered through [Dashboard Icons](https://dashboardicons.com/icons/external/simpleicons/chocolatey) and served by [Simple Icons CDN](https://cdn.simpleicons.org/chocolatey/80B5E3). Dashboard Icons identifies the collection as CC0-1.0 while noting that individual artwork rights may differ. Chocolatey names and marks remain the property of their respective owners.

`src/WinProvision.Store/Assets/Icons/microsoft-store.svg` is the Microsoft Store icon from [selfh.st/icons](https://selfh.st/icons), indexed by [Dashboard Icons](https://dashboardicons.com/icons/external/selfhst/microsoft-store). It is distributed under CC BY 4.0; Microsoft and Microsoft Store names and marks remain the property of their respective owners.

`src/WinProvision.Store/Assets/Icons/microsoft-office.svg` is the colored Microsoft Office icon listed by [Dashboard Icons](https://dashboardicons.com/icons/microsoft-office), used for Office source and product identity across the app. The catalog entry does not provide license metadata. Microsoft and Office names and marks remain the property of their respective owners. The older monochrome adaptations are retained as unused assets.

`src/WinProvision.Store/Assets/Icons/winget.svg` is a vectorization of the Microsoft Windows Package Manager logo by PantheraLeo1359531, based on artwork by Microsoft and obtained from [Wikimedia Commons](https://commons.wikimedia.org/wiki/File:Windows_Package_Manager_logo.svg). The file is licensed under the Expat/MIT License; the original copyright and permission notice are in the source repository's [WinGet CLI license](https://github.com/microsoft/winget-cli/blob/master/LICENSE).

`src/WinProvision.Store/Assets/Icons/winget.png` is a local PNG rendering of that SVG from Wikimedia Commons, cropped to remove transparent canvas padding so its visible mark matches the square icon sizes used by the other package managers. It is used because the WinUI SVG renderer does not support all embedded elements in the source file.

