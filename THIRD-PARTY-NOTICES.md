# Componentes de terceiros

O código do WinProvision Store é distribuído sob a licença MIT em `LICENSE`. As dependências e os materiais de terceiros mantêm suas próprias licenças e atribuições.

- WiX DTF Compression/Cab 5.0.2: licença MS-RL, aviso e fontes correspondentes em `src/WinProvision.Core/ThirdParty/WiX-DTF`. Esses arquivos acompanham a distribuição.
- gsudo 2.6.1, de Gerardo Grignoli e colaboradores, usado como base do WinProvision Elevator 2.6.1.2: licença MIT original em `Tool/gsudo/LICENSE.txt`, fonte e alterações documentadas em [Tool/gsudo/README.md](Tool/gsudo/README.md). O helper e a licença acompanham a interface e a CLI.
- Pacotes NuGet: os componentes Microsoft, CommunityToolkit.Mvvm e demais dependências declaradas nos arquivos de projeto mantêm as licenças de seus respectivos pacotes.
- Materiais de marca: a licença da fonte Inter usada no wordmark está em `src/WinProvision.Store/Assets/ThirdParty/Inter-OFL.txt`.

Os ícones da interface usam os recursos nativos do Windows/WinUI. O cliente não distribui fontes de ícones extraídas de outras bibliotecas.

O SDK WebView2 mantém o texto de licença original em `src/WinProvision.Store/Assets/ThirdParty/WebView2-LICENSE.txt`. O Microsoft Edge WebView2 Runtime é um componente externo do sistema, com termos próprios; não é relicenciado como MIT por este projeto.
