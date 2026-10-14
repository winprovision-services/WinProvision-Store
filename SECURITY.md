# Segurança — WinProvision Store

Esta política cobre o aplicativo e a CLI do projeto atual.

## Relatar uma vulnerabilidade

Solicite um canal reservado pelos espaços de suporte do projeto. Não publique detalhes exploráveis, credenciais, dados pessoais ou arquivos maliciosos em um relato aberto.

Inclua a versão do aplicativo, a edição do Windows, as condições necessárias, o impacto e uma reprodução mínima segura. Aguarde a análise antes de divulgar detalhes técnicos sensíveis.

## Proteções do cliente

O aplicativo utiliza conexões HTTPS, verificações de integridade e proteção local de tokens pelo Windows. As opções de instalação não permitem solicitar que hashes sejam ignorados.

A verificação de um instalador não garante o comportamento dos aplicativos instalados nem cobre necessariamente arquivos adicionais obtidos por eles. Revise o aplicativo e seu fornecedor antes de autorizar uma operação.

## Dados de diagnóstico

Logs e históricos podem conter nomes de aplicativos, caminhos e mensagens de instaladores. O cliente oculta formatos comuns de credenciais, mas revise os arquivos antes de compartilhá-los.

Consulte a [privacidade](PRIVACY.md) e as [verificações de segurança do cliente](docs/SECURITY-AUDIT.md).
