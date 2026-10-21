# Verificações de segurança do cliente

Este documento descreve os cuidados e verificações do WinProvision Store. Ele não é uma certificação de ausência de vulnerabilidades.

## Cuidados implementados

- Validação de entradas antes de iniciar operações ou modificar arquivos.
- Uso de conexões HTTPS nas funções online.
- Verificação de integridade por padrão. Nas instalações e atualizações individuais via WinGet, o usuário pode optar por ignorar o hash somente na operação atual; os downloads da API própria continuam exigindo SHA-256.
- Proteção local de tokens pela conta do Windows.
- Ocultação de formatos comuns de credenciais em diagnósticos.
- Validação de caminhos e conteúdo em arquivos importados.
- Cancelamento e acompanhamento dos processos e tarefas iniciados.

## Verificações de desenvolvimento

Execute os testes de regressão e confira entradas inválidas, arquivos incompletos, cancelamento e preservação dos dados existentes.

Antes de compartilhar uma publicação, verifique os arquivos incluídos, as dependências e a ausência de credenciais ou dados pessoais. Os comandos de compilação e validação estão no [README](../README.md).

## Limites

Testes simulados verificam cenários controlados e não comprovam instalações reais. Um hash confere a correspondência de um arquivo com o valor esperado; não garante que o programa seja seguro nem verifica todos os arquivos que um instalador possa obter posteriormente.

A proteção local de um token não impede acesso por programas executando com os mesmos privilégios. A filtragem de logs pode não reconhecer todos os dados sensíveis.

Consulte a [política de segurança](../SECURITY.md) para relatar problemas e a [política de privacidade](../PRIVACY.md) para entender os dados utilizados.
