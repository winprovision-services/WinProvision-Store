# Medições de memória da Store

## Ambiente e método

- Data dos registros: 19/10/2026 (UTC, conforme timestamps dos CSVs).
- Máquina: `PC-GABRIEL`, Intel Core i3-9350KF, Windows 11 Pro x64, versão 10.0.26300.
- Store: Release, x64, Native AOT.
- Repetições: 7 execuções por configuração, sem interação, aguardando o último marco e mais 10 segundos antes da leitura final.
- Métrica: Private Working Set (PWS) em MiB. O modo 1 mede sem coleta forçada; o modo 2 força GC completo antes das leituras. As configurações GC foram passadas por variáveis de ambiente apenas nos experimentos, não aplicadas ao produto.
- Faixa é máximo menos mínimo, não intervalo de confiança.

## PWS por configuração

| Configuração | Modo | N | Mediana (MiB) | Mínimo (MiB) | Máximo (MiB) | Faixa (MiB) |
|---|---:|---:|---:|---:|---:|---:|
| Baseline | 1 | 7 | 189,277 | 187,781 | 192,098 | 4,316 |
| Baseline | 2 | 7 | 161,844 | 160,656 | 162,781 | 2,125 |
| `gcConcurrent=0` | 1 | 7 | 209,402 | 186,996 | 211,367 | 24,371 |
| `gcConcurrent=0` | 2 | 7 | 184,637 | 168,883 | 186,156 | 17,273 |
| `GCConserveMemory=3` | 1 | 7 | 187,996 | 176,734 | 191,762 | 15,027 |
| `GCConserveMemory=3` | 2 | 7 | 161,676 | 160,141 | 169,996 | 9,855 |
| `GCConserveMemory=7` | 1 | 7 | 179,320 | 160,145 | 180,977 | 20,832 |
| `GCConserveMemory=7` | 2 | 7 | 159,395 | 138,867 | 166,590 | 27,723 |

O modo 1 de `GCConserveMemory=7` apresentou mediana 9,957 MiB abaixo da baseline, mas sua faixa de variação é 20,832 MiB. Portanto, o ajuste 7 **não está provado** como ganho; o resultado não justifica aplicá-lo ao produto.

## Delta medido na carga do inventário instalado

Delta entre o marco imediatamente anterior e `installed_inventory_loaded`, em uma execução por modo. Os deltas gerenciado/nativo são as parcelas do delta de PWS registradas na análise de marcos; “residual” é o delta de PWS menos o delta do heap gerenciado.

| Modo | Delta PWS (MiB) | Delta gerenciado (MiB) | Residual (MiB) |
|---|---:|---:|---:|
| 1, sem coleta forçada | 51,20 | 22,12 | 29,07 |
| 2, coleta completa antes da leitura | 37,72 | 1,08 | 36,64 |

Esses dois deltas são leituras de marco únicas, não as sete repetições da tabela anterior. O residual não identifica sozinho uma categoria nativa.

## Origem dos dados

Resumo das execuções repetidas em `artifacts/memory-milestones/Stage3/repeated/*-summary.csv`. Deltas do inventário em `artifacts/memory-milestones/delta-analysis.csv`. Nenhuma configuração de GC foi incorporada ao produto.
