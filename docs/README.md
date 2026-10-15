# Documentação do WinProvision Store

## Uso

- [Capturas de tela](SCREENSHOTS.md): telas de Descobrir, Atualizações, Instalados, Coleções e Office.
- [CLI](CLI.md): comandos, opções, importação, exportação e Office.
- [Office online e offline](OFFICE-OFFLINE.md): preparação e utilização de mídia local.
- [Compatibilidade de dados](DATA-FORMATS.md): preferências, coleções e formatos de arquivos.
- [Privacidade](../PRIVACY.md): dados locais, comunicações e sincronização opcional.

## Desenvolvimento

- [Arquitetura do cliente](ARCHITECTURE.md).
- [Padrão visual](VISUAL-DESIGN.md) e [diretrizes de apresentação](../DESIGN.md).
- [Movimento e acessibilidade](MOTION.md).
- [Native AOT](NATIVE-AOT.md).
- [Publicação de versões](RELEASES.md): build manual, releases e envio opcional ao WinGet.
- [Memória e ciclo de vida](PERFORMANCE.md).
- [Comunicação de rede do cliente](NETWORK-LIMITS.md).

## Qualidade e participação

- [Verificações de segurança](SECURITY-AUDIT.md).
- [Organização e revisão do projeto](REPOSITORY-AUDIT.md).
- [Contribuição](../CONTRIBUTING.md), [conduta](../CODE_OF_CONDUCT.md) e [segurança](../SECURITY.md).
- [Histórico](../CHANGELOG.md), [licença](../LICENSE) e [avisos de terceiros](../THIRD-PARTY-NOTICES.md).
## Site do projeto

O [site do WinProvision Store](https://winprovision-services.github.io/WinProvision-Store/) apresenta o aplicativo, as capturas, a demonstração em vídeo e os downloads.

O conteúdo está em `docs/site`. Para preparar uma prévia local, execute `scripts/build-site.ps1` na raiz do projeto e sirva a pasta `artifacts/site` com um servidor HTTP. O workflow `Publicar site do WinProvision` publica as alterações do site e de seus materiais na branch `main`.
