# Arquitetura do cliente

O WinProvision Store utiliza .NET 10, C# 14, WinUI 3 e CommunityToolkit.Mvvm. A interface usa controles nativos e recursos de tema compartilhados.

## Responsabilidades

| Área | Responsabilidade |
| --- | --- |
| Views | Apresentação, navegação, foco e adaptação do layout |
| ViewModels | Estado observável, seleção, validação e comandos |
| Serviços do aplicativo | Arquivos, notificações, janelas e integração com o Windows |
| Core | Modelos, coleções, histórico e funcionalidades compartilhadas |
| CLI | Entrada de comandos, consultas, confirmações e saída de console |
| PackageManagement | Motor compartilhado de consultas e operações, provisionamento e cadeia COM/API própria/CLI, sem dependência de WinUI |

A fila organiza operações, evita duplicatas e acompanha cancelamento e conclusão. A interface recebe atualizações de estado pela thread apropriada.

## WinProvision Viper independente

`WinProvision.Viper` é uma biblioteca .NET 10 sem referências à Store, Core, WinUI ou motores externos. O Core usa a biblioteca nos downloads verificados de instaladores do catálogo, inclusive durante instalações pela API própria; a CLI dedicada `WinProvision.Viper.Cli` produz `winprovision-viper.exe`, com publicação própria por `scripts/build-viper.ps1`, e não é incluída no instalador. A implementação é original, sob a licença MIT do repositório, usando o aria2 como referência funcional sem incorporar seu código GPL.

O transporte HTTP usa I/O assíncrona, limites por arquivo/origem/instância e escrita por posição. Downloads verificados da Store dividem um orçamento de até 24 conexões entre os arquivos concorrentes, com até 16 conexões por arquivo, e mantêm o SHA-256 publicado como obrigatório. Divide apenas representações com tamanho conhecido, suporte validado a Range e ETag forte. A retomada verifica identidade remota e hashes das partes locais; uma mudança remota reinicia o trabalho. Cada resposta parcial precisa corresponder exatamente ao intervalo, tamanho e ETag esperados. Ignorar Range provoca reinício por transferência única após aguardar todos os workers.

429/503 reduzem concorrência por origem; repetição limitada respeita Retry-After. TLS permanece validado, redirecionamentos são limitados e cabeçalhos privados não atravessam origens. Dados completos são verificados antes de mover o arquivo sem sobrescrever destinos existentes. O módulo possui testes HTTP locais e validação separada do executável nativo. Detalhes e limites estão no [README do Viper](../src/WinProvision.Viper/README.md).

O transporte e as consultas de metadados do Office compartilham conexão TCP com tentativas IPv6/IPv4 alternadas a cada 250 ms; sockets perdedores são cancelados e descartados. O handler HTTP continua responsável por TLS e proxy. O Office usa partes de 16 MiB, renova CABs em área temporária e reutiliza DATs somente após conferir o SHA-256 oficial. O fallback para ODT preserva arquivos finais para reparo e remove apenas trabalho não publicado do Viper.

## Ciclo de vida

Clientes de inicialização com `RequestRateLimitHandler` configuram transportes `SocketsHttpHandler` para alternar IPv6/IPv4, com timeout de conexão de 15 segundos quando não há outro definido. Callbacks e timeouts explícitos são preservados. O cliente do catálogo próprio também usa essa estratégia de conexão. Cancelamentos continuam propagados aos chamadores.

Páginas são criadas sob demanda. ViewModels preservam o estado necessário à navegação. Imagens e detalhes são carregados conforme o uso, com limites de cache e memória.

A vitrine resolve o editorial em cache e carrega banners antes de aguardar o inventário de programas instalados; o inventário apenas atualiza os indicadores de instalação. Atualizações do catálogo recebidas durante o carregamento dos banners são reaplicadas ao concluir esse carregamento. O catálogo de apps e o editorial começam a carregar em paralelo, e banners disponíveis no catálogo local aparecem antes das buscas complementares da Display Catalog. A memória do cache de bitmaps é limitada a 4–12 MiB conforme o orçamento de memória informado pelo runtime.

A tela Descobrir mantém o catálogo e a seleção visual em memória durante a sessão. A descoberta complementar de banners da Microsoft Store é gravada por semana em `Cache/featured-store-banners.json`; reabrir o app reutiliza esses metadados, inclusive URLs de banners e ícones. O cache de imagens `Cache/RenderedIcons` conserva os bytes e a memória compartilha bitmaps decodificados. Atualizar manualmente a vitrine ou limpar o catálogo invalida a descoberta de banners; a troca de semana a revalida automaticamente. O estado instalado continua vindo do inventário, não do cache visual.

Quando a janela fica inativa por 30 segundos, sem operações de pacotes pendentes e com Descobrir aberta, o app cancela o carregamento da vitrine e libera o catálogo, o índice de busca e os itens visuais em memória. Os arquivos de cache e a seleção persistida não são removidos. Ao voltar, o catálogo local é carregado novamente e a revalidação remota continua em segundo plano. A sincronização automática de coleções só é ativada na primeira gravação, quando há um snapshot para processar.

A opção `SearchOnDemand` mantém Descobrir sem catálogo, banners ou cards até a confirmação de uma pesquisa. O fluxo carrega apenas o catálogo necessário para resultados, sem buscar conteúdo editorial ou inventário de instalados; ao limpar a pesquisa, cancela buscas ao vivo e libera o catálogo em memória, preservando o cache em disco.

Na pesquisa da vitrine, o WinGet consulta também a origem `msstore`; a Display Catalog pública da Microsoft complementa os resultados com localização, descrição, ícones, banners e capturas usando URLs diretas. Para preencher o editorial e os populares, o app pesquisa termos na Display Catalog e reaproveita a descoberta semanal dos banners. A mídia é consumida em tempo de uso e não é publicada no R2.

## Operações, downloads e notificações

A preparação do Office prioriza uma fonte local compatível com inventário de integridade válido. A mídia é conferida independentemente da versão do ODT: substituir um `setup.exe` ausente ou antigo não reinicia o download dos DATs. Uma fonte explícita permanece separada do diretório que recebe o XML. O XML fixa a versão com CAB específico e DATs da arquitetura e idiomas solicitados, e não habilita fallback da mídia para a CDN. A preparação e a execução do instalador informam durações separadas; o Click-to-Run continua responsável pela aplicação dos arquivos, componentes e serviços do Office.

Os ícones dos aplicativos da página Office usam `https://cdn.winprovisionstore.com.br/office/media/<produto>.png`, correspondente a `office/media/` no bucket `wps-cdn`. O cliente usa uma área visual comum e escalas por produto para compensar margens transparentes diferentes nos PNGs; `ms365.png` é o ícone separado para os perfis da suíte e precisa estar no mesmo diretório.

Antes de preparar mídia ou executar o ODT, a instalação consulta os Product IDs Click-to-Run registrados. Se todos os produtos selecionados já estiverem presentes, a operação termina com sucesso sem iniciar o instalador; se faltar um produto adicional, a configuração ainda pode prosseguir. A verificação é repetida após adquirir o bloqueio compartilhado, cobrindo interface, coleções e CLI. A presença é determinada por Product ID, sem comparar idiomas, componentes excluídos ou versão.

**Configurações → Notificações** controla atualizações disponíveis, erros, sucessos e resumos. O serviço nativo consulta a política antes de exibir cada aviso, inclusive na CLI agendada. Avisos de sucesso em lote exigem tanto Sucessos quanto Resumos; falhas usam a categoria Erros. A opção anterior de notificações da manutenção continua como controle adicional daquela tarefa. Não há um aviso por pacote. Cancelamentos sem sucesso/falha não produzem aviso de erro. Permissões do Windows continuam sendo respeitadas.

**Configurações → Operações e downloads** ajusta concorrência da interface e downloads avulsos. O limitador de operações fica no Core, abrange todos os runners da fila e permite cancelar enquanto se aguarda uma vaga. Os lotes da interface executam trabalho limitado, mantendo atualizações de estado na thread da interface. Bloqueios próprios do Office e de outros mecanismos permanecem. Os limites são por processo; lotes CLI continuam sequenciais. Reduções não interrompem operações ativas.

A mesma página gerencia fontes WinGet por meio da CLI instalada, valida URLs HTTPS ao adicionar fontes e protege as fontes padrão `winget` e `msstore` contra remoção nessa interface. Ao cadastrar uma fonte personalizada, tenta `Microsoft.PreIndexed.Package` e, se a primeira falhar sem registrar a fonte, `Microsoft.Rest`; consulta a lista antes de repetir para evitar cadastro duplicado. A exportação/importação portátil inclui tema, inicialização, manutenção agendada, notificações da agenda e preferências de operações/downloads em JSON versão 1, sem credenciais, coleções ou dados de conta. Importar atualiza a integração de inicialização desta conta do Windows. O aviso opcional de mudança de domínio compara os URLs HTTPS do instalador nas versões instalada e disponível do pacote WinGet antes de cada atualização iniciada pela página Atualizações; se os metadados não estiverem disponíveis, a operação segue sem o aviso.

Nos detalhes de um pacote da fonte `winget`, o `app.json` geral da CDN fornece as versões selecionáveis automaticamente. O detalhe da versão selecionada, conferido pelo SHA-256 declarado no app geral, determina quais escopos de instalação podem ser oferecidos. A versão escolhida é encaminhada à API própria ou ao WinGet conforme o método selecionado. O usuário pode ignorar o hash apenas na instalação ou atualização individual em curso, usando `--ignore-security-hash`; a escolha não é salva e não afeta downloads verificados do catálogo próprio.

Quando uma versão específica é escolhida, o método automático usa o WinGet CLI com `--version`, pois a API COM instala a versão padrão do catálogo e não aplica essa seleção. Uma escolha explícita de COM retorna erro informativo; a API própria explícita continua a usar o detalhe da versão requisitada. O inventário clássico consulta as visões de Registro 32 e 64 bits tanto para a máquina quanto para o usuário, usando o caminho lógico de desinstalação de cada visão.

No método automático, a operação provisiona o WinGet e então tenta COM, API própria WinProvision e WinGet CLI, nessa ordem. Escopo, arquitetura, versão, pasta, interação e argumentos são encaminhados às camadas; cada camada aplica as opções que suporta antes do fallback. Pacotes msstore também passam primeiro pela COM. Os métodos ComApi, WinProvisionApi e WingetCli continuam sendo escolhas explícitas de uma única camada.

Falhas de download classificadas como transitórias no WinGet CLI permitem até duas novas tentativas, com atrasos de 1 e 2 segundos, somente antes de iniciar o instalador e sem elevação. Quando a API própria não encontra instalador compatível, ela pode tentar uma vez sem as restrições opcionais de arquitetura e escopo, mantendo a versão e as demais opções. A COM pede elevação dinâmica após um erro de permissão pré-instalação; recusa, política, cancelamento e estado incerto são terminais. Se a COM informou um identificador instalado diferente do ID do catálogo, a última tentativa CLI de atualização pode usar esse ID local após falha de localização, sem fixar a fonte remota.

A descoberta de atualizações registra as versões instalada e alvo. Três atualizações reportadas como bem-sucedidas para o mesmo pacote e alvo, seguidas de nova oferta com a versão instalada sem avanço, ocultam essa oferta até que uma das versões mude. Instalações não entram nesse contador. O estado é local e descartável, em `stuck-upgrades.json`.

O método explícito `WinProvisionApi` usa o catálogo publicado em `winget/v1`, confere os detalhes e o instalador e baixa com Viper quando o formato permite. Não há uma segunda opção de instalação WinGet-Pkgs + Viper: ela duplicava essa mesma rota. Preferências antigas com `WinGetPkgsViper` são migradas para `Automatic` na inicialização.

Downloads WinGet usam uma pasta temporária exclusiva dentro do destino e publicam arquivos apenas após sucesso do comando. Downloads do catálogo próprio verificam SHA-256 antes de publicar. Metadados do catálogo aceitam apenas redirecionamentos HTTPS na mesma origem e têm limite de 64 MiB; downloads de instaladores podem seguir redirecionamentos HTTPS entre origens. O hash detecta divergência em relação ao catálogo, mas não substitui uma assinatura independente do fornecedor. A extração offline rejeita traversal, links e nomes de dispositivo reservados do Windows. A publicação usa movimento sem sobrescrita e numera colisões, inclusive entre downloads concorrentes. A configuração de nomes aplica-se aos instaladores avulsos, com ID, versão e arquitetura quando conhecidos; arquivos auxiliares do WinGet mantêm seus nomes. Pastas escolhidas explicitamente têm prioridade sobre a preferência. A alteração não move downloads existentes.

## Cache do catálogo

**Configurações → Armazenamento e cache** mede imagens, catálogo/metadados e instaladores separadamente. A medição ocorre fora da interface, não percorre pontos de nova análise e indica resultados parciais quando arquivos não podem ser lidos. As limpezas de imagens, catálogo e instaladores preservam coleções e preferências. Os instaladores reutilizáveis continuam sendo gerenciados pelas operações quando presentes, mas o usuário pode remover seu cache local separadamente nessa página. Métodos gerais de limpeza anteriores mantêm seu contrato; a nova página usa métodos específicos por categoria.

`AsyncImage` compartilha downloads por URL e mantém memória com orçamento automático. Imagens remotas ficam em `%LOCALAPPDATA%/WinProvisionStore/Cache/RenderedIcons`; ícones extraídos ficam em `Cache/Icons`. O limite configurável de 64 a 2048 MB (512 MB por padrão) aplica-se às imagens remotas. A manutenção periódica remove entradas com mais de 60 dias e as menos usadas para respeitar o limite. Liberar memória remove referências do cache; imagens visíveis podem continuar referenciadas pelos controles.

Ao reler do disco, o carregador revalida imagens após o prazo de 1 a 30 dias (14 por padrão), usando ETag e Last-Modified quando disponíveis. HTTP 304 renova a validade sem baixar os bytes novamente. Falhas de rede ou respostas que não decodificam preservam a cópia válida anterior, limitada à retenção de 60 dias. Downloads têm limite de 16 MB; bytes e metadados são publicados por escritas atômicas após decodificação válida. Bloqueio e geração de cache impedem que downloads anteriores republiquem arquivos após a limpeza. Imagens existentes sem metadados continuam legíveis, usando a data de modificação como referência.

O encerramento solicita cancelamento e aguarda as tarefas observadas. Processos iniciados pelo aplicativo devem preservar o resultado real da operação.

O histórico de operações guarda início, fim, método, resultado e a parte final da saída de cada instalação, atualização ou remoção. A página Mais apresenta cada tarefa como um item expansível, com resumo primeiro e saída detalhada sob demanda. O log de diagnóstico do processo continua separado para eventos de inicialização, serviços e falhas fora de operações de pacotes.

O backup de coleções inclui também guias vazias. A sincronização automática agrupa mudanças próximas; um backup manual aguarda uma sincronização em andamento para salvar o snapshot pedido. Ao conectar ao GitHub, a conta só passa a constar como conectada depois de validar também o acesso à listagem dos Gists. Falhas dessa etapa são apresentadas ao usuário sem substituir a credencial anterior.

A página Conta lista os JSON do Gist de backup vinculado, lendo cada perfil para mostrar quantidade de coleções e aplicativos e indicando arquivos incompatíveis. O usuário pode importar um arquivo específico ou excluí-lo após confirmação; a importação da página Pacotes também pede a escolha do JSON. A exclusão altera somente o Gist, e uma sincronização futura pode recriar o arquivo principal. A preferência de envio automático ao Gist não interrompe os backups locais; o botão de backup envia à nuvem quando há uma conta conectada.

Os backups são exibidos com títulos normalizados e conteúdo expansível por coleção e aplicativo; os nomes reais dos JSON continuam visíveis para identificação. Um HTTP 401 encerra a sessão local do GitHub, remove o token salvo e interrompe novas tentativas automáticas até uma nova conexão. A exclusão em massa enumera os Gists da conta, verifica novamente cada Gist antes de apagá-lo e restringe a ação aos marcadores de backup ou bootstrap do WinProvision. Após confirmação, desativa o envio automático à nuvem e preserva os backups locais. Falhas parciais informam quantos Gists foram removidos.

## Dados e compatibilidade

Os formatos de coleções, preferências e opções são compartilhados pelas funções que os utilizam. Mudanças nesses formatos precisam de leitura compatível ou de uma transição documentada em [compatibilidade](DATA-FORMATS.md).

A serialização utilizada na distribuição é preparada para Native AOT. `ProfileDocumentService` valida os perfis usados por coleções e pela CLI. `ApplicationEngine` concentra a execução; o adaptador de console delega a ele. A CLI executa sem iniciar o processo da interface.

## Elevação

**Configurações → WinProvision Elevator** controla os pedidos feitos pelo helper na interface e na CLI. A política por usuário é lida antes de abrir o cache e antes de despachar processos; atualizações de instalação de máquina do próprio app também a respeitam antes de preparar o helper temporário. O bloqueio não controla pedidos próprios de instaladores nem serviços oficiais do Windows.

A página permite autorizar explicitamente a sessão, encerrar seu cache e consultar estado/integridade sem UAC. A consulta verifica o SHA-256 fixado e pergunta ao helper se o cache está disponível; não deduz autorização apenas de uma solicitação anterior. Encerrar o cache não desativa sua criação automática na próxima operação permitida. Nenhuma opção altera a duração de 5 minutos ou permite substituir o helper distribuído.

O Core integra o WinProvision Elevator, variante do gsudo distribuída em `Tool/gsudo`, para processos que precisam de privilégios administrativos. O binário é conferido por SHA-256 antes da execução. O cache de autorização é automático, fixo em 5 minutos de inatividade, limitado ao PID do host e seus descendentes e encerrado na saída normal; a interface permanece sem elevação. Detalhes de origem, build e limites estão em [Tool/gsudo](../Tool/gsudo/README.md).

## Validação

A COM administrativa executa no host privado da CLI elevado pelo Elevator, carregando `Microsoft.WindowsPackageManager.InProcCom` 1.29.380. As DLLs oficiais são conferidas por SHA-256 e as classes são criadas localmente por `DllGetClassObject`, sem reutilizar o servidor COM externo. A consulta da interface permanece na COM do sistema. Antes de pedir elevação, a interface confere se a CLI auxiliar aceita o protocolo privado v3. O protocolo confirma a inicialização do worker antes de qualquer despacho e preserva resultado/progresso; falha nessa inicialização permite recorrer à API própria. Após o sinal de prontidão, falha de comunicação não inicia outro backend. A API própria mantém sua instalação direta pelo Elevator.

A atualização do próprio app usa um coordenador sem elevação e uma cópia temporária verificada do Elevator. O payload administrativo verifica o hash, sinaliza prontidão, aguarda o app encerrar e executa o instalador no diretório/escopo original. O app só fecha depois da prontidão; o coordenador reabre a interface sem elevação. Instalações por usuário não são promovidas a instalações de máquina. A cópia temporária separa os arquivos em execução dos que serão substituídos e isola o pipe/cache do helper original.

Os testes de regressão ficam em `tests/WinProvision.Core.Tests`. O smoke da interface fica em `tests/WinProvision.Store.Smoke` e é habilitado apenas em compilações de validação.

Os testes da interface usam preferências e coleções isoladas. A distribuição normal não contém dados simulados, comandos de captura ou arquivos de teste.

A validação de instalação real executa pelo seu próprio `.exe`, com `Microsoft.Management.Deployment.winmd` ao lado. Executar a DLL com `dotnet` muda o diretório nativo de resolução dos metadados para o host .NET e não representa a distribuição; esse modo pode falhar na ativação COM mesmo com o App Installer registrado.

## Atualizações na inicialização

**Configurações → Manutenção agendada** usa o mesmo executor da CLI. A agenda pode executar ao entrar no Windows, em intervalos de 15 minutos a 24 horas, diariamente ou semanalmente. Agendas periódicas são registradas no Agendador de Tarefas com token interativo do usuário; funcionam com a interface fechada enquanto a conta estiver conectada. O intervalo de 24 horas usa uma tarefa diária no horário de configuração. A interface salva opções e aplica a agenda com compensação em caso de falha, sem habilitar tarefas ao apenas visitar a página.

“Executar agora” inicia `--background-update-now`, comando interno que executa a política salva mesmo quando a agenda está desativada. O mutex do executor impede sobreposição com tarefas agendadas. Não há confirmação da CLI; o UAC continua sendo solicitado quando necessário. A execução imediata aguarda o processo terminar sem interromper um instalador já iniciado.

O executor registra início, conclusão, resultado e impressão digital das versões descobertas em `background-update-state.json`, separado das preferências. Esse estado alimenta a última execução e evita repetir notificações de uma consulta sem alterações. As notificações locais usam `Microsoft.Windows.AppNotifications.AppNotificationManager` e `AppNotificationBuilder` do Windows App SDK. A preferência permite desativá-las; consultas vazias não notificam. Falhas de notificação não alteram o resultado das operações.

A entrada de logon e as tarefas de atualização executam `cli/winprovision.exe --background-update`, sem criar janela da interface. O comando é interno e não pede confirmação da CLI. A autorização administrativa continua sendo solicitada pelo Elevator quando necessária, com cache de 5 minutos de inatividade para o lote. Uma recusa interrompe o lote sem repetir prompts. O host antigo com `--background-update` encaminha para a CLI para compatibilidade com registros ainda não migrados.

A CLI compila os serviços de preferências e notificações compartilhados com o host gráfico, sem depender de WinUI. O Windows App SDK acompanha a CLI para as notificações nativas. O executor respeita modo de consulta, seleção, versões ignoradas, método e opções de instalação salvos; registra o resultado em `%LOCALAPPDATA%/WinProvisionStoreWinUI3/logs/background-update.log`. Um mutex por usuário impede lotes simultâneos.
