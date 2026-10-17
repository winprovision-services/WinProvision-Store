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

## Ciclo de vida

Páginas são criadas sob demanda. ViewModels preservam o estado necessário à navegação. Imagens e detalhes são carregados conforme o uso, com limites de cache e memória.

O encerramento solicita cancelamento e aguarda as tarefas observadas. Processos iniciados pelo aplicativo devem preservar o resultado real da operação.

## Dados e compatibilidade

Os formatos de coleções, preferências e opções são compartilhados pelas funções que os utilizam. Mudanças nesses formatos precisam de leitura compatível ou de uma transição documentada em [compatibilidade](DATA-FORMATS.md).

A serialização utilizada na distribuição é preparada para Native AOT. `ProfileDocumentService` valida os perfis usados por coleções e pela CLI. `ApplicationEngine` concentra a execução; o adaptador de console delega a ele. A CLI executa sem iniciar o processo da interface.

## Cache do catálogo

`CatalogCacheService` concentra o cache HTTP bruto de `StoreService` e `WinProvisionApiService`, usado pela UI e CLI. Os arquivos derivados das duas projeções permanecem compatíveis nos diretórios existentes. Novos detalhes são gravados somente no cache comum, sem duplicação por consumidor; detalhes legados continuam legíveis. Payloads comuns ficam em `%LOCALAPPDATA%/WinProvisionStore/Cache/Catalog-v1` e são conferidos por SHA-256 antes de reutilizar.

Manifestos têm prazo persistido de 6 horas por padrão, configurável para 12 ou 24 horas. Reiniciar não reinicia esse prazo. Após expirar, ETag/Last-Modified permitem revalidação condicional. Índices e detalhes são reutilizados pelo SHA esperado enquanto estiverem no cache. Atualização manual força a conferência dos manifestos e atualiza ambas as projeções sem apagar instaladores e imagens.

Uma pasta absoluta ou UNC opcional recebe `WinProvisionCache/Catalog-v1`. Cada usuário mantém uma cópia local, consultada antes da rede. Bloqueio de arquivo exclusivo coordena downloads/gravações entre processos e máquinas, com gravação atômica. Falha do compartilhamento ativa espera de 15 minutos e fallback local. A pasta precisa permitir criação, leitura, gravação e bloqueio. Os testes usam diretórios locais distintos para representar clientes; um servidor SMB real precisa de validação no ambiente de implantação.

Falhas transitórias, JSON inválido, hash divergente e 404 têm espera persistida de 15 minutos por recurso/versão esperada. Bytes inválidos não substituem o payload anterior. Falha de rede permite dados mutáveis verificados até 30 dias desde a validação. Cancelamento é propagado. Diagnóstico explícito consulta apenas os dois manifestos (máximo de 256 KiB cada), sem índice completo ou sucesso offline.

O limite padrão é 512 MiB por cópia do catálogo; opções: 128, 256, 512, 1024 e 2048 MiB. A manutenção acontece no uso do catálogo, no máximo uma vez ao dia, e em gravações acima da cota. Remove arquivos gerenciados sem uso há mais de 30 dias e depois os menos usados até atender o limite. Arquivos desconhecidos e redirecionamentos não são percorridos. Não há serviço residente de limpeza. Instaladores mantêm cache separado de 2 GiB/30 dias, agora também com manutenção diária e preservação de arquivos usados nos últimos 10 minutos; imagens mantêm sua política de 512 MiB/14 dias.

As Configurações oferecem pasta, prazo, cota, consulta de espaço e limpeza seletiva. A limpeza total local preserva a cópia compartilhada, que pode preencher novamente a cópia local. `cache` na CLI administra somente o catálogo bruto comum. Novas configurações exigem reinício da UI e valem na próxima execução da CLI. Instaladores são baixados sob demanda e conferidos antes da execução.

Referências: persistência e prazo do [HTTP cache do Chocolatey](https://docs.chocolatey.org/en-us/choco/features/http-caching/), seus [comandos de cache](https://docs.chocolatey.org/en-us/choco/commands/cache/) e a separação de metadados, mídia e dados do usuário do [UniGetUI](https://github.com/Devolutions/UniGetUI/blob/main/docs/PORTABLE.md). A limpeza diária e os limites acima são políticas do WinProvision.

## Elevação

O Core integra o WinProvision Elevator, variante do gsudo distribuída em `Tool/gsudo`, para processos que precisam de privilégios administrativos. O binário é conferido por SHA-256 antes da execução. O cache de autorização é automático, fixo em 5 minutos de inatividade, limitado ao PID do host e seus descendentes e encerrado na saída normal; a interface permanece sem elevação. Detalhes de origem, build e limites estão em [Tool/gsudo](../Tool/gsudo/README.md).

## Validação

A COM administrativa executa no host privado da CLI elevado pelo Elevator, carregando `Microsoft.WindowsPackageManager.InProcCom` 1.29.380. As DLLs oficiais são conferidas por SHA-256 e as classes são criadas localmente por `DllGetClassObject`, sem reutilizar o servidor COM externo. A consulta da interface permanece na COM do sistema. O protocolo privado v2 preserva resultado/progresso; após o despacho, falha de comunicação não inicia outro backend. A API própria mantém sua instalação direta pelo Elevator.

A atualização do próprio app usa um coordenador sem elevação e uma cópia temporária verificada do Elevator. O payload administrativo verifica o hash, sinaliza prontidão, aguarda o app encerrar e executa o instalador no diretório/escopo original. O app só fecha depois da prontidão; o coordenador reabre a interface sem elevação. Instalações por usuário não são promovidas a instalações de máquina. A cópia temporária separa os arquivos em execução dos que serão substituídos e isola o pipe/cache do helper original.

Os testes de regressão ficam em `tests/WinProvision.Core.Tests`. O smoke da interface fica em `tests/WinProvision.Store.Smoke` e é habilitado apenas em compilações de validação.

Os testes da interface usam preferências e coleções isoladas. A distribuição normal não contém dados simulados, comandos de captura ou arquivos de teste.

A validação de instalação real executa pelo seu próprio `.exe`, com `Microsoft.Management.Deployment.winmd` ao lado. Executar a DLL com `dotnet` muda o diretório nativo de resolução dos metadados para o host .NET e não representa a distribuição; esse modo pode falhar na ativação COM mesmo com o App Installer registrado.

## Atualizações na inicialização

A entrada de logon e as tarefas de atualização executam `cli/winprovision.exe --background-update`, sem criar janela da interface. O comando é interno e não pede confirmação da CLI. A autorização administrativa continua sendo solicitada pelo Elevator quando necessária, com cache de 5 minutos de inatividade para o lote. Uma recusa interrompe o lote sem repetir prompts. O host antigo com `--background-update` encaminha para a CLI para compatibilidade com registros ainda não migrados.

A CLI compila os serviços de preferências e notificações compartilhados com o host gráfico, sem depender de WinUI. O Windows App SDK acompanha a CLI para as notificações nativas. O executor respeita modo de consulta, seleção, versões ignoradas, método e opções de instalação salvos; registra o resultado em `%LOCALAPPDATA%/WinProvisionStoreWinUI3/logs/background-update.log`. Um mutex por usuário impede lotes simultâneos.
