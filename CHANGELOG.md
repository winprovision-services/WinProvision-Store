# Histórico de alterações

## Não lançado

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
