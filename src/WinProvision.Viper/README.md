# WinProvision Viper

Motor HTTP(S) próprio em .NET 10/C# 14, com biblioteca e CLI dedicadas. A biblioteca atende os downloads verificados de instaladores do catálogo da Store e das instalações pela API própria. Na Store, **Configurações → Operações e downloads** limita os arquivos simultâneos e divide um orçamento de até 24 conexões entre eles, com até 16 por arquivo. A CLI `winprovision` não mudou. O executável dedicado é `winprovision-viper.exe`.

Implementação original sob a licença MIT do projeto. O aria2 serviu como referência de funcionalidades — divisão em partes, limites por servidor e repetição de requisições. Não contém código, binários ou formatos de sessão do aria2 e não é um fork dele. Marca própria não significa alteração da licença MIT existente.

## Recursos

- Vários arquivos e várias partes de cada arquivo em paralelo, usando I/O assíncrona e escrita em posições independentes.
- Padrões: 3 arquivos, 4 partes por arquivo, 6 conexões por origem e 24 conexões totais por instância.
- Partes de 4 MiB. Arquivos pequenos, servidores sem suporte a intervalos e recursos sem ETag forte usam transferência única.
- Retomada entre execuções: identifica a origem por impressão digital, compara ETag/tamanho e confere SHA-256 de cada parte local antes de reutilizá-la.
- Checkpoints são agrupados para reduzir escritas sincronizadas: após pelo menos um grupo de workers, publica ao acumular 64 MiB ou dois segundos; cancelamento/finalização também salva as partes concluídas. Só partes sincronizadas entram no manifesto. Sem retomada, não grava checkpoints.
- Verificação SHA-256 final, obrigatória quando o chamador informa o hash esperado. Sem hash esperado, o resultado informa o hash calculado, sem comprovar a identidade do fornecedor.
- Ao baixar instaladores do catálogo, o Viper valida o hash antes de publicar. As partes retomáveis também são conferidas individualmente antes de voltar ao lote.
- Validação estrita de Content-Range, tamanho, versão remota e destino dos redirecionamentos; publicação apenas após conclusão e verificação.
- Repetições limitadas com espera exponencial e jitter; respeita Retry-After e reduz concorrência da origem em HTTP 429/503. Esperas além do orçamento configurado falham, sem tentar antes do prazo do servidor.
- TLS/certificados do sistema, proxy do sistema, timeout de cabeçalhos e de inatividade. HTTP exige opção explícita; HTTPS não pode redirecionar para HTTP.
- O destino confirmado pela sondagem é reutilizado pelas partes e pela transferência única, evitando alternância de espelhos durante o mesmo download. A identidade da origem original continua controlando o envio de cabeçalhos privados. Quando um seletor HTTPS anuncia um espelho HTTP na porta padrão, o Viper tenta somente seu endpoint HTTPS na porta padrão TLS; se ele não funcionar ou o certificado for inválido, não recorre a HTTP. Portas HTTP não padrão são rejeitadas nesse fluxo.
- Um cancelamento interno do transporte, quando o token do chamador continua ativo, é tratado como timeout sujeito ao limite de tentativas. Cancelamento do chamador, encerramento do motor e cancelamento dos workers após falha de outra parte continuam sendo propagados sem repetição indevida.
- Conexões alternam famílias IPv6/IPv4 com tentativas espaçadas em 250 ms; uma rota indisponível não bloqueia as outras até o timeout TCP. TLS e proxy continuam sob responsabilidade do handler HTTP.
- Cabeçalhos privados permanecem na origem original e não são gravados em texto no checkpoint. Cookies automáticos são desativados.
- Destino existente nunca é sobrescrito. Um lock por destino impede escritores simultâneos, inclusive entre processos. O chamador pode pedir reutilização idempotente com `ReuseIfVerified`, que só retorna sucesso quando o SHA-256 informado também confere o arquivo local.
- Cancelamento observado; DisposeAsync cancela e aguarda as transferências. Ctrl+C mantém apenas trabalho elegível para retomada.

## CLI

```powershell
winprovision-viper download "https://servidor/arquivo.iso" --output "D:\Downloads\arquivo.iso" --connections 4 --sha256 HASH_SHA256
winprovision-viper download "https://servidor/arquivo.iso" --output "D:\Downloads\arquivo.iso" --sha256 HASH_SHA256 --reuse-verified
winprovision-viper batch "D:\Downloads\lote.json" --parallel 3 --per-host 6 --json
winprovision-viper --help
```

Uma lista de lote contém `url`, `destination`, `sha256` opcional e `reuseIfVerified` opcional. Esse último exige `sha256` e permite tratar um destino final já conferido como concluído, sem rede. Destinos relativos na CLI são resolvidos pelo diretório de trabalho; a API exige caminhos absolutos. Na biblioteca, pedidos equivalentes para o mesmo destino compartilham uma transferência e pedidos conflitantes falham antes de iniciar a rede. A CLI rejeita destinos duplicados antes da execução. Códigos: 0 sucesso, 1 falha, 2 argumentos/lista inválidos, 130 cancelamento. Com `--json`, stdout contém somente os resultados do lote concluído e stderr recebe progresso/erros. Em falhas de lote, arquivos já concluídos são preservados; o comando retorna 1.

```json
[
  { "url": "https://servidor/arquivo.zip", "destination": "D:/Downloads/arquivo.zip", "sha256": "HASH_SHA256", "reuseIfVerified": true }
]
```

## API

```csharp
await using var viper = new ViperEngine(new ViperOptions
{
    MaxConcurrentDownloads = 3,
    ConnectionsPerFile = 4,
    MaxConnectionsPerHost = 6
});
ViperResult result = await viper.DownloadAsync(
    new ViperRequest(new Uri(url), Path.GetFullPath(destination))
    {
        Sha256 = expectedHash,
        ExistingFilePolicy = ViperExistingFilePolicy.ReuseIfVerified
    },
    progress, cancellationToken);
```

`DownloadManyAsync` enumera trabalho com concorrência limitada. Os limites são por instância do motor e por origem (esquema, host, porta); compartilhe uma instância para obter um orçamento único. Não cria uma thread bloqueada por conexão. A instância é reutilizável e pode atender chamadas concorrentes.

Em transferências segmentadas, `ViperProgress.DownloadedBytes` inclui bytes das partes em andamento e é atualizado no máximo a cada 200 ms, além de conclusões de partes. Uma tentativa descartada retira seus bytes do contador antes da repetição. `CompletedPieces` e os checkpoints continuam contando somente partes concluídas e verificadas; bytes recebidos não comprovam integridade. `Verifying` e `Completed` distinguem a conferência final da publicação do arquivo.

## Arquivos de trabalho

Ao lado do destino: `.viper.part` guarda os bytes, `.viper.json` guarda o checkpoint de partes concluídas e `.viper.lock` é temporário. O checkpoint tem formato próprio, versão 1, e não é compatível com `.aria2`. Metadados ausentes, incompatíveis ou com partes corrompidas fazem o motor descartar/rebaixar a retomada; arquivos finalizados permanecem protegidos contra sobrescrita. Arquivos de trabalho devem ficar em pasta confiável do usuário.

Um arquivo segmentado só reutiliza partes após nova sondagem da mesma versão remota. O checkpoint não é um arquivo de credenciais: contém uma impressão digital da URL/cabeçalhos, ETag, tamanho, tamanho das partes e hashes das partes. Os dados são sincronizados no disco antes da publicação atômica do checkpoint. Dados inválidos por SHA-256 final divergente são descartados.

## Compilação e validação

Na raiz do repositório:

```powershell
.\scripts\build-viper.ps1
.\scripts\build-viper.ps1 -NativeAot
dotnet test tests\WinProvision.Viper.Tests\WinProvision.Viper.Tests.csproj
python scripts\test-viper-http.py artifacts\viper\win-x64\winprovision-viper.exe
```

A publicação Native AOT exige as ferramentas C++ do Visual Studio e produz um executável independente do runtime .NET em `artifacts/viper/win-x64`. `-Publish` oferece publicação autossuficiente gerenciada. O script inclui licença, documentação e SHA-256 do executável.

## Limites desta versão

HTTP(S) apenas: sem BitTorrent, FTP, Metalink, RPC, integração com navegador, execução de arquivos, desativação de TLS, rotação de identidade ou resolução de CAPTCHA. O motor respeita autenticação e limites do servidor. Mais conexões não garantem maior velocidade; a versão 0.1 não afirma superar o aria2 em benchmarks gerais. A retomada é conservadora e exige intervalos, tamanho conhecido e ETag forte.
