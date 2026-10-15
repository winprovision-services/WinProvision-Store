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
| PackageManagement | Provisionamento e integração COM compartilhados pelo aplicativo e pela CLI, sem dependência da interface |

A fila organiza operações, evita duplicatas e acompanha cancelamento e conclusão. A interface recebe atualizações de estado pela thread apropriada.

## Ciclo de vida

Páginas são criadas sob demanda. ViewModels preservam o estado necessário à navegação. Imagens e detalhes são carregados conforme o uso, com limites de cache e memória.

O encerramento solicita cancelamento e aguarda as tarefas observadas. Processos iniciados pelo aplicativo devem preservar o resultado real da operação.

## Dados e compatibilidade

Os formatos de coleções, preferências e opções são compartilhados pelas funções que os utilizam. Mudanças nesses formatos precisam de leitura compatível ou de uma transição documentada em [compatibilidade](DATA-FORMATS.md).

A serialização utilizada na distribuição é preparada para Native AOT.

## Validação

Os testes de regressão ficam em `tests/WinProvision.Core.Tests`. O smoke da interface fica em `tests/WinProvision.Store.Smoke` e é habilitado apenas em compilações de validação.

Os testes da interface usam preferências e coleções isoladas. A distribuição normal não contém dados simulados, comandos de captura ou arquivos de teste.
