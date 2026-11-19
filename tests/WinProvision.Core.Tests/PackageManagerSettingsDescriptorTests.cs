using System.Text.Json;
using Xunit;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Core.Tests;

public sealed class PackageManagerSettingsDescriptorTests
{
    [Fact]
    public void DescriptorUsesVersionedTypedFieldShape()
    {
        var descriptor = new PackageManagerSettingsDescriptor
        {
            Id = "example",
            DisplayName = "Example",
            Enabled = false,
            Status = new PackageManagerSettingsStatus { State = "ready", Version = "1.2", Executable = "example.exe" },
            Fields = new[] { new PackageManagerSettingsField
            {
                Id = "architecture", Type = "choice", Section = "install", Label = "Architecture", Value = "x64", DefaultValue = "default",
                Choices = new[] { new PackageManagerSettingsChoice("x64", "x64") }
            }, new PackageManagerSettingsField
            {
                Id = "installLocation", Type = "path", PathKind = "folder", Section = "install", Label = "Install folder",
                Value = null, DefaultValue = null, Placeholder = "Package default"
            } },
            Sources = new PackageManagerSettingsSources { Supported = true, Items = Array.Empty<PackageManagerSettingsSource>() },
            Actions = new PackageManagerSettingsActions
            {
                Supported = true,
                Items = new[] { new PackageManagerSettingsAction("selectExecutable", "Procurar...", "button", Section: "status") }
            }
        };

        string json = JsonSerializer.Serialize(descriptor, CoreJsonContext.Default.PackageManagerSettingsDescriptor);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("example", root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("enabled").GetBoolean());
        Assert.Equal("x64", root.GetProperty("fields")[0].GetProperty("value").GetString());
        Assert.Equal("choice", root.GetProperty("fields")[0].GetProperty("type").GetString());
        Assert.Equal("default", root.GetProperty("fields")[0].GetProperty("defaultValue").GetString());
        Assert.Equal("path", root.GetProperty("fields")[1].GetProperty("type").GetString());
        Assert.Equal("folder", root.GetProperty("fields")[1].GetProperty("pathKind").GetString());
        Assert.Equal("ready", root.GetProperty("status").GetProperty("state").GetString());
        Assert.True(root.GetProperty("sources").GetProperty("supported").GetBoolean());
        Assert.Empty(root.GetProperty("sources").GetProperty("items").EnumerateArray());
        Assert.True(root.GetProperty("actions").GetProperty("supported").GetBoolean());
        Assert.Equal("status", root.GetProperty("actions").GetProperty("items")[0].GetProperty("section").GetString());
    }
}
