# Histórico de alterações

## Não lançado

## 1.0.15 - 25 de outubro de 2026

- Corrigidos falsos erros na confirmação de atualização do Anki e de outras migrações MSI: o cliente lê ProductCode e ProductVersion do instalador com SHA-256 validado, em modo somente leitura, sem depender de um ProductCode incorreto no catálogo. Se a chave antiga desapareceu, a identidade comprovada do MSI pode confirmar o novo registro mesmo em outro escopo, preservando a arquitetura e exigindo versão igual ou superior ao alvo. Formatos como `26.9.3` e `26.09.3` são equivalentes. A confirmação observa até três snapshots do Registro e não executa novamente o instalador; um resultado já confirmado não repete a conferência nem incrementa outra vez o contador de tentativas.

- Corrigido o download segmentado de instaladores com seleção de espelhos, como VLC: as partes reutilizam o destino resolvido pela sondagem, sem consultar novamente o seletor a cada requisição e sem encaminhar credenciais a outra origem. Espelhos anunciados em HTTP na porta padrão são tentados exclusivamente pelo endpoint HTTPS correspondente, mantendo validação TLS e SHA-256. Cancelamentos internos do transporte sem cancelamento do solicitante passam por repetição limitada e são reportados como timeout; a API classifica esse timeout antes do instalador como falha de download.

- O inventário da API usa a precedência de versões ARP do WinGet como referência (`DisplayVersion`, pares major/minor e DWORD empacotado), mantendo consultas diretamente no Windows. A arquitetura MSI vem do pacote registrado, sem confundir o bootstrapper x86 com o produto x64. Bundles visíveis e seus filhos MSI ocultos da mesma release são consolidados; instalações x86/x64 e escopos distintos continuam separados. Isso corrige a seleção redundante do PowerShell x86 quando a instalação x64 ainda está antiga.
- Antes de despachar uma atualização, o motor relê o inventário. Uma versão igual ou superior ao alvo conclui sem executar instalador e sem contabilizar tentativa de atualização; a preparação da API também relê a instalação. A comparação numérica reconhece equivalências como Anki `26.9.3`/`26.09.3` e a conferência de migração preserva o fabricante instalado separadamente da marca de apresentação.
- Atualizações próprias têm origem e filtro visual **WinProvision API**, independentes do WinGet, nas três visualizações da página Atualizações. Os identificadores internos de catálogo permanecem compatíveis com os formatos existentes.

- Corrigida a descoberta de aplicativos antigos sem fabricante no Registro, como Anki 23.10: fabricante ausente permanece vazio e um desinstalador EXE não é usado como prova da tecnologia de instalação. A API pode assim oferecer o MSI atual publicado no R2, mantendo escopo, arquitetura e restrições de tecnologias comprovadas. A conferência após uma migração usa também o ProductCode do instalador validado, sem aceitar a permanência da instalação antiga.
- A listagem WinGet compara ofertas COM/CLI para a mesma versão instalada e conserva o alvo numérico mais novo, preservando metadados. Uma oferta COM antiga deixa de ocultar Anki 26.09.3 retornado pela CLI; versões opacas e inventários divergentes não são usados para substituir o alvo.

- A descoberta de atualizações pela API resolve nomes compartilhados entre pacotes usando o fabricante do Registro e do catálogo, para todos os aplicativos. Isso permite oferecer Git 2.56.0.3 sem confundi-lo com Microsoft.Git; candidatos ainda ambíguos continuam excluídos, sem consultas ao WinGet.

- O modo de atualização WinProvision API usa apenas o inventário do Registro e o catálogo R2/cache, sem consultas ou provisionamento WinGet COM/CLI na interface ou na CLI. A conferência após atualizar usa a identidade local preservada. Corrigida a arquitetura inferida pela visão do Registro: aplicativos x64 registrados na visão de 32 bits, como Visual Studio, deixam de ser descartados como incompatíveis. A arquitetura é conferida no executável quando disponível. Registros distintos de usuário e máquina são preservados.

- A pesquisa da CLI prioriza o catálogo WinProvision, preservando COM e CLI WinGet como alternativas quando o catálogo não retorna resultados.

- A descoberta de atualizações pela API compara todo o catálogo com o inventário instalado, sem exigir instalação anterior pelo WinProvision. Concilia IDs do WinGet e do Registro, consulta a CLI quando a COM está indisponível e rejeita nomes ambíguos. A preparação preserva arquitetura, escopo e tecnologia do instalador, sem fallback que remova essas restrições.

- Corrigido o carregamento prolongado da aba Atualizações: a COM enumera somente os instalados e não baixa metadados detalhados durante a listagem. Consultas COM/CLI ficam fora da thread da interface, têm prazo por fonte e preservam os resultados da fonte que concluir; o carregamento também tem limite global e cancelamento efetivo.
- Atualizações COM com `NoApplicableInstallers` (`0x8A150010`) passam ao próximo backend porque o WinGet confirma que a falha antecede o instalador. A versão-alvo da interface chega ao motor de atualização; o worker COM elevado devolve apenas o resultado COM para que a conferência da versão ocorra no processo chamador. Se o WinGet CLI ainda falhar antes da execução por ausência de atualização/instalador aplicável, uma instalação `--force` da versão-alvo tenta reparar uma única instalação inequívoca e mais antiga; hash, cancelamento, política, falha do instalador e inventário ambíguo não disparam essa repetição.
- A atualização COM correlaciona a instalação local com o catálogo remoto antes de chamar `UpgradePackageAsync`, que exige `InstalledVersion`; isso evita `InvalidOptions` (`0x8A150002`) causado por um pacote obtido apenas no catálogo remoto. Atualizações de instalações WinGet deixam de executar o instalador da API própria como se fosse uma nova instalação: após falha COM, seguem ao `winget update`. A API própria só atualiza pacotes gerenciados por ela e requisita a versão-alvo exata; uma atualização não cria registro de instalação nova. Isso evita código zero com versão antiga ou cópia portátil paralela.
- A confirmação do PowerShell confere também a versão do `pwsh.exe` em Program Files quando presente, pois duas entradas no inventário podem representar versões diferentes e a mais nova não garante que o comando executado tenha sido atualizado.
- Os fluxos de instalação, atualização e desinstalação foram conferidos com o código oficial do WinGet. A instalação encaminha ao CLI apenas opções que a COM/API própria não conseguem cumprir; a COM preserva o HRESULT `0x8A150002` de opções inválidas e permite fallback por ser falha pré-execução. A desinstalação só continua para API própria/CLI quando a falha COM ocorreu antes de iniciar o desinstalador, e expõe o HRESULT estendido quando não há código do desinstalador.
- O código WinGet `0x8A15002B` agora identifica atualização não aplicável, sem confundi-la com ausência de instalador compatível.
- Atualizações só aparecem como concluídas após uma nova consulta confirmar a versão instalada na identidade ofertada. Resultado inconclusivo, versão divergente ou reinicialização pendente deixam a operação sem confirmação e impedem retry automático do instalador. A classificação de códigos WinGet/Win32/MSI usa um dicionário central, preservando os códigos cujo significado depende do contexto.
- As opções WinGet por pacote aceitam argumentos adicionais do instalador e hooks PowerShell pré/pós-instalação. Hooks locais exigem opt-in explícito por instalação individual, não são exportados e executam sem elevação automática, com cancelamento e limite de tempo.
- A atualização interrompe o fallback quando a API própria iniciou o instalador ou deixou seu estado incerto. O auto-update pode repetir uma falha transitória pré-instalação uma vez; downloads WinGet `0x8A150008` têm até seis tentativas adicionais, com espera cancelável e sem repetir erros de hash, política, elevação ou cancelamento.
- Descobrir mantém o catálogo e o estado visual enquanto a janela está inativa, permitindo retorno imediato; o cache de imagens reduz seu orçamento em segundo plano e remove bitmaps ociosos quando o carregamento recomeça.
- Consultas simultâneas dos aplicativos instalados compartilham uma única varredura em andamento.
- O painel de detalhes passa a ser criado na primeira abertura, reduzindo o custo de memória inicial da interface.
- É possível escolher Descobrir, Atualizações ou Aplicativos instalados como página inicial em **Configurações → Interface e inicialização**. A preferência acompanha a exportação portátil.
- Os detalhes permitem copiar o SHA-256 do instalador quando disponível.
- O menu de contexto de uma atualização WinGet oferece a escolha pontual de ignorar a verificação de hash, identificada como menos segura.
- A descoberta da WinProvision API compara instalações gerenciadas ao catálogo R2 por identidade exata, usa índice e detalhes locais content-addressed com hashes verificados, limita consultas paralelas e ignora versões ambíguas. Consultas auxiliares têm prazos limitados e preservam resultados COM/WinGet quando o R2 ou inventário demora. A execução revalida a versão-alvo remotamente antes de iniciar; não usa cache offline para autorizar atualização.

## 1.0.14 - 23 de outubro de 2026

- O menu Provisionamento agora é opcional e começa oculto. Ative-o em **Configurações → Provisionamento**; a escolha acompanha as configurações portáteis.
- A detecção de instalados reconhece o UniGetUI da Microsoft Store (`XPFFTQ032PTPHF`) como o mesmo aplicativo do catálogo WinGet (`Devolutions.UniGetUI`).
- A tela Descobrir conserva a seleção semanal dos banners da Microsoft Store em cache local entre sessões; URLs de banners e ícones reutilizam o cache de imagens. Atualização manual e limpeza do catálogo invalidam a descoberta.

- Corrigida a seleção do método para pacotes da Microsoft Store: a interface não força o WinGet CLI antes da tentativa pela API COM. A consulta de escopos não bloqueia o botão Instalar; o inventário de instalados carrega em paralelo aos banners, que deixam de repetir buscas pela CLI na origem msstore. Versões fixadas no modo automático usam primeiro a API própria; o modo COM exclusivo informa quando a seleção de versão não é suportada.

## 1.0.11 - 21 de outubro de 2026

- O mecanismo de download offline do Office agora é escolhido em **Configurações → Operações e downloads**. A preferência é salva imediatamente, acompanha a exportação de configurações e se aplica a novas configurações criadas na página Office; configurações Office já salvas preservam sua escolha.
- Ícones de função migrados para Fluent UI System Icons com cores de tema. Modos de visualização, pesquisa e detalhes receberam ajustes visuais; controles de instalação usam cantos e caixas de seleção coerentes com o restante da interface.
- Ícones dos aplicativos do Office passam a usar `office/media` na CDN e escala visual padronizada. A detecção de aplicativos instalados recebe uma consulta complementar para reconhecer entradas como UniGetUI.

- Busca e API própria migradas para `winget/v1` da CDN. O índice e cada detalhe de versão são validados por SHA-256; versões aparecem automaticamente nas opções, e o escopo oferecido acompanha os instaladores da versão escolhida. A busca consulta a origem `msstore` pelo WinGet e enriquece resultados e banners com a Display Catalog da Microsoft; o editorial resolve IDs pela mesma origem para preencher banners e populares sem depender de sugestões aproximadas. Imagens da Store são carregadas pelas URLs da API e não são publicadas no R2.

- Instalações WinGet-Pkgs + Viper com dependências ou formato incompatível agora seguem pelo WinGet, preservando a versão escolhida e evitando a falha prematura `NoApplicableInstallers` (como no Alacritty). O progresso do Viper é exibido em vermelho.

- Novo método opcional WinGet-Pkgs + Viper: consulta o manifesto YAML oficial, baixa o instalador com SHA-256 obrigatório e executa silenciosamente os formatos compatíveis. A escolha de versão do pacote também funciona nessa rota; outras fontes continuam pelo WinGet. Preferências antigas mantêm o método anterior.

- Viper: downloads de URLs assinadas que mudam a cada requisição (GitHub Releases, Azure Blob, S3) deixam de falhar com “o servidor mudou o arquivo”; a identidade vem do ETag forte e do tamanho, e sem ETag basta o mesmo host/caminho. Conexão derrubada no meio do corpo (IOException com SocketException) passa a ser repetida, e o progresso de transferência única é limitado a 200 ms como na segmentada.

- WinProvision Elevator mais resiliente: processo já administrativo executa direto, sem helper nem UAC; recusa do UAC e credencial inválida são reconhecidas pela mensagem do gsudo, e um código 999 de instalador deixa de bloquear a sessão; pedido de UAC expira em 3 minutos e falha de imediato em sessão sem área de trabalho interativa; helper ausente, ilegível ou adulterado, e política ilegível, falham com mensagem própria em vez de exceção não tratada ou falso “bloqueado nas Configurações”; leituras transitórias (antivírus, interface x CLI) são repetidas; pasta de trabalho inexistente ou em WindowsApps usa System32; host COM elevado e ODT reportam essas falhas como resultado.

- Office reutiliza mídia local compatível e verificada mesmo quando “baixar antes” está desativado. Fontes explícitas preservam o SourcePath e a versão da mídia, com XML gravado separadamente quando necessário. Atualizar apenas o ODT não provoca nova preparação dos DATs; inventário divergente inicia reparo preservando arquivos concluídos. A escolha da versão ignora pastas incompletas e exige arquitetura/idiomas disponíveis. SHA-256 usa streaming assíncrono em blocos de 1 MiB, preservando a verificação integral e o cancelamento.

- Tela inicial carrega o editorial e os banners antes do inventário WinGet, preserva atualizações recebidas durante o carregamento e evita apresentar o ranking provisório quando já há editorial em cache. Progresso do Viper acompanha bytes parciais; Office agrega a porcentagem pelo tamanho dos fluxos e a fila atualiza também o texto visível. Verificação e instalação mantêm etapas próprias. Testes de scripts Office passam a gravar em log isolado.

- Clientes HTTP da inicialização (catálogos, Destaques e imagens) passam a tentar IPv4/IPv6 em paralelo com espera curta, evitando expirar o carregamento quando uma das rotas está indisponível. Timeouts, cancelamento e políticas HTTP específicas continuam respeitados.

- Corrigido o download do Office pelo Viper: conexões alternam IPv6/IPv4 sem aguardar o timeout de uma rota indisponível; CABs são renovados em área temporária e o CAB canônico acompanha a versão resolvida. Fluxos DAT existentes são conferidos e reparados sem descartar o arquivo anterior antes da verificação. Fallback para ODT preserva arquivos concluídos e registra a falha do Viper no log. Partes maiores no Office e checkpoints agrupados reduzem requisições e sincronizações de disco.

- Novo WinProvision Viper independente: biblioteca HTTP(S) e CLI própria, downloads segmentados e simultâneos, retomada com hashes por parte, verificação SHA-256 final, controle adaptativo por origem e respeito a Retry-After. Executável Native AOT e testes de rede locais; sem integração aos downloads da Store ou da CLI existente. Implementação original MIT, inspirada funcionalmente no aria2, sem código GPL incorporado.

- Configurações incluem notificações por categoria (atualizações, erros, sucessos e resumos), limites de operações e downloads simultâneos, pasta padrão e formato dos nomes dos instaladores. Lotes da interface respeitam os limites; downloads avulsos na interface e CLI preservam arquivos existentes com sufixos numéricos. Destinos explícitos prevalecem, e mídia offline mantém seu layout.

- Armazenamento e cache mostra espaço por categoria e memória de imagens, permite liberar memória e limpar imagens, catálogo ou instaladores reutilizáveis separadamente. Limite de imagens e prazo de revalidação configuráveis; HTTP condicional e cópia válida preservada em falhas. Coleções e preferências são preservadas nessas limpezas.

- Configurações ganham uma categoria WinProvision Elevator: permitir/bloquear pedidos do helper, autorizar ou encerrar a sessão, consultar o cache e verificar integridade. Política compartilhada com a CLI; cache automático de 5 minutos e UAC preservados.

- Configurações incluem Manutenção agendada: verificar e avisar ou instalar pela CLI, repetição de 15 minutos a 24 horas, agendas diária/semanal, seleção de aplicativos, execução imediata e última/próxima execução. A interface pode ficar fechada; a conta precisa estar conectada. UAC preservado, sem perguntas da CLI.
- Notificações do Windows App SDK podem ser desativadas; consultas vazias ou sem mudança nas versões não repetem avisos. Estado de execução separado das preferências, com leitura compatível das agendas existentes.

- Falhas da CLI mostram uma causa curta em português, com detalhes técnicos restritos aos logs. Sucesso, cancelamento, JSON e códigos de saída preservados.

- CLI padroniza a apresentação de todos os comandos públicos, incluindo ajuda, Office, exportação, logs, resumos, erros e cancelamento. Saídas JSON e códigos de saída preservados.
- Desinstalação executa diretamente, sem confirmação da CLI, inclusive com entrada redirecionada.
- Removidas as opções `--yes` e `-y`; retire-as dos scripts existentes. `--no-input` não exige confirmação nas operações; `upgrade --no-input` preserva a consulta sem executar atualizações.

- CLI padroniza pesquisa, listagem e consulta de atualizações em tabelas, e detalhes em painel, usando o renderizador compartilhado das operações. Saída textual muda de apresentação; contratos JSON, comandos e códigos de saída permanecem preservados.

## 1.0.10 - 17 de outubro de 2026

- Instalador usa escopo de máquina e PATH do sistema por padrão, inclusive em execução silenciosa por SYSTEM. Mantido `/CURRENTUSER` para instalações e atualizações por usuário; PATH automático sem duplicação e remoção apenas da entrada registrada. Manifesto WinGet passa a escopo de máquina.
- Validação real da release cobre instalação, reinstalação, resolução da CLI pelo PATH e desinstalação nos dois escopos.

## 1.0.9 - 17 de outubro de 2026

- Console com Spectre.Console: progresso temporário, spinner e resultados por aplicativo, com saída simples em redirecionamentos e JSON preservado.

- CLI executa instalações, importações e atualizações sem menus; somente desinstalação pede confirmação. Terminal apresenta progresso por aplicativo e resultados; diagnósticos técnicos ficam em arquivo, com pasta acessível por `winprovision logs`. `--download` continua apenas baixando.

- “Iniciar com o Windows” executa as atualizações pela CLI sem abrir a interface. Entradas antigas são migradas; o lote preserva seleção, versões ignoradas, método e opções salvas, notificações e cache do Elevator.

- Cache de elevação automático e fixo em 5 minutos de inatividade na interface e CLI; removidos os controles de duração e encerramento manual. `--elevation-cache` permanece aceito por compatibilidade.

## 1.0.7 - 16 de outubro de 2026

- Restaurada a execução administrativa por COM usando o SDK oficial Microsoft.WindowsPackageManager.InProcCom 1.29.380 dentro do host elevado pelo WinProvision Elevator.
- A escolha COM não é convertida para CLI por precisar de administração. A API própria continua independente, com execução direta pelo Elevator.
- Atualização do próprio aplicativo passa pelo Elevator quando exige administração, espera autorização antes de fechar e preserva diretório/escopo e reinício sem elevação.
- Remoções administrativas, Office e demais executores explícitos usam o helper, sem fallback para um UAC direto do instalador.
- SDK COM nativo e cópia temporária do Elevator são conferidos por hash antes do uso.

## 1.0.6 - 16 de outubro de 2026

- Corrigida a rota que elevava o cliente COM, mas deixava o Windows Installer pedir outro UAC pelo servidor externo do WinGet.
- Para instaladores administrativos, a COM consulta o pacote e delega a execução ao WinGet CLI elevado pelo Elevator, preservando escopo e arquitetura e mostrando o método real no progresso e log.
- Removido o worker COM privado da 1.0.5. O modo diagnóstico COM exclusivo informa a limitação em vez de executar silenciosamente outro backend.
- O teste de instalação real verifica a delegação COM para CLI. Sucesso em um runner não comprova a ausência de prompts em uma sessão de usuário com UAC habilitado.

## 1.0.5 - 16 de outubro de 2026

- WinProvision Elevator com nome de arquivo legível, ícone próprio, licença MIT e verificação de integridade na interface e CLI.
- Instalações COM que exigem administração executam em um worker elevado pelo Elevator, preservando a seleção explícita de COM, progresso e resultado.
- WinGet CLI e API própria consultam requisitos de elevação antes de iniciar instaladores; escopo de máquina e proibição de elevação são respeitados.
- Cache de autorização opcional por sessão, limitado ao processo, com encerramento explícito e prazo de inatividade.
- CLI autônoma com `--yes`/`-y`, sem menus nas automações, e `--elevation-cache` opcional.
- Provisionamento verifica a ativação COM e tenta registro/reparo do App Installer antes de preservar os fallbacks.
- Logs identificam caminho, versão e presença do Elevator; empacotamento rejeita helper ausente, divergente ou sem licença.

Validação: testes automatizados e smoke da interface; esses testes não substituem a confirmação visual do UAC e instalações reais em máquinas de usuários.

## 1.0.4 - 16 de outubro de 2026

- O próprio WinProvision Store pode ser adicionado aos Pacotes e exportado, mas é protegido contra instalação, atualização ou remoção pela interface em execução.
- A versão em execução aparece nos detalhes; instalações em lote informam quando o próprio aplicativo é ignorado.
- Origem dos aplicativos identificada nas listas e nos Pacotes, incluindo o Catálogo WinProvision.
- Novo comando para atualizar o catálogo preservando instaladores e imagens em cache.
- Acesso às atualizações pelos detalhes do próprio app, consulta automática em Configurações e confirmação antes de fechar para instalar uma atualização verificada.
- Comando curto de instalação silenciosa documentado no README.

Validação: 183 testes do Core e 95 verificações de smoke com dados simulados; instalações reais são verificadas separadamente.

## 0.1.3 - 16 de outubro de 2026

- Corrigidos os detalhes de aplicativos da infraestrutura própria: desenvolvedor, autor, data da release, tipo do instalador, URL, SHA-256 e galeria vêm do catálogo.
- Pacotes sem manifesto publicado no WinGet mostram “Não encontrado”. A tag `office` isolada não caracteriza mais uma oferta Microsoft 365.
- Capturas de tela com caminhos relativos passam a funcionar também fora da Microsoft Store.
- Atualização do índice e nova tentativa de leitura quando um detalhe muda de hash durante a publicação do catálogo, preservando a verificação de integridade.
- CLI utiliza o motor compartilhado sem carregar WinUI 3 e preserva opções Office e versões fixadas na importação de perfis e backups.

Validação: testes automatizados e smoke da interface. O smoke usa dados simulados e não comprova instalações reais.

## Em desenvolvimento

- WinProvision Elevator incorpora o ícone do aplicativo e metadados de identificação próprios.
- No modo Automático, instaladores com elevação conhecida são encaminhados ao helper antes do despacho pela COM; o requisito acompanha a instalação pela API própria.

- WinProvision Elevator baseado no gsudo, com licença MIT, fonte rastreável, hash fixado e configuração/cache isolados.
- Cache de elevação opcional por sessão nas Configurações e por execução da CLI com `--elevation-cache`; prazo por inatividade e encerramento explícito.
- Instaladores da API própria, elevações pelo fallback WinGet e ODT passam a usar o helper distribuído.

- CLI aceita `--yes`/`-y` para instalações, atualizações, remoções e importações sem menus; entrada redirecionada sem confirmação retorna erro de uso.
- Provisionamento verifica a ativação COM mesmo quando o executável WinGet funciona, tenta registro/reparo do App Installer e preserva o fallback.
- Remoção pelo fallback da CLI solicita modo silencioso.

- Origem do catálogo visível nas listas e Pacotes; o próprio aplicativo mostra a versão em execução e informa quando é ignorado em um lote.
- Atualização do catálogo sem apagar instaladores e imagens em cache, com acesso em Configurações.
- Detalhes do próprio app dão acesso às atualizações; o fluxo existente verifica o download e pede confirmação antes de fechar o app para executar o instalador externo.

- O próprio WinProvision Store pode ser adicionado aos Pacotes e exportado, mas não pode ser instalado, atualizado ou desinstalado pela interface em execução.

- Detalhes de aplicativos da infraestrutura própria mostram datas, instalador, URL, SHA-256, galeria e ausência de manifesto WinGet a partir do catálogo; a tag Office isolada não caracteriza uma oferta Microsoft 365.

- Documentado o contrato do catálogo remoto, seus hashes e a atualização de metadados de instaladores com URLs mutáveis.
- Documentadas a retenção de registros retirados do catálogo e a verificação operacional de conclusão da manutenção R2.

- CLI utiliza o motor compartilhado sem carregar WinUI 3; importação de perfis e backups do app, inclusive links de nuvem, preserva Office e versões fixadas.
- Exportação da CLI no formato nativo de coleções, com validação completa antes de executar os lotes.

- CLI com provisionamento do WinGet sob demanda e serviço compartilhado de instalação: COM, API própria e WinGet CLI.

- Navegador da página Sobre com perfil de dados no usuário e opção para abrir o site no navegador padrão.

- Interface WinUI 3 com temas claro e escuro, navegação por teclado e painel de operações.
- Pesquisa de aplicativos, detalhes, atualizações, programas instalados e coleções.
- Configuração do Office e preparação de mídia offline.
- CLI com pesquisa, instalação, download, atualização, remoção, importação e exportação.
- Comandos do Office na CLI com instalação mediante confirmação e download offline separado.
- Verificações de integridade, cancelamento e diagnóstico das operações.
- Documentação revisada e política de privacidade com descrição dos dados utilizados.

Ao publicar uma versão, registre a data, as novidades, as correções e as limitações conhecidas. Resultados obtidos apenas com dados simulados devem ser identificados como tais.
