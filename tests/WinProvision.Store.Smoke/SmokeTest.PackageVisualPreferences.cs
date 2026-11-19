using WinProvision.Store.Services;

namespace WinProvision.Store;

internal static partial class SmokeTest
{
    private static async Task VerifyPackageVisualPreferencesAsync()
    {
        var catalogWarnings = new List<string>();
        const string invalidCatalog = """
            {
              "schemaVersion": 1,
              "fields": [
                { "id": "showPackageIcons", "type": "toggle", "label": "Ícones", "description": "Exibe ícones.", "defaultValue": "sometimes" },
                { "id": "showPackageScreenshots", "type": "toggle", "label": "Capturas", "description": "Exibe capturas.", "defaultValue": "false" }
              ]
            }
            """;
        PackageVisualPreferencesCatalog invalidCatalogResult =
            PackageVisualPreferencesCatalog.Load(invalidCatalog, catalogWarnings.Add);
        Check(invalidCatalogResult.GetDefaultValue("showPackageIcons")
                == ApplicationPreferencesService.DefaultShowPackageIcons
            && catalogWarnings.Count > 0,
            "Preferências visuais: definição de catálogo inválida usa o padrão e registra aviso");
        Check(PackageVisualPreferencesCatalog.Default.GetDefaultValue("showPackageIcons")
                == ApplicationPreferencesService.DefaultShowPackageIcons
            && PackageVisualPreferencesCatalog.Default.GetDefaultValue("showPackageScreenshots")
                == ApplicationPreferencesService.DefaultShowPackageScreenshots,
            "Preferências visuais: padrões embutidos e retrocompatíveis permanecem alinhados");

        string invalidPreferencesPath = Path.Combine(Path.GetTempPath(),
            $"winprovision-visual-invalid-{Guid.NewGuid():N}.json");
        string roundTripPreferencesPath = Path.Combine(Path.GetTempPath(),
            $"winprovision-visual-roundtrip-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(invalidPreferencesPath,
                """{"appearance":{"showPackageIcons":"invalid","showPackageScreenshots":false}}""");
            var invalidPreferences = new ApplicationPreferencesService(invalidPreferencesPath, persistStartup: false);
            Check(invalidPreferences.ShowPackageIcons
                    == PackageVisualPreferencesCatalog.Default.GetDefaultValue("showPackageIcons")
                && !invalidPreferences.ShowPackageScreenshots,
                "Preferências visuais: valor inválido recorre ao padrão sem descartar a outra preferência");

            var preferences = new ApplicationPreferencesService(roundTripPreferencesPath, persistStartup: false);
            var visualPreferences = new PackageVisualPreferencesService(preferences);
            preferences.SetPackageVisualPreference("showPackageIcons", false);
            preferences.SetPackageVisualPreference("showPackageScreenshots", false);
            var reloaded = new ApplicationPreferencesService(roundTripPreferencesPath, persistStartup: false);
            Check(!reloaded.ShowPackageIcons && !reloaded.ShowPackageScreenshots,
                "Preferências visuais: escolhas são persistidas em appearance no arquivo existente");

            int iconRequests = 0;
            Task<string?> FakeIconLoader()
            {
                iconRequests++;
                return Task.FromResult<string?>("fake-icon");
            }

            string[] screens = ["discover", "updates", "installed", "collections"];
            string[] modes = ["list", "details", "grid"];
            foreach (string screen in screens)
            foreach (string mode in modes)
            {
                string? result = await visualPreferences.LoadIfEnabledAsync(
                    PackageImageKind.PackageIcon, FakeIconLoader);
                Check(result is null && iconRequests == 0,
                    $"Ícones: carregador não recebe requisições com a opção desativada em {screen}/{mode}");
            }

            preferences.SetPackageVisualPreference("showPackageIcons", true);
            int expectedIconRequests = 0;
            foreach (string screen in screens)
            foreach (string mode in modes)
            {
                await visualPreferences.LoadIfEnabledAsync(PackageImageKind.PackageIcon, FakeIconLoader);
                Check(iconRequests == ++expectedIconRequests,
                    $"Ícones: carregador recebe uma requisição quando ativado em {screen}/{mode}");
            }
            Check(iconRequests == screens.Length * modes.Length,
                "Ícones: carregador volta a receber uma requisição por tela e modo quando ativado");

            int screenshotRequests = 0;
            Task<string?> FakeScreenshotLoader()
            {
                screenshotRequests++;
                return Task.FromResult<string?>("fake-screenshot");
            }

            await visualPreferences.LoadIfEnabledAsync(PackageImageKind.Screenshot, FakeScreenshotLoader);
            Check(screenshotRequests == 0
                && visualPreferences.GetScreenshots((string[])["https://example.test/shot.png"]).Count == 0,
                "Capturas: opção desativada bloqueia carregamento e remove a galeria do modelo");
            preferences.SetPackageVisualPreference("showPackageScreenshots", true);
            await visualPreferences.LoadIfEnabledAsync(PackageImageKind.Screenshot, FakeScreenshotLoader);
            Check(screenshotRequests == 1
                && visualPreferences.GetScreenshots((string[])[
                    "https://example.test/shot.png", "http://example.test/insecure.png",
                    "https://example.test/shot.png"
                ]).SequenceEqual((string[])["https://example.test/shot.png"]),
                "Capturas: opção ativada carrega e mantém somente URLs HTTPS únicas");
        }
        finally
        {
            File.Delete(invalidPreferencesPath);
            File.Delete(roundTripPreferencesPath);
        }
    }
}
