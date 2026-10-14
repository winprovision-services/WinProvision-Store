# Contribuir com o WinProvision Store

Relate problemas reproduzíveis e proponha alterações com propósito claro. Para vulnerabilidades, siga a [política de segurança](SECURITY.md). Participantes devem respeitar o [código de conduta](CODE_OF_CONDUCT.md).

## Preparação

Use Windows, o SDK indicado em `global.json`, C# 14, WinUI 3 e CommunityToolkit.Mvvm.

Leia o [README](README.md), as [orientações de desenvolvimento](AGENTS.md), a [arquitetura](docs/ARCHITECTURE.md) e as [diretrizes visuais](DESIGN.md).

## Implementação

- Mantenha apresentação nas Views e estado e comandos nos ViewModels.
- Preserve os formatos de dados, o cancelamento e a compatibilidade Native AOT.
- Use recursos de tema compartilhados e mantenha os textos em português.
- Preserve as verificações de integridade e a confirmação de operações.
- Justifique novas dependências e confira suas condições de uso.

## Validação

1. Execute `scripts/build.ps1` e os testes apropriados.
2. Para apresentação, execute `scripts/smoke-test.ps1` e examine os temas claro e escuro e a janela compacta.
3. Para distribuição, confira a publicação Native AOT e os arquivos incluídos.
4. Identifique quais cenários usaram dados simulados e quais foram verificados em execução real.

## Envio da alteração

Descreva o problema, o comportamento resultante, a validação e as limitações conhecidas. Confira os links da documentação com `scripts/check-repository.ps1`.

Não inclua credenciais, dados pessoais, caches ou arquivos de compilação. Preserve a licença do projeto e os avisos dos componentes utilizados.
