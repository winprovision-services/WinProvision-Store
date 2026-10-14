# Desenvolvimento

- Leia `README.md`, `docs/ARCHITECTURE.md` e `docs/DATA-FORMATS.md` antes de alterar funcionalidades ou formatos de dados.
- Use .NET 10, C# 14, WinUI 3 e CommunityToolkit.Mvvm.
- Views apresentam; ViewModels mantêm estado e comandos; serviços integram o sistema. O Core deve continuar independente da interface.
- Prefira propriedades parciais com `[ObservableProperty]`, compatíveis com as projeções WinRT.
- Leia `DESIGN.md` antes de alterar a apresentação. Mantenha português e recursos de tema compartilhados.
- Preserve os contratos de dados e da CLI, observe as tarefas e propague cancelamento. Evite lambdas assíncronas em RelayCommand.
- Execute `scripts/build.ps1` e os testes adequados. Para mudanças de interface, use `scripts/smoke-test.ps1` e inspecione as imagens. Testes simulados não comprovam instalações reais.
- Documente mudanças de compatibilidade e mantenha a documentação restrita ao projeto atual.
