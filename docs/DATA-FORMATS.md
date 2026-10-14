# Compatibilidade de dados

Este documento registra as regras de compatibilidade do WinProvision Store.

## Preferências e coleções

Atualizações devem preservar as coleções, os identificadores dos aplicativos e as preferências existentes. Campos opcionais ausentes recebem o comportamento padrão documentado.

Ao importar um arquivo, valide seu formato antes de modificar dados ou iniciar operações. Não interprete arquivos arbitrários como listas de instalação.

## Office

Configurações salvas mantêm produto, arquitetura, idiomas, aplicativos selecionados e opções de instalação. Uma configuração sem modo offline explícito mantém o comportamento online.

A mídia offline conserva sua versão e seu inventário de integridade. Arquivos incompletos não devem iniciar uma instalação.

## Linha de comando

Os comandos e aliases aceitos estão em [CLI](CLI.md). Mudanças devem preservar os códigos de saída, as confirmações e o significado das opções existentes.

Exportações e importações de listas usam identificadores completos. Entradas inválidas devem ser rejeitadas antes de executar o lote.

## Verificação de alterações

Execute os testes apropriados ao formato alterado. Use arquivos de teste isolados e confira cancelamento, dados incompletos e preservação de arquivos anteriores.

Registre mudanças de comportamento no [histórico](../CHANGELOG.md).
