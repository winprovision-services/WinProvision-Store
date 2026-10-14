# Office online e offline

O WinProvision Store permite escolher os aplicativos do Office, a arquitetura, os idiomas e as opções de instalação.

## Instalação online

O instalador obtém os arquivos necessários durante a instalação. É preciso manter conexão com a internet até a conclusão das etapas que dependem de download.

## Preparação offline

No modo offline, o aplicativo baixa primeiro a mídia completa e só inicia a instalação após concluir a preparação. Falhas ou cancelamento não devem transformar uma mídia incompleta em uma instalação online.

O conjunto preparado inclui:

- `setup.exe`: ferramenta de instalação.
- `configuration.xml`: opções de instalação.
- Pasta `Office/Data`: mídia do produto.
- `payload-sha256.txt`: inventário de integridade da mídia.

O download inicial exige internet, espaço livre e pode levar vários minutos. As fontes offline ficam no perfil do usuário; use a ação **Abrir pasta** para localizá-las.

## Coleções

O modo escolhido é salvo na configuração do Office da coleção. Atualizar a configuração existente preserva o item sem criar uma duplicata.

Na exportação de uma coleção, o conjunto online contém a ferramenta e a configuração; o conjunto offline inclui também a mídia preparada e o script de instalação.

O conjunto offline conserva a versão da mídia e pode ser transferido para outra pasta ou computador. Não altere os arquivos após a geração do inventário de integridade.

## Linha de comando

```powershell
winprovision --install --office
winprovision --office
winprovision --office --offline
```

O primeiro comando instala após confirmação. O segundo baixa a ferramenta, o XML e o script para instalação online. O terceiro baixa também a mídia completa para uso offline. Os downloads da CLI ficam em Downloads.

O padrão da CLI é Microsoft 365 Família, 64 bits, português do Brasil, canal Current, com Access e Publisher excluídos. Consulte [CLI](CLI.md) para detalhes.

## Verificação e utilização

O script `Install-Office.ps1 -VerifyOnly` confere os arquivos preparados sem iniciar a instalação. Execute o script sem essa opção quando desejar instalar.

Os hashes locais ajudam a detectar arquivos alterados ou incompletos após a preparação. Eles não constituem uma garantia sobre todos os arquivos obtidos durante uma instalação online.

A mídia offline não substitui a licença e a ativação do produto. Atualizações e ativação podem exigir conexão posteriormente.

## Validação de desenvolvimento

Os testes automatizados utilizam arquivos e processos simulados. Eles verificam configuração, caminhos, hashes, cancelamento e preservação de arquivos, mas não comprovam uma instalação real do Office.
