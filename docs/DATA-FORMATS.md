# Compatibilidade de dados

O cache descartável `Cache/featured-store-banners.json` contém a segunda-feira da semana em `yyyy-MM-dd`, uma quebra de linha e uma lista JSON de `AppEntry` da Microsoft Store. Não é dado exportado nem contrato público; semana diferente, arquivo inválido ou limpeza do catálogo provocam nova descoberta.

Este documento registra as regras de compatibilidade do WinProvision Store.

A identidade MSI lida do instalador validado e os indicadores de identidade comprovada/confirmação de versão são metadados transitórios, omitidos do JSON. A confirmação aceita versões numéricas equivalentes ou superiores ao alvo e não altera históricos anteriores, formatos de coleção, preferências ou códigos públicos da CLI. Uma falha de confirmação registra também as versões e os escopos encontrados no Windows.

`%LOCALAPPDATA%/WinProvisionStore/stuck-upgrades.json` é um estado local descartável de tentativas de atualização WinGet. Guarda origem, ID, versão instalada, versão alvo, contador limitado a três e data. Não faz parte de perfis, backups ou contratos da CLI; arquivo ausente ou inválido reinicia apenas a detecção de ciclos de atualização.

`installation-preferences.json` armazena os enums `PreferredMethod` e `PreferredUpdateMethod`. O segundo campo é opcional para leitura retrocompatível e assume `Automatic` quando ausente; alterar uma preferência preserva a outra. Preferências existentes com o valor legado `WinGetPkgsViper` são migradas para `Automatic`; o valor permanece no enum para que arquivos antigos continuem legíveis. Ambos os padrões são `Automatic`.
O histórico preserva o rótulo `WinGet-Pkgs + Viper` para operações gravadas por versões anteriores; novas operações não usam esse método. Os valores existentes dos enums preservam suas posições.

## Sessões do WinProvision Viper

A preferência `UseViperOfficeDownload` em `app-preferences.json` e nas configurações portáteis é opcional e assume `false` (ODT) em arquivos anteriores. Ela determina o downloader usado em novas configurações criadas na página Office; o campo `useViperDownload` já salvo em cada configuração Office continua sendo respeitado.

O módulo independente usa arquivos adjacentes ao destino: `.viper.part`, `.viper.json` e `.viper.lock`. O checkpoint JSON (source generation para Native AOT) contém `Format=1`, `SourceFingerprint`, `ETag`, `Length`, `PieceSize` e `Completed` (lista de `Index` e `Sha256`). `SourceFingerprint` é SHA-256 da URL e dos cabeçalhos normalizados; não armazena esses valores em texto. O checkpoint nunca é um formato de coleção, configuração do app ou sessão `.aria2`.

Partes concluídas são sincronizadas no disco antes de substituir atomicamente o checkpoint. Na retomada, o motor valida versão do formato, impressão digital, tamanho, tamanho das partes, ETag e hash de cada parte. Partes locais divergentes são baixadas novamente. O destino final só é publicado após conclusão e eventual validação do SHA-256 esperado; por padrão, um destino existente gera falha e permanece intacto. A API pode selecionar `ReuseIfVerified`, que exige SHA-256 esperado e retorna sucesso sem rede apenas quando o arquivo final já confere; a CLI dedicada expõe esse modo como `--reuse-verified` e no campo opcional `reuseIfVerified` do lote. Cancelamentos preservam trabalho retomável, e falha do SHA-256 final descarta os bytes inválidos. A adição não altera contratos da Store ou da CLI `winprovision`.

## Preferências e coleções

`%LOCALAPPDATA%/WinProvisionStore/package-install-options.json` mantém as opções reutilizáveis por pacote WinGet, incluindo `CustomArguments`, `PreInstallScript` e `PostInstallScript`. Scripts são texto PowerShell inline, limitados a 32 KiB cada, exclusivos deste arquivo local e nunca entram em perfis, backups ou scripts de coleção. A opção de execução é sempre transitória (`RunInstallHooks=false` ao salvar): apenas a instalação individual iniciada pela interface pode habilitar hooks explicitamente. Arquivos antigos sem esses campos continuam válidos.

Perfis de coleção mantêm `schemaVersion`, `createdAt`, `name` e `apps`; o campo opcional `provisioning` contém configurações do Windows exportadas para uma máquina de destino. `ProfileBackupSet.provisioning` continua aceito por compatibilidade com backups anteriores, e quando há uma configuração por perfil ela é promovida ao nível do backup ao importar coleções. Perfil só de provisionamento pode omitir `apps`; os campos opcionais ausentes significam manter a configuração de destino. O módulo WinUI atual edita e transporta esses dados, preservando perfis antigos sem o campo.

`%LOCALAPPDATA%/WinProvisionStoreWinUI3/operation-preferences.json` contém `NotifyUpdates`, `NotifyErrors`, `NotifySuccess` e `NotifyBatchSummary` (booleanos, padrão `true`), `ParallelOperations` (1 a 4, padrão 1), `ParallelDownloads` (1 a 8, padrão 3), `DownloadDirectory` (caminho absoluto ou nulo para o padrão), `InstallerNames` (`Original=0`, `PackageVersion=1`, `PackageVersionArchitecture=2`, padrão 1) e `WarnInstallerHostChange` (booleano, padrão `true`). Campos ausentes preservam os padrões; arquivos inválidos são ignorados. A gravação valida antes da substituição atômica. Não inclui credenciais nem altera os perfis de aplicativos. Avisos de sucesso em lote requerem ambas as preferências `NotifySuccess` e `NotifyBatchSummary`; notificações de falha são controladas por `NotifyErrors`.

Downloads avulsos passam a considerar a pasta configurada quando não há destino explícito. Na interface, sem pasta configurada, a seleção por lote é preservada; na CLI, usa-se Downloads. Instaladores concluídos são publicados sem sobrescrever arquivos anteriores; colisões recebem ` (1)`, ` (2)` etc. Sem versão resolvida, o nome nativo do WinGet acompanha o ID para preservar as informações disponíveis. Manifestos auxiliares mantêm seus nomes. Mídia offline, cache interno e atualização do próprio app preservam seus layouts e destinos. Os contratos JSON e códigos de saída da CLI não mudam.

`%LOCALAPPDATA%/WinProvisionStoreWinUI3/image-cache-preferences.json` contém `MaxDiskMegabytes` (64 a 2048, padrão 512) e `RevalidateAfterDays` (1 a 30, padrão 14). Ausência ou conteúdo inválido usa os padrões; a gravação valida antes de substituir o arquivo atomicamente. Não altera coleções, preferências de instalação nem contratos da CLI.

Imagens remotas continuam usando o SHA-256 da URL com extensão `.img`. O novo arquivo opcional adjacente `.img.meta.json` contém `ETag`, `LastModified` (data ou nulo) e `ValidatedUtc`. Arquivos antigos sem metadados são aceitos; metadados inválidos são ignorados. HTTP 304 preserva bytes e validadores ausentes na resposta. Respostas inválidas não substituem a imagem válida. Essas entradas são descartáveis, separadas dos instaladores reutilizáveis.

O arquivo por usuário `elevation-preferences.json` registra `allowElevation` (booleano), compartilhado entre interface e CLI. A ausência do arquivo preserva a permissão de usar o Elevator; arquivo existente inválido ou inacessível bloqueia novos pedidos até a preferência ser salva novamente. O bloqueio aplica-se aos processos iniciados pelo helper, incluindo a preparação de atualização de máquina do próprio app. Não altera o formato das coleções nem a duração do cache. Autorizações em outras execuções da CLI têm cache próprio e não são encerradas pelo botão da interface.

Preferências de agendamento preservam os valores existentes de `UpdateSchedule` (`AtLogon=0`, `Daily=1`, `Weekly=2`) e acrescentam `Interval=3`. `UpdateIntervalMinutes` aceita de 15 a 1440, com padrão de 60; `UpdateNotificationsEnabled` usa `true` quando ausente; `UpdateScheduleAnchorUtc` é opcional e registra a configuração da agenda para estimar a próxima execução. Dados antigos mantêm sua agenda. Esses campos são compartilhados pela interface e pela CLI e usam serialização preparada para Native AOT.

`app-preferences.json` também contém `AutomaticCloudBackupEnabled` (booleano, padrão `true` quando ausente). Ele controla apenas o envio automático ao Gist após mudanças nas coleções; o backup local automático e o botão de backup manual continuam disponíveis. O formato do perfil e os contratos da CLI permanecem iguais.

`app-preferences.json` e as configurações portáteis versão 1 aceitam `SearchOnDemand` (booleano, padrão `false` quando ausente). Quando ativado, Descobrir aguarda uma pesquisa para carregar o catálogo e libera os dados da vitrine ao limpar a consulta. Arquivos anteriores continuam compatíveis e mantêm a vitrine completa como padrão.

`app-preferences.json` e as configurações portáteis versão 1 aceitam `ProvisioningMenuEnabled` (booleano, padrão `false` quando ausente). Quando ativado, exibe o menu Provisionamento na navegação; desativá-lo oculta o menu sem remover os perfis existentes. Arquivos anteriores continuam compatíveis e mantêm o menu oculto.

`app-preferences.json` e as configurações portáteis versão 1 aceitam `StartupPage` (enum numérico opcional: `Discover=0`, `Updates=1`, `Installed=2`; padrão `Discover`). A preferência escolhe a página aberta ao iniciar a interface. Valores ausentes abrem Descobrir; um valor inválido no arquivo local também é ignorado, enquanto a importação portátil rejeita opções inválidas. A inicialização em segundo plano pela CLI não usa essa opção. Exportações portáteis anteriores continuam compatíveis.

O arquivo local `background-update-state.json` armazena `StartedUtc`, `CompletedUtc`, `ExitCode`, `Available` e `Fingerprint`. Ele não altera as coleções nem o contrato JSON público da CLI; ausência ou arquivo inválido não impedem abrir as configurações. A impressão digital identifica o conjunto de IDs, origens e versões disponíveis, sem armazenar saídas brutas de serviços.

Atualizações devem preservar as coleções, os identificadores dos aplicativos e as preferências existentes. Campos opcionais ausentes recebem o comportamento padrão documentado.

`%LOCALAPPDATA%/WinProvisionStore/operation-history.json` mantém os campos existentes e acrescenta `StartedAt` (data opcional) e `Details` (saída da operação, limitada aos 16.384 caracteres finais). Entradas antigas sem esses campos continuam legíveis, com início e duração indicados como não registrados. O texto estruturado em `operation-tasks.log` é separado desse JSON, rotaciona a partir de 10 MiB e é apagado junto com o histórico. Os detalhes seguem a redação de dados sensíveis já aplicada ao log da operação. A pesquisa na tela Mais filtra localmente os registros por termos encontrados no nome, tipo, resultado, método ou saída; ela não altera nem exporta o formato persistido.

Ao importar um arquivo, valide seu formato antes de modificar dados ou iniciar operações. Não interprete arquivos arbitrários como listas de instalação.

## Office

O progresso do download Viper na fila agrega bytes dos fluxos DAT, ponderados pelos tamanhos consultados na CDN. Sem todos os tamanhos conhecidos, a barra permanece indeterminada. Após receber os bytes, a conferência de integridade e a instalação têm estados separados. Os callbacks opcionais de progresso no serviço Office não alteram JSON, XML ou comandos existentes. O parâmetro oculto opcional `LogPath` do script exportado permite isolar a saída de testes; sem ele, o destino do log continua o mesmo.

Configurações salvas mantêm produto, arquitetura, idiomas, aplicativos selecionados e opções de instalação. Entradas com `officeOptions` válidas usam a origem `office`; perfis já exportados com a origem padrão `winget` são reconhecidos e normalizados. Na instalação, uma configuração sem modo offline explícito usa a fonte local compatível já preparada e verificada, quando disponível e sem reduzir a versão instalada; na ausência dela, mantém o comportamento online. A exportação online continua sem incluir a mídia. O campo booleano opcional `useViperDownload` escolhe o downloader paralelo para preparar mídia offline ausente ou inválida; ausente ou `false` preserva o ODT. Uma mídia válida já existente é reutilizada independentemente do downloader. SourcePath explícito é conferido e mantido no XML, com Version selecionada entre versões locais com CAB e DATs da arquitetura e idiomas solicitados e AllowCdnFallback="FALSE". Caminhos Windows equivalentes com barras diferentes são normalizados; o formato do inventário SHA-256 permanece inalterado.

A mídia offline conserva sua versão e seu inventário de integridade. Arquivos incompletos não devem iniciar uma instalação. O downloader Viper usa a API oficial File List da Microsoft para resolver versão, caminhos e arquivos, baixa a CDN em paralelo e compara o SHA-256 oficial dos fluxos `.dat`. O caminho virtual `/office/data` da CDN é traduzido para `Office\Data` no cache, que é o layout usado pelo C2R. Se o Viper falhar ou a configuração incluir produtos ou idiomas adicionais, o processo remove apenas arquivos de trabalho não publicados do Viper e tenta o download oficial pelo ODT, preservando CABs e DATs concluídos para reaproveitamento e reparo pelo ODT; o cancelamento explícito interrompe ambas as tentativas. A limpeza recusa pontos de redirecionamento no cache. CABs são baixados novamente em área temporária; o CAB canônico é derivado do CAB da versão resolvida. DATs existentes só são reutilizados após conferir o SHA-256 oficial; arquivos divergentes são substituídos após baixar e verificar uma cópia de reparo. As partes do Office passam de 4 para 16 MiB; checkpoints antigos com outro tamanho são reiniciados. O `setup.exe` do ODT apenas entrega a configuração ao C2R, que instala o Office. O inventário SHA-256 local detecta alterações posteriores; a conferência de origem dos `.dat` usa os hashes publicados pela Microsoft.

## Instalação

Na 1.0.10, novas instalações usam escopo de máquina (Program Files, registro de desinstalação HKLM e PATH do sistema), com privilégios administrativos. O nome de tarefa `clipath` é preservado para scripts antigos, mas o PATH passa a ser obrigatório, inclusive na instalação silenciosa sem `/MERGETASKS`. Reinstalar não duplica a entrada; a desinstalação remove apenas a entrada registrada pelo instalador, preservando as demais.

`/CURRENTUSER` mantém instalações por usuário e seu PATH em HKCU. A atualização pelo aplicativo já informa `/ALLUSERS` ou `/CURRENTUSER` e preserva diretório e escopo. O instalador padrão não migra nem remove instalações antigas por usuário: para converter para máquina, remova a instalação anterior e instale novamente para todos os usuários. O ZIP portátil continua sem modificar o PATH. Processos já abertos mantêm seu ambiente antigo; abra outro terminal após instalar, e no deployment use o caminho completo da CLI no processo atual.

## Linha de comando

Todos os comandos públicos usam a apresentação compartilhada: tabelas para consultas, painéis para ajuda/detalhes/versão/logs/destinos e cabeçalhos e resumos comuns para lotes. Erros e cancelamento ficam em stderr. A apresentação textual muda; os contratos JSON e códigos de saída são preservados. Autorizações do Windows e interação própria de desinstaladores continuam dependendo do sistema e do pacote.

A apresentação interativa da CLI usa Spectre.Console: barra verde, spinner azul e resultado fixo por aplicativo. A barra mede aplicativos concluídos no lote; operações sem porcentagem disponível usam animação indeterminada. A barra temporária desaparece ao terminar ou cancelar, preservando o histórico do terminal. Com saída redirecionada ou terminal sem suporte, usa texto simples, sem códigos ANSI. JSON não passa pelo renderizador de progresso.

A distribuição da CLI inclui o executável e os metadados `Microsoft.Management.Deployment.winmd` na mesma pasta. A integração de pacotes é compartilhada com o aplicativo; preservar a pasta completa mantém o suporte à API COM em Native AOT.

Os comandos e aliases aceitos estão em [CLI](CLI.md). Mudanças devem preservar os códigos de saída e o significado das opções existentes; mudanças de confirmação são registradas abaixo.

Exportações da CLI usam o perfil nativo com `apps`. Importações aceitam esse formato, backups com `tabs`, listas simples e exportações WinGet. O limite é de 16 MiB e 10.000 aplicativos. Opções Office e `pinnedVersion` dos perfis nativos acompanham a execução; duplicatas com opções conflitantes são rejeitadas antes do lote. URLs HTTPS podem seguir até cinco redirecionamentos HTTPS e links de Gist podem apontar ao perfil armazenado na nuvem. Metadados de provisionamento não são aplicados pela importação de aplicativos.

A CLI passa a executar instalações, importações e atualizações sem confirmação ou seleção interativa. `upgrade` executa todos os resultados do filtro; `--json` e `upgrade --no-input` continuam apenas consultas. A desinstalação também executa sem confirmação da CLI, inclusive com entrada redirecionada; `--no-input` é aceito nas demais operações sem mudar a execução direta. As opções `--yes` e `-y` foram removidas: scripts existentes precisam retirá-las; seu uso retorna erro de opção desconhecida (código 2). `--download` mantém o significado de apenas baixar. Diagnósticos técnicos passam do terminal para arquivos por processo em `logs/cli`, consultáveis por `winprovision logs`; stdout JSON e códigos de saída são preservados.

O helper de elevação acompanha cada host em `Tool/gsudo`, com licença e inventário; a CLI portátil precisa preservar também essa pasta. O cache de autorização é temporário e não altera preferências persistidas nem o formato das coleções.

Na 1.0.5, o executável do helper passa a `WinProvision Elevator.exe`. A pasta `cli` da distribuição deve ser preservada para a CLI autônoma. O campo opcional `installers[].elevationRequirement` aceita os requisitos WinGet `elevationRequired`, `elevatesSelf` e `elevationProhibited`; sua ausência mantém a leitura dos catálogos anteriores e a consulta de metadados COM quando disponível. Na 1.0.6, a COM delega a execução administrativa ao WinGet CLI elevado pelo helper; o worker privado da 1.0.5 é removido, sem alterar os comandos públicos ou dados salvos.

## Catálogo remoto

### Contrato ativo da CDN WinGet

A CDN pública é servida por `https://cdn.winprovisionstore.com.br/winget/v1/`, a partir do bucket `wps-cdn`. A raiz `v1` identifica a versão do contrato, não a versão dos pacotes. A API WinProvision e o cliente WinGet compartilham os mesmos objetos publicados. O layout canônico por pacote segue a convenção de identificador/versão do WinGet-Pkgs:

```text
winget/v1/
  catalog.json
  search/<sha256>.json           # índice compacto e imutável para busca
  manifests/<partição>/<publisher>/<pacote>/
    app.json                     # identidade do app e versões disponíveis
    versions/<versão>/app.json   # metadados, mídia e instaladores da versão
    media/                       # mídia compartilhada pelo pacote
  media/sha256/<prefixo>/<hash>.<ext>
```

`<partição>` é o primeiro caractere alfanumérico do `PackageIdentifier`, minúsculo; `_` representa identificadores que começam com outro caractere. Publisher, pacote e versão preservam a grafia oficial nos dados, mas segmentos de caminho devem ser escapados e validados para impedir traversal, separadores e colisões sem distinção de maiúsculas/minúsculas. Por exemplo, `7zip.7zip` usa `manifests/7/7zip/7zip/`.

`catalog.json` é o ponteiro pequeno da publicação. Deve registrar versão do esquema, geração, commit de origem, instante UTC, contagens, caminho/tamanho/SHA-256 do índice e, opcionalmente, assinatura do catálogo. O caminho do índice inclui seu SHA-256 (`search/<sha256>.json`), permitindo cache longo sem alterar o objeto ativo. O `app.json` geral mantém identidade estável, metadados de busca/apresentação, `latestVersion`, arquiteturas disponíveis e a lista de versões com `detailPath` e SHA-256 do detalhe. Cada detalhe tem hash publicado e fica no caminho por versão, por exemplo `manifests/7/7zip/7zip/versions/26.04/app.json`. Metadados que descrevem uma release específica, incluindo `publisherUrl`, `licenseUrl`, `releaseDate` e `releaseNotesUrl`, ficam no detalhe da versão, não na identidade geral do app. `security` não é duplicado no índice: a decisão de elevação é tomada a partir de `elevationRequirement` de cada instalador no detalhe cuja integridade foi validada. Cada detalhe preserva `id`, `version`, `source` e `installers` compatíveis com o modelo atual; cada instalador mantém URL, arquitetura, tipo e SHA-256 verificado.

Todos os JSONs cujo SHA-256 é publicado usam **WPS Canonical JSON v1**: UTF-8 sem BOM; objetos serializados com nomes de propriedade em ordem lexicográfica crescente; arrays preservam a ordem definida pelo contrato/origem; sem espaços de formatação; sem quebra de linha final; números de ponto flutuante, `NaN` e infinitos são proibidos. Strings são emitidas em UTF-8, sem escape ASCII desnecessário. O SHA-256 é calculado sobre os bytes exatos enviados à CDN. O produtor deve gerar os mesmos bytes para o mesmo conteúdo semântico; qualquer alteração nessa regra exige uma nova versão de canonicalização e validação de compatibilidade.

Ícones e screenshots ficam uma vez no diretório `media` do pacote e são compartilhados por todas as versões. Os metadados usam caminhos relativos, SHA-256 e tipo MIME; o publicador verifica a mídia existente no R2 antes de enviar e não substitui bytes divergentes. Uma futura deduplicação entre pacotes pode migrar os arquivos para um espaço content-addressed sem alterar o conteúdo dos detalhes de versão.

O índice de busca contém apenas campos de busca e apresentação, como ID, nome, publisher, descrição curta, tags, versão mais recente e caminho do `app.json` geral. O `app.json` geral lista os detalhes e seus hashes. O cliente baixa e mantém o índice em cache local, pesquisa em memória e busca o app, detalhes e mídia sob demanda. O índice só será particionado se medições mostrarem que o arquivo único prejudica o tempo de atualização ou a memória.

Publicação segue uma ordem segura: workflows processam mudanças do repositório desde o último commit, reutilizam mídia por hash, geram e validam somente os detalhes e índices afetados, enviam detalhes e `app.json` antes do índice imutável e atualizam `catalog.json` por último. Uma reconciliação completa periódica confere a origem contra o bucket. Como os caminhos dos detalhes e do app geral são estáveis por contrato, usam cache curto de cinco minutos; índices content-addressed recebem cache longo e `catalog.json` revalidação de um minuto. Escrita no bucket usa credencial restrita do workflow, nunca distribuída no app. HTTPS/TLS 1.2+ é obrigatório. Hashes detectam corrupção; para provar autenticidade contra substituição maliciosa do catálogo, o cliente deve validar uma assinatura com chave pública embutida no app.

O cliente e a API própria consomem `winget/v1`; a busca da vitrine consulta ainda a origem WinGet `msstore` e enriquece seus resultados pela Display Catalog da Microsoft. Ícones, banners e capturas da Store usam URLs retornadas pela API e não são publicados como mídia no R2. O índice WinGet contém também `icon` (caminho relativo e SHA-256), `score` e `locale` para apresentar os resultados sem baixar todos os `app.json` gerais. Cada entrada do índice fixa `appSha256`; o `app.json` geral fixa o SHA-256 de cada detalhe por versão. A API só usa instaladores depois dessas verificações. O cache local usa nomes separados dos arquivos do catálogo anterior.

Na 1.0.7, o host COM privado v2 substitui a delegação administrativa para CLI: usa o SDK oficial InProcCom no processo elevado pelo Elevator. Os comandos públicos e formatos salvos não mudam. A distribuição completa inclui `Tool/winget-com` na interface e na CLI. As atualizações próprias preservam o escopo/diretório da instalação e aguardam autorização antes de fechar a interface.

A origem de apresentação distingue WinGet, Microsoft Store e Catálogo WinProvision sem alterar o campo de roteamento `source`. O próprio app é identificado como instalado pela versão do processo em execução e permanece exportável nos Pacotes, mas é ignorado nas instalações locais em lote. A consulta manual do catálogo preserva instaladores e imagens em cache; limpar o cache continua sendo uma ação separada.

Pacotes mantidos pela infraestrutura própria usam `catalogOrigin: "curated"`, preservando `source: "winget"` para compatibilidade de roteamento. `wingetManifestAvailable: false` indica que o link do manifesto não deve ser apresentado. `releaseDate` e `catalogSyncedUtc` são datas ISO 8601 da release e da última publicação dos dados; `author` e `releaseNotes` são opcionais. A tela usa os instaladores do detalhe para tipo, URL e SHA-256 desses pacotes, sem depender de uma consulta ao WinGet. A galeria aceita os caminhos relativos de `media.screenshots` em ambas as origens.

O app e a instalação pela API própria consultam `Store/Catalog/manifest.json`, `Store/Catalog/manifest/search-index.json` e os detalhes por aplicativo indicados por `detailPath` (terminado em `app.json`). A fonte Microsoft Store usa o mesmo contrato sob `Store/Catalog/msstore/`. Não existe consumo direto do snapshot de YAMLs em `Store/Manifests/winget/v1` pelo cliente.

`recordSha256` valida os bytes do detalhe; `indexSha256` valida os bytes do índice; `catalogSha256` identifica a geração do catálogo. O `sha256` de cada instalador valida o binário baixado. São verificações diferentes: atualizar o instalador exige publicar seus dados, recalcular os hashes dos JSONs afetados e atualizar o manifesto principal por último. Não se deve remover essas verificações para aceitar um catálogo desatualizado.

Campos opcionais nulos podem ser omitidos. Os detalhes contêm instaladores e metadados completos; o índice mantém dados de busca, apresentação, classificação, caminhos e hashes, sem duplicar instaladores. `score`, `regionTags` e `gitHubStars` participam da classificação; mídia, URLs e idiomas participam da apresentação. O campo `hasGitHubMetrics` não tem consumidor funcional atual. Um tamanho de instalador antigo não deve acompanhar uma URL ou binário atualizado.

Uma URL de instalador mutável pode entregar uma versão diferente antes da atualização do WinGet. Nesse caso, a sincronização dos YAMLs não basta: o catálogo precisa de metadados atualizados a partir do fornecedor e a verificação do binário permanece obrigatória. Os detalhes podem incluir `installerVerification` com a origem, o método e a data dessa conferência; o cliente ignora metadados operacionais desconhecidos. O Chrome pode receber dados conferidos por assinatura Authenticode e identidade MSI, sem executar o instalador.

`Store/Catalog/manifest/layout.json` descreve a organização publicada. Snapshots sob `Store/Manifests` e arquivos de recuperação sob `Store/Automation` não são catálogos consultados pelo cliente. Registros fora do índice atual podem continuar referenciados por índices em cache; a sua exclusão exige considerar essa compatibilidade.

A manutenção concede sete dias de carência, a partir da primeira detecção, aos registros canônicos retirados dos índices. Os arquivos removidos do legado têm cópia verificada e manifesto de restauração por 30 dias. Mídias, Office, wallpapers e registros ativos ficam fora dessa remoção. O diário de publicação preserva seus arquivos de recuperação; backups sem referência no diário expiram após sete dias.

`Store/Automation/maintenance/last-success.json` registra a geração, as contagens conferidas e a execução que concluiu a validação integral dos catálogos e a manutenção. Esse indicador é operacional e não substitui os manifestos lidos pelo app. Uma execução parcial ou com falha não o atualiza.

## Verificação de alterações

Execute os testes apropriados ao formato alterado. Use arquivos de teste isolados e confira cancelamento, dados incompletos e preservação de arquivos anteriores.

Registre mudanças de comportamento no [histórico](../CHANGELOG.md).

O cache do Elevator passa a ser automático e fixo em 5 minutos de inatividade para interface e CLI, sem opção de duração ou desativação. `--elevation-cache` permanece aceito por compatibilidade e torna-se redundante. A autorização inicial continua no UAC; o vínculo ao processo e a invalidação na saída são preservados.

“Iniciar com o Windows” passa a ativar atualizações pela CLI, preservando o esquema de `app-preferences.json`. Registros existentes de abertura da interface são migrados ao carregar as preferências e passam a habilitar `AutoUpdateAtStartup`. As entradas de logon usam a CLI; a entrada adicional de atualização é omitida quando já existe a entrada principal para evitar execução duplicada. Desativar a atualização automática também desativa essa inicialização.

No modo de atualização WinProvision API, o inventário é lido do Registro do Windows e comparado ao catálogo `winget/v1` ou ao seu cache validado, sem depender de WinGet. A chave local da instalação e a arquitetura conferida no executável são metadados transitórios, omitidos do JSON público da CLI. Registros de usuário e máquina com o mesmo nome não são fundidos. O índice R2 deve publicar a versão nova para que ela seja oferecida; atualizar apenas a fonte externa WinGet não muda o snapshot próprio.

Fabricante e tecnologia desconhecidos permanecem ausentes no inventário de atualizações, sem inferir família EXE a partir de um desinstalador. O ProductCode usado para confirmar uma atualização API vem de `installers[].productCode` do detalhe validado e permanece metadado transitório do resultado, omitido do JSON. Os formatos públicos de catálogo, preferências e CLI não mudam.

A identidade transitória do Registro inclui hive, visão e subchave, evitando confundir instalações de escopos ou arquiteturas diferentes; HKCU compartilhado entre visões é lido uma vez. Fabricante instalado, origem de apresentação e resultado “já atualizado” também são transitórios. `UpdateSource=winprovision` e o rótulo **WinProvision API** distinguem a origem na interface; o campo histórico `source=winget` continua sendo a chave de roteamento do catálogo/JSON público, sem exigir WinGet para consultar o inventário ou atualizar pela API. Nenhuma preferência de filtro é persistida.
