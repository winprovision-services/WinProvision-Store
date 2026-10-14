# Memória e ciclo de vida

O cliente utiliza carregamento sob demanda e limites de cache para controlar o uso de memória.

## Interface

- As páginas são criadas conforme a navegação.
- ViewModels preservam o estado necessário sem manter páginas anteriores.
- Listas utilizam os recursos de virtualização disponíveis.
- Detalhes e imagens são carregados conforme o uso.
- Animações respeitam a preferência de movimento do Windows.

## Caches

O cache de imagens possui orçamento de memória e limite em disco. O orçamento é reavaliado durante o uso e reduzido em segundo plano ou sob pressão de memória.

Catálogos e informações de aplicativos em cache reduzem consultas repetidas. O uso de disco e memória depende do conteúdo acessado e das operações em andamento.

## Encerramento e cancelamento

As consultas e operações devem propagar cancelamento e manter suas tarefas observadas. O encerramento aguarda o trabalho acompanhado pelo aplicativo.

## Medição

Use `scripts/measure-memory.ps1` para medir o executável atual. Compare cenários equivalentes e registre a versão, o conteúdo carregado e o estado da janela.

Capturas estáticas mostram o estado final da interface; não medem fluidez ou quadros por segundo.
