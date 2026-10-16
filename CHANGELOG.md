# Histórico de alterações

## 0.1.3 - 16 de outubro de 2026

- Corrigidos os detalhes de aplicativos da infraestrutura própria: desenvolvedor, autor, data da release, tipo do instalador, URL, SHA-256 e galeria vêm do catálogo.
- Pacotes sem manifesto publicado no WinGet mostram “Não encontrado”. A tag `office` isolada não caracteriza mais uma oferta Microsoft 365.
- Capturas de tela com caminhos relativos passam a funcionar também fora da Microsoft Store.
- Atualização do índice e nova tentativa de leitura quando um detalhe muda de hash durante a publicação do catálogo, preservando a verificação de integridade.
- CLI utiliza o motor compartilhado sem carregar WinUI 3 e preserva opções Office e versões fixadas na importação de perfis e backups.

Validação: testes automatizados e smoke da interface. O smoke usa dados simulados e não comprova instalações reais.

## Em desenvolvimento

- Detalhes de aplicativos da infraestrutura própria mostram datas, instalador, URL, SHA-256, galeria e ausência de manifesto WinGet a partir do catálogo; a tag Office isolada não caracteriza uma oferta Microsoft 365.

- Documentado o contrato do catálogo remoto, seus hashes e a atualização de metadados de instaladores com URLs mutáveis.
- Documentadas a retenção de registros retirados do catálogo e a verificação operacional de conclusão da manutenção R2.

- CLI utiliza o motor compartilhado sem carregar WinUI 3; importação de perfis e backups do app, inclusive links de nuvem, preserva Office e versões fixadas.
- Exportação da CLI no formato nativo de coleções, com validação completa antes de executar os lotes.

- CLI com provisionamento do WinGet sob demanda e serviço compartilhado de instalação: COM, API própria e WinGet CLI.

- Navegador da página Sobre com perfil de dados no usuário e opção para abrir o site no navegador padrão.

- Interface WinUI 3 com temas claro e escuro, navegação por teclado e painel de operações.
- Pesquisa de aplicativos, detalhes, atualizações, programas instalados e coleções.
- Configuração do Office e preparação de mídia offline.
- CLI com pesquisa, instalação, download, atualização, remoção, importação e exportação.
- Comandos do Office na CLI com instalação mediante confirmação e download offline separado.
- Verificações de integridade, cancelamento e diagnóstico das operações.
- Documentação revisada e política de privacidade com descrição dos dados utilizados.

Ao publicar uma versão, registre a data, as novidades, as correções e as limitações conhecidas. Resultados obtidos apenas com dados simulados devem ser identificados como tais.
