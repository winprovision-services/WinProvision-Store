using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ShellShortcutTests
{
    [Fact]
    public void NativeShellLinkRoundTripPreservesUnicodeTarget()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-atalho-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "Aplicação de teste.exe");
            File.WriteAllText(target, "teste");
            string path = Path.Combine(directory, "Teste.lnk");
            using (var shortcut = new ShellShortcut()) shortcut.Save(path, target, "Descrição de teste");
            using var reader = new ShellShortcut();
            Assert.Equal(target, reader.ReadTarget(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
