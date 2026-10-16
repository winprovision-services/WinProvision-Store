# WinProvision Elevator

Variante do [gsudo de Gerardo Grignoli](https://github.com/gerardog/gsudo), mantida no [fork WinProvision](https://github.com/winprovision-services/gsudo/tree/codex/winprovision-elevator). A licença MIT original e o copyright permanecem em [LICENSE.txt](LICENSE.txt).

## Conteúdo e origem

- [manifest.json](manifest.json): versão, commit do fork, base upstream e SHA-256 do executável distribuído.
- [win-x64/WinProvision.Elevator.exe](win-x64/WinProvision.Elevator.exe): helper Native AOT para Windows x64.
- [winprovision.patch](winprovision.patch): alterações de fonte em relação ao gsudo v2.6.1.
- [rebuild.ps1](rebuild.ps1): recompila o commit fixado em uma pasta nova de `artifacts`, sem substituir automaticamente o binário distribuído.

A variante incorpora o ícone do WinProvision Store e os metadados de exibição WinProvision Elevator. Usa .NET 10, nome `WinProvision.Elevator.exe`, registro `SOFTWARE\WinProvision\Gsudo`, pipes `WinProvision_gsudo` e eventos de invalidação próprios. Preserva as verificações de cliente, ACLs e o cache explícito do upstream. Ajusta as chamadas de marshalling para as versões genéricas compatíveis com Native AOT e remove o hook de build que encerrava processos globais de gsudo.

O executável atual não tem assinatura Authenticode. O hash fixado no Core confere o arquivo antes de executá-lo; isso não equivale a assinatura digital, nem implementa o reconhecimento por certificado da variante da Devolutions. Não foram incluídos código, marcas ou certificados da Devolutions.

## Uso no WinProvision

O helper acompanha a interface e a CLI, cada uma com sua pasta `Tool/gsudo`. Não modifica o PATH nem instala um serviço permanente. No modo Automático, requisitos de elevação identificados pela COM são encaminhados à cadeia API própria/CLI antes de despachar a instalação. As elevações explícitas de instaladores da API própria, fallback WinGet e ODT usam o helper; solicitações feitas diretamente pelo servidor COM ou por instaladores que elevam a si próprios continuam sob controle desses componentes.

O cache é desativado por padrão e não é persistido entre sessões. Nas Configurações, o usuário pode autorizar a sessão por 2, 5, 10 ou 15 minutos de inatividade ou encerrar essa autorização. Na CLI, `--elevation-cache` solicita 5 minutos quando a primeira operação elevada do helper for necessária. O cache é vinculado ao PID do WinProvision e seus descendentes; nunca é iniciado com PID 0. O encerramento normal pede a invalidação desse PID e o upstream observa também a saída do processo e a expiração por inatividade.

A autorização inicial continua usando o UAC. Depois da expiração, uma nova operação pode solicitar autorização novamente. O cache não torna outros programas nem a interface permanentemente administrativos. A configuração de duração selecionada só se aplica quando o usuário autoriza a sessão novamente.

Cancelamento é aceito antes de despachar um instalador. Após o despacho, o motor aguarda o resultado real, preservando seus arquivos e evitando um segundo backend. A recusa ou falha do helper não dispara uma segunda solicitação de UAC pelo mecanismo nativo e impede pedidos repetidos na mesma sessão. Uma nova autorização explícita nas Configurações permite tentar novamente. Para compatibilidade de checkouts antigos, a ausência do helper mantém o caminho nativo; distribuições novas exigem binário e licença durante a publicação, e um helper presente com hash inválido é rejeitado.

## Atualização

Atualize primeiro o fork a partir de uma release upstream, revise o diff e execute o workflow do fork. Recompile com o SDK fixado, teste elevação/cache em Windows e copie o executável revisado. Atualize juntos `manifest.json`, `GsudoBinary.cs`, a licença se necessário e o patch. Não aceite automaticamente uma release `latest` em tempo de execução.

O build local e o do CI podem produzir hashes diferentes conforme o ambiente e o toolchain nativo; o inventário identifica os bytes efetivamente distribuídos. A arquitetura ARM64 ainda não é distribuída por esta integração.
