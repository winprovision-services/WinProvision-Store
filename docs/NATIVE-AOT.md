# Publicação Native AOT

O WinProvision Store utiliza Native AOT como publicação padrão x64. A saída é autossuficiente e mantém os recursos necessários à interface WinUI 3.

## Requisitos

Use o SDK indicado em `global.json`, as ferramentas MSVC C++ x64/x86 e o Windows SDK.

```powershell
.\scripts\publish.ps1
```

A publicação é preparada em `artifacts/publish/win-x64`. Distribua o conjunto completo de arquivos gerados, preservando bibliotecas e recursos.

## Cuidados de implementação

- Mantenha a serialização preparada para Native AOT.
- Preserve os metadados necessários aos bindings da interface.
- Confira a interoperabilidade com o Windows no executável publicado.
- Não distribua dados simulados ou recursos exclusivos de teste.

## Validação

Use `scripts/publish.ps1 -Validation` para gerar uma publicação separada de testes. Saídas em `artifacts/validation` não devem ser distribuídas.

O smoke verifica navegação, bindings e temas. Instalações, atualizações, remoções e preparação real do Office precisam de validação própria.

Para diagnóstico, `scripts/publish.ps1 -Jit` gera a alternativa gerenciada. Essa opção não altera o padrão Native AOT da distribuição.
