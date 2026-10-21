# Componentes de terceiros

O código do WinProvision Store é distribuído sob a licença MIT em `LICENSE`. As dependências e os materiais de terceiros mantêm suas próprias licenças e atribuições.

- WiX DTF Compression/Cab 5.0.2: licença MS-RL, aviso e fontes correspondentes em `src/WinProvision.Core/ThirdParty/WiX-DTF`. Esses arquivos acompanham a distribuição.
- gsudo 2.6.1, de Gerardo Grignoli e colaboradores, usado como base do WinProvision Elevator 2.6.1.2: licença MIT original em `Tool/gsudo/LICENSE.txt`, fonte e alterações documentadas em [Tool/gsudo/README.md](Tool/gsudo/README.md). O helper e a licença acompanham a interface e a CLI.
- Spectre.Console e Spectre.Console.Ansi 0.57.2: licença MIT, para apresentação da CLI; licença incluída nos pacotes NuGet. Projeto: https://github.com/spectreconsole/spectre.console.
- Pacotes NuGet: os componentes Microsoft, CommunityToolkit.Mvvm e demais dependências declaradas nos arquivos de projeto mantêm as licenças de seus respectivos pacotes.
- Microsoft.WindowsPackageManager.InProcCom 1.29.380: SDK oficial Microsoft sob MIT. As DLLs nativas verificadas e o `NOTICE.txt` do pacote acompanham cada host em `Tool/winget-com`.
- Materiais de marca: a licença da fonte Inter usada no wordmark está em `src/WinProvision.Store/Assets/ThirdParty/Inter-OFL.txt`.
- FluentIcons.WinUI 2.2.344: controle WinUI para Fluent UI System Icons sob MIT; licença do controle em `Tools/FluentUI/LICENSE.txt` e licença dos ícones da Microsoft em `Tools/FluentUI/FluentSystemIcons-LICENSE.txt`.

Os ícones funcionais e de navegação da interface usam Fluent UI System Icons. As imagens e marcas dos aplicativos do catálogo permanecem fornecidas pelas respectivas fontes.

O SDK WebView2 mantém o texto de licença original em `src/WinProvision.Store/Assets/ThirdParty/WebView2-LICENSE.txt`. O Microsoft Edge WebView2 Runtime é um componente externo do sistema, com termos próprios; não é relicenciado como MIT por este projeto.
