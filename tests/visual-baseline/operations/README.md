# Baselines visuais do painel de operações — v2

## Proveniência

- Conjunto promovido após aprovação visual em 2026-11-13.
- Commit de origem das 34 capturas: `c91b488` (`Generate operations review v2 artifacts`). Este é o commit identificado para este conjunto de arquivos; o commit que promove os PNGs ao diretório de baseline é posterior.
- Commit que promoveu as 34 capturas para este diretório e substituiu a versão anterior: `149ff51c9f56acab112b28eacc0ccd1df8493fe2`.
- Capturas geradas em `artifacts/review-v2/final/`, com o harness de revisão v2 descrito abaixo.
- As baselines antigas foram substituídas no working tree; seus conteúdos continuam disponíveis no histórico do Git.

## Harness v2

O harness em `tests/WinProvision.Store.Smoke/SmokeTest.cs` aplica o tema efetivo à raiz e à subárvore capturada, compõe a imagem sobre superfície sólida branca (claro) ou preta (escuro), fixa progresso em 50% e só salva após duas renderizações consecutivas iguais. O conjunto tem 17 capturas por tema, totalizando 34.

## Critério de comparação

Uma imagem falha se 0,1% ou mais de seus pixels tiver qualquer canal RGB com delta maior que 2 em relação à baseline correspondente; para passar, a porcentagem deve ser estritamente menor que 0,1%. Dimensões diferentes também falham.

## Regeneração

Na raiz do repositório, execute:

```powershell
$env:WINPROVISION_CAPTURE_OPERATION_BASELINE = '1'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/smoke-test.ps1
Remove-Item Env:\WINPROVISION_CAPTURE_OPERATION_BASELINE
```

O smoke compara automaticamente as 34 capturas com este diretório pelo script `scripts/compare-operation-baselines.py` (Python padrão, sem dependências externas). Novas imagens ficam em `artifacts/validation/.../smoke-results/operations-baseline/`; promova-as somente após revisão e aprovação.

## Diferença conhecida

Nas capturas históricas pré-v2, o texto da mensagem em `failed-row` e `canceled-row` apresenta deslocamento vertical de aproximadamente 1 px em relação a `queued-row`. Não foi possível localizar um ZIP de backup do projeto datado de 2026-11-08 para comparar o XAML histórico. Até essa comparação estar disponível, fica registrada como **diferença conhecida de 1px do harness antigo**, sem atribuição comprovada ao app.

## Capturas adicionais v2.1 — fallback do ícone da operação

O subdiretório `v2.1/` acrescenta seis capturas de linhas de operação, em tema claro e escuro, para três casos: ícone de pacote PNG local com transparência, ausência de ícone (fallback genérico) e URL inválida do pacote (fallback do gerenciador). O conjunto cobre a regressão em que o glifo genérico permanecia visível atrás de uma imagem de pacote transparente. As 34 capturas v2 do diretório raiz permanecem inalteradas e continuam sendo comparadas pelo smoke; as imagens v2.1 são referência adicional para a cobertura específica de ícones.

As novas capturas foram geradas pelo harness de `tests/WinProvision.Store.Smoke/SmokeTest.cs`, após aguardar a imagem ou o fallback esperado e fixar a barra na captura. Para regenerar, execute `scripts/smoke-test.ps1` com `WINPROVISION_CAPTURE_OPERATION_BASELINE=1`; o smoke salva também os casos v2.1 em `artifacts/validation/smoke/operations-icons-v2_1/`. Copie para `v2.1/` somente depois da revisão visual.
