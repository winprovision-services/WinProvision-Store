using System.Reflection;
using System.Text.Json;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OperationsPanelSchemaTests
{
    [Fact]
    public void EmbeddedOperationsPanelJsonConformsToPublishedSchema()
    {
        using JsonDocument schema = JsonDocument.Parse(Read("WinProvision.Store.Tests.operations-panel.schema.json"));
        using JsonDocument instance = JsonDocument.Parse(Read("WinProvision.Store.Assets.operations-panel.json"));
        var errors = new List<string>();
        Validate(instance.RootElement, schema.RootElement, schema.RootElement, "$", errors);
        Assert.Empty(errors);
    }

    private static void Validate(JsonElement value, JsonElement schema, JsonElement root, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out JsonElement reference))
        {
            string name = reference.GetString()!.Split('/').Last();
            Validate(value, root.GetProperty("$defs").GetProperty(name), root, path, errors);
            return;
        }
        if (schema.TryGetProperty("type", out JsonElement type))
        {
            bool matches = type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "number" => value.ValueKind == JsonValueKind.Number,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => true
            };
            if (!matches) { errors.Add($"{path}: expected {type.GetString()}"); return; }
        }
        if (schema.TryGetProperty("const", out JsonElement constant)
            && (value.ValueKind != constant.ValueKind || value.GetRawText() != constant.GetRawText())) errors.Add($"{path}: const mismatch");
        if (schema.TryGetProperty("enum", out JsonElement enumValues)
            && !enumValues.EnumerateArray().Any(item => item.ValueKind == value.ValueKind && item.GetRawText() == value.GetRawText())) errors.Add($"{path}: enum mismatch");
        if (value.ValueKind == JsonValueKind.Number)
        {
            double number = value.GetDouble();
            if (schema.TryGetProperty("minimum", out JsonElement minimum) && number < minimum.GetDouble()) errors.Add($"{path}: below minimum");
            if (schema.TryGetProperty("maximum", out JsonElement maximum) && number > maximum.GetDouble()) errors.Add($"{path}: above maximum");
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            int length = value.GetString()!.Length;
            if (schema.TryGetProperty("minLength", out JsonElement minLength) && length < minLength.GetInt32()) errors.Add($"{path}: below minLength");
            if (schema.TryGetProperty("maxLength", out JsonElement maxLength) && length > maxLength.GetInt32()) errors.Add($"{path}: above maxLength");
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out JsonElement items))
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray()) Validate(item, items, root, $"{path}[{index++}]", errors);
        }
        if (value.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("required", out JsonElement required))
            foreach (JsonElement requiredName in required.EnumerateArray())
                if (!value.TryGetProperty(requiredName.GetString()!, out _)) errors.Add($"{path}: missing {requiredName.GetString()}");
        if (schema.TryGetProperty("propertyNames", out JsonElement propertyNames))
            foreach (JsonProperty property in value.EnumerateObject())
            {
                using JsonDocument propertyName = JsonDocument.Parse("\"" + JsonEncodedText.Encode(property.Name) + "\"");
                Validate(propertyName.RootElement, propertyNames, root, $"{path}.{property.Name}", errors);
            }
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (schema.TryGetProperty("properties", out JsonElement properties) && properties.TryGetProperty(property.Name, out JsonElement propertySchema))
                Validate(property.Value, propertySchema, root, $"{path}.{property.Name}", errors);
            else if (schema.TryGetProperty("additionalProperties", out JsonElement additional))
            {
                if (additional.ValueKind == JsonValueKind.False) errors.Add($"{path}.{property.Name}: additional property");
                else if (additional.ValueKind == JsonValueKind.Object) Validate(property.Value, additional, root, $"{path}.{property.Name}", errors);
            }
        }
    }

    private static string Read(string resource)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
