# Privacidade — WinProvision Store

Revisão: 14/10/2026.

Esta política descreve o aplicativo WinProvision Store e sua CLI.

## O aplicativo coleta dados?

O aplicativo lê e armazena dados necessários às funções que você utiliza. Isso inclui informações dos aplicativos instalados, preferências, coleções e resultados das operações.

O código nativo do cliente não possui módulos dedicados de telemetria ou publicidade, nem uma rotina de envio automático do inventário completo para análise pelo mantenedor. As funções online fazem consultas, e a sincronização opcional transmite o conteúdo dos perfis para backup. O navegador integrado tem o comportamento descrito em sua seção abaixo.

## Dados usados no computador

| Dados | Finalidade |
| --- | --- |
| Nome, identificador e versão dos aplicativos instalados | Mostrar o inventário, consultar atualizações e identificar operações |
| Coleções, aplicativos selecionados e opções do Office | Organizar listas e reproduzir configurações escolhidas |
| Preferências de tema, inicialização e instalação | Manter suas escolhas entre sessões |
| Histórico, resultados, códigos de erro e logs | Acompanhar operações e ajudar no diagnóstico |
| Catálogos, imagens e tamanhos de instaladores em cache | Reduzir consultas repetidas e agilizar a apresentação |
| Arquivos de instalação e mídia offline | Preparar downloads e instalações solicitados |

Esses dados ficam no perfil do usuário do Windows ou na pasta de destino escolhida. Logs podem conter nomes e identificadores de aplicativos, caminhos de arquivos e saídas de instaladores. O aplicativo oculta formatos comuns de credenciais, mas essa filtragem pode não reconhecer todas as informações sensíveis.

O cliente não inclui uma função dedicada a coletar documentos pessoais, senhas de outros aplicativos ou histórico de navegação de outros navegadores. Arquivos JSON, ZIP ou XML que você selecionar são lidos para realizar a importação ou operação solicitada.

## Dados transmitidos durante o uso

Consultas de catálogo, imagens, versões e atualizações podem ocorrer automaticamente ao abrir o aplicativo ou suas páginas. Pesquisas e consultas de detalhes podem transmitir nomes ou identificadores dos aplicativos consultados às fontes correspondentes.

Os serviços acessados recebem o endereço IP, o endereço do recurso solicitado e os cabeçalhos HTTP necessários à conexão. Downloads e instaladores também podem se comunicar com os respectivos fornecedores. O aplicativo não controla os registros ou as políticas desses serviços.

Inicialização e atualizações em segundo plano dependem das opções habilitadas pelo usuário. As operações configuradas podem gerar novas consultas e downloads. A CLI também acessa as fontes necessárias aos comandos executados.

Os logs locais não têm uma rotina dedicada de envio automático ao mantenedor. Se você copiar um diagnóstico ou anexar um arquivo a um relato, revise o conteúdo antes de compartilhá-lo.

## Conta e backup opcionais

A conexão com o GitHub é opcional. Quando autorizada, o aplicativo recebe e mantém o nome de usuário, o endereço do avatar, o token de acesso e informações de sincronização, como o identificador do backup e a data da última sincronização.

O token é protegido no computador pelo Windows, vinculado à conta do usuário. Essa proteção não impede acesso por programas maliciosos executando com os mesmos privilégios.

O backup envia ao GitHub todas as coleções não vazias, os aplicativos selecionados e as opções incluídas nos perfis, como configurações do Office, caminhos e argumentos de instalação salvos. Dados adicionais preservados em perfis importados também podem acompanhar o arquivo. Alterações nas coleções podem disparar sincronização automática enquanto a conta estiver conectada.

O conteúdo é armazenado em um Gist secreto. Ele não é um backup criptografado de ponta a ponta, e pessoas com o endereço podem ter acesso ao conteúdo. Não inclua senhas, tokens ou dados confidenciais nos perfis.

Desconectar interrompe a sincronização pelo aplicativo e remove a autorização local. Isso não apaga automaticamente os backups já enviados nem revoga o acesso no serviço. Gerencie os backups e a autorização diretamente na sua conta quando desejar.

## Navegador integrado

A página Sobre pode carregar o site do projeto em um navegador integrado. Esse conteúdo pode usar cookies, armazenamento local e recursos externos. O navegador mantém dados locais próprios; os dados de navegação de outros navegadores não são importados por essa função.

## Controle e retenção

Você pode usar as funções locais sem conectar uma conta e desabilitar as opções de execução em segundo plano. Ao conectar uma conta, considere que a sincronização abrange as coleções não vazias; revise seus perfis antes de habilitá-la. O aplicativo não oferece um bloqueio global interno de toda comunicação de rede. Sem conexão, as funções online ficam indisponíveis.

Caches, logs e dados locais permanecem até a limpeza prevista para cada recurso ou a remoção pelo usuário. A desinstalação pode preservar arquivos no perfil do usuário. Backups remotos, arquivos exportados e instaladores baixados precisam ser gerenciados separadamente.

A retenção de registros pelos serviços acessados depende das políticas desses serviços. O mantenedor não recebe automaticamente uma cópia das coleções sincronizadas na sua conta.

## Contato

Para dúvidas sobre privacidade, entre em contato com o mantenedor Gabriel Silva pelos canais de suporte do projeto. Para solicitar um contato reservado, siga a [política de segurança](SECURITY.md), sem publicar dados pessoais ou credenciais.
