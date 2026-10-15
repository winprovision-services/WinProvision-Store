# Compatibilidade de dados

Este documento registra as regras de compatibilidade do WinProvision Store.

## Preferências e coleções

Atualizações devem preservar as coleções, os identificadores dos aplicativos e as preferências existentes. Campos opcionais ausentes recebem o comportamento padrão documentado.

Ao importar um arquivo, valide seu formato antes de modificar dados ou iniciar operações. Não interprete arquivos arbitrários como listas de instalação.

## Office

Configurações salvas mantêm produto, arquitetura, idiomas, aplicativos selecionados e opções de instalação. Entradas com `officeOptions` válidas usam a origem `office`; perfis já exportados com a origem padrão `winget` são reconhecidos e normalizados. Uma configuração sem modo offline explícito mantém o comportamento online.

A mídia offline conserva sua versão e seu inventário de integridade. Arquivos incompletos não devem iniciar uma instalação.

## Linha de comando

A distribuição da CLI inclui o executável e os metadados `Microsoft.Management.Deployment.winmd` na mesma pasta. A integração de pacotes é compartilhada com o aplicativo; preservar a pasta completa mantém o suporte à API COM em Native AOT.

Os comandos e aliases aceitos estão em [CLI](CLI.md). Mudanças devem preservar os códigos de saída, as confirmações e o significado das opções existentes.

Exportações da CLI usam o perfil nativo com `apps`. Importações aceitam esse formato, backups com `tabs`, listas simples e exportações WinGet. O limite é de 16 MiB e 10.000 aplicativos. Opções Office e `pinnedVersion` dos perfis nativos acompanham a execução; duplicatas com opções conflitantes são rejeitadas antes do lote. URLs HTTPS podem seguir até cinco redirecionamentos HTTPS e links de Gist podem apontar ao perfil armazenado na nuvem. Metadados de provisionamento não são aplicados pela importação de aplicativos.

## Verificação de alterações

Execute os testes apropriados ao formato alterado. Use arquivos de teste isolados e confira cancelamento, dados incompletos e preservação de arquivos anteriores.

Registre mudanças de comportamento no [histórico](../CHANGELOG.md).
