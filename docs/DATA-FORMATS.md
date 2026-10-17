# Compatibilidade de dados

Este documento registra as regras de compatibilidade do WinProvision Store.

## Preferências e coleções

Atualizações devem preservar as coleções, os identificadores dos aplicativos e as preferências existentes. Campos opcionais ausentes recebem o comportamento padrão documentado.

Ao importar um arquivo, valide seu formato antes de modificar dados ou iniciar operações. Não interprete arquivos arbitrários como listas de instalação.

## Office

Configurações salvas mantêm produto, arquitetura, idiomas, aplicativos selecionados e opções de instalação. Entradas com `officeOptions` válidas usam a origem `office`; perfis já exportados com a origem padrão `winget` são reconhecidos e normalizados. Uma configuração sem modo offline explícito mantém o comportamento online.

A mídia offline conserva sua versão e seu inventário de integridade. Arquivos incompletos não devem iniciar uma instalação.

## Instalação

Na 1.0.10, novas instalações usam escopo de máquina (Program Files, registro de desinstalação HKLM e PATH do sistema), com privilégios administrativos. O nome de tarefa `clipath` é preservado para scripts antigos, mas o PATH passa a ser obrigatório, inclusive na instalação silenciosa sem `/MERGETASKS`. Reinstalar não duplica a entrada; a desinstalação remove apenas a entrada registrada pelo instalador, preservando as demais.

`/CURRENTUSER` mantém instalações por usuário e seu PATH em HKCU. A atualização pelo aplicativo já informa `/ALLUSERS` ou `/CURRENTUSER` e preserva diretório e escopo. O instalador padrão não migra nem remove instalações antigas por usuário: para converter para máquina, remova a instalação anterior e instale novamente para todos os usuários. O ZIP portátil continua sem modificar o PATH. Processos já abertos mantêm seu ambiente antigo; abra outro terminal após instalar, e no deployment use o caminho completo da CLI no processo atual.

## Linha de comando

A apresentação interativa da CLI usa Spectre.Console: barra verde, spinner azul e resultado fixo por aplicativo. A barra mede aplicativos concluídos no lote; operações sem porcentagem disponível usam animação indeterminada. A barra temporária desaparece ao terminar ou cancelar, preservando o histórico do terminal. Com saída redirecionada ou terminal sem suporte, usa texto simples, sem códigos ANSI. JSON não passa pelo renderizador de progresso.

A distribuição da CLI inclui o executável e os metadados `Microsoft.Management.Deployment.winmd` na mesma pasta. A integração de pacotes é compartilhada com o aplicativo; preservar a pasta completa mantém o suporte à API COM em Native AOT.

Os comandos e aliases aceitos estão em [CLI](CLI.md). Mudanças devem preservar os códigos de saída e o significado das opções existentes; mudanças de confirmação são registradas abaixo.

Exportações da CLI usam o perfil nativo com `apps`. Importações aceitam esse formato, backups com `tabs`, listas simples e exportações WinGet. O limite é de 16 MiB e 10.000 aplicativos. Opções Office e `pinnedVersion` dos perfis nativos acompanham a execução; duplicatas com opções conflitantes são rejeitadas antes do lote. URLs HTTPS podem seguir até cinco redirecionamentos HTTPS e links de Gist podem apontar ao perfil armazenado na nuvem. Metadados de provisionamento não são aplicados pela importação de aplicativos.

A CLI passa a executar instalações, importações e atualizações sem confirmação ou seleção interativa. `upgrade` executa todos os resultados do filtro; `--json` e `--no-input` sem `--yes` continuam apenas consultas. Somente desinstalação mantém confirmação; com entrada redirecionada exige `--yes`. A opção `--yes` permanece aceita nas demais operações por compatibilidade. `--download` mantém o significado de apenas baixar. Diagnósticos técnicos passam do terminal para arquivos por processo em `logs/cli`, consultáveis por `winprovision logs`; stdout JSON e códigos de saída são preservados.

O helper de elevação acompanha cada host em `Tool/gsudo`, com licença e inventário; a CLI portátil precisa preservar também essa pasta. O cache de autorização é temporário e não altera preferências persistidas nem o formato das coleções.

Na 1.0.5, o executável do helper passa a `WinProvision Elevator.exe`. A pasta `cli` da distribuição deve ser preservada para a CLI autônoma. O campo opcional `installers[].elevationRequirement` aceita os requisitos WinGet `elevationRequired`, `elevatesSelf` e `elevationProhibited`; sua ausência mantém a leitura dos catálogos anteriores e a consulta de metadados COM quando disponível. Na 1.0.6, a COM delega a execução administrativa ao WinGet CLI elevado pelo helper; o worker privado da 1.0.5 é removido, sem alterar os comandos públicos ou dados salvos.

## Catálogo remoto

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
