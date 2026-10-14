# Publicação de versões

Execute o workflow **build-releases** em Actions, na branch `main`.

| Campo | Uso |
| --- | --- |
| Versão | Versão do aplicativo, como `0.1.0` ou `0.2.0-beta.1` |
| Modo | **Somente validar** gera os arquivos para conferência; **Publicar release** publica no GitHub |
| Enviar ao WinGet | Abre uma solicitação de inclusão ou atualização pelo WinGet Create; exige uma release estável publicada |

O workflow testa o Core, valida a interface Native AOT, gera o instalador x64 e o arquivo portátil, prepara os manifestos WinGet e preserva os resultados como artefatos por 14 dias. Os diagnósticos visuais ficam disponíveis por sete dias. Os binários de distribuição não contêm o modo de teste.

Ao publicar, a tag `v<versão>` aponta para o commit escolhido na `main`. Uma tag ou release existente bloqueia uma nova publicação da mesma versão. Releases com sufixo, como `-beta.1`, são marcadas como prévias.

Os anexos incluem o instalador, seu hash, o arquivo portátil, `release.json` com o commit de origem e `SHA256SUMS`. Os arquivos enviados são baixados e comparados por SHA-256 antes de o rascunho ser publicado. Uma falha nessa etapa mantém a release em rascunho para inspeção; resolva o rascunho e a tag antes de repetir a publicação. A opção de validação não cria release nem tag.

## Envio opcional ao WinGet

O identificador do pacote é `WinProvision.Store`. O workflow prepara os manifestos tanto para a primeira inclusão quanto para versões posteriores, confere o hash do instalador na URL pública e usa uma versão fixa do WinGet Create, também verificada por SHA-256.

Os manifestos usam o esquema `1.12` e incluem `/MERGETASKS="clipath"`, para adicionar a CLI ao PATH do usuário durante a instalação pelo WinGet. Abra um novo terminal após a instalação para utilizar o comando.

Para uma versão que já tenha release, use o workflow **Publicar versão existente no WinGet**. Ele baixa o instalador publicado, confere seu hash, gera os manifestos e testa instalação silenciosa, metadados, CLI, inclusão no PATH, reinstalação sem duplicação e desinstalação com restauração do PATH em um runner descartável. Os manifestos e o relatório ficam disponíveis como artefatos. A opção **Enviar ao WinGet** controla o envio após esses testes; desmarcada, ela apenas valida. Esse fluxo preserva a tag e os anexos da release existente.

Cadastre o secret de repositório `WINGET_CREATE_GITHUB_TOKEN` em **Settings → Secrets and variables → Actions**. Use um token pessoal clássico dedicado, com o escopo `public_repo`, conforme as exigências do WinGet Create. Mantenha validade limitada e renove-o quando necessário. Não coloque o valor em arquivos, campos do workflow ou logs. O token padrão do Actions não permite esse envio externo.

O token é passado ao WinGet Create pela variável de ambiente esperada pela ferramenta, sem argumento de linha de comando. O job de envio é separado do build e só roda quando solicitado manualmente após a publicação da release. Sem o secret, o workflow rejeita antecipadamente a opção de envio; as demais modalidades continuam disponíveis.

O envio abre um PR para avaliação do catálogo do WinGet. A disponibilidade pelo WinGet depende da aprovação e das verificações desse catálogo. Uma falha no envio não remove a release já publicada no GitHub. Nesse caso, os manifestos preservados como artefatos permitem revisar e repetir o envio com WinGet Create após resolver o problema.
