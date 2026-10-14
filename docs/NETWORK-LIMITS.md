# Comunicação de rede do cliente

O WinProvision Store consulta informações de aplicativos, imagens, versões e atualizações para apresentar os recursos online. A sincronização de coleções depende de uma conta conectada.

## Espera e cancelamento

As consultas compartilhadas pelo cliente possuem controle de frequência e espera para reduzir chamadas repetidas. Quando uma fonte solicita mais tempo, o aplicativo pode aguardar ou encerrar a consulta por limite de tempo.

O cancelamento deve ser propagado às tarefas observadas. Uma falha de consulta precisa ser apresentada sem indicar que uma instalação foi concluída.

## Alcance

O controle das consultas do cliente não é um limitador geral de velocidade de download. Ferramentas externas, o navegador integrado e os aplicativos instalados podem fazer suas próprias conexões.

Sem internet, os recursos que dependem de consultas online ficam indisponíveis. O aplicativo não oferece um bloqueio global interno de toda comunicação.

Consulte a [política de privacidade](../PRIVACY.md) para saber quais dados são usados e transmitidos.
