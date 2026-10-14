# Organização e revisão do projeto

## Estrutura esperada

O projeto mantém a solução na raiz, o código em `src`, os testes em `tests`, os comandos de desenvolvimento em `scripts` e a documentação em `docs`.

Compilações, caches e saídas de validação ficam fora do conteúdo versionável. Credenciais e arquivos pessoais não devem ser incluídos.

## Documentação

- Descreva o comportamento do aplicativo e da CLI atuais.
- Use português, títulos objetivos e exemplos que correspondam aos comandos aceitos.
- Mantenha links locais válidos.
- Identifique limites e resultados de testes sem prometer garantias não verificadas.
- Preserve os textos originais de licença e atribuição dos componentes utilizados.

## Revisão antes da entrega

1. Execute `scripts/check-repository.ps1` para conferir a estrutura e os links.
2. Compile com `scripts/build.ps1` e execute os testes apropriados.
3. Para apresentação, execute o smoke e examine os temas claro e escuro e a janela compacta.
4. Confira o conteúdo da publicação e a versão do aplicativo.
5. Registre as mudanças no [histórico](../CHANGELOG.md).

A revisão local não substitui a execução dos cenários relevantes no Windows de destino. Relatos de problemas devem indicar a versão e as condições necessárias à reprodução.
