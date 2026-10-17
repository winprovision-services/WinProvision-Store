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

## Elevação

O Core integra o WinProvision Elevator, variante do gsudo distribuída em `Tool/gsudo`, para processos que precisam de privilégios administrativos. O binário é conferido por SHA-256 antes da execução. O cache de autorização é explícito, limitado ao PID do host e seus descendentes e encerrado na saída normal; a interface permanece sem elevação. Detalhes de origem, build e limites estão em [Tool/gsudo](../Tool/gsudo/README.md).

## Validação

A COM consulta os metadados do instalador. Quando eles exigem administração, a execução é encaminhada ao WinGet CLI elevado pelo Elevator antes do despacho COM, com escopo e arquitetura preservados. Elevar apenas um cliente COM não garante a elevação do servidor externo e pode causar outro UAC do Windows Installer. O log e o progresso identificam a execução pela CLI. O modo diagnóstico COM exclusivo recusa essa operação em vez de mudar de backend; pacotes de usuário continuam pela COM quando aplicável.

Os testes de regressão ficam em `tests/WinProvision.Core.Tests`. O smoke da interface fica em `tests/WinProvision.Store.Smoke` e é habilitado apenas em compilações de validação.

Os testes da interface usam preferências e coleções isoladas. A distribuição normal não contém dados simulados, comandos de captura ou arquivos de teste.

A validação de instalação real executa pelo seu próprio `.exe`, com `Microsoft.Management.Deployment.winmd` ao lado. Executar a DLL com `dotnet` muda o diretório nativo de resolução dos metadados para o host .NET e não representa a distribuição; esse modo pode falhar na ativação COM mesmo com o App Installer registrado.
