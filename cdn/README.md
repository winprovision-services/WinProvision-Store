# Estrutura local de staging da CDN

Esta pasta espelha a raiz de staging `wps-cdn/winget/v1`. Os dados reais permanecem em `C:\Users\Gabriel\Documents\wps-cdn`; este diretório do repositório documenta a estrutura-alvo e não deve ser usado como fonte de publicação.

- `catalog.json`: manifesto e ponteiro da publicação atual.
- `search-index.json`: índice único, carregado para busca local.
- `manifests/<partição>/<publisher>/<pacote>/versions.json`: lista de versões do pacote.
- `manifests/.../versions/<versão>/app.json`: metadados e instaladores daquela versão.
- `manifests/.../media`: ícone e screenshots do pacote, compartilhados entre versões.
- `media/sha256/<prefixo>/`: objetos globais deduplicados por hash quando compartilhados entre pacotes.

O `app.json` fica dentro da pasta da versão, seguindo o padrão de organização do WinGet-Pkgs. A estrutura do staging real está em `Documents\wps-cdn\winget\v1`.
