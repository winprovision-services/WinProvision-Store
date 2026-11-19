using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace WinProvision.Core.Services;

/// <summary>JSON contracts resolved exclusively from generated metadata, including on Native AOT.</summary>
public static class AotJson
{
    private static readonly List<IJsonTypeInfoResolver> Resolvers = [CoreJsonContext.Default];
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Options = new();
    private static readonly JsonSerializerOptions Defaults = new();

    public static void Register(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        lock (Resolvers)
        {
            Resolvers.Insert(0, resolver);
            Options.Clear();
        }
    }

    private static JsonSerializerOptions Resolve(JsonSerializerOptions? options)
        => Options.GetValue(options ?? Defaults, original =>
        {
            var copy = new JsonSerializerOptions(original);
            lock (Resolvers) copy.TypeInfoResolver = JsonTypeInfoResolver.Combine(Resolvers.ToArray());
            copy.MakeReadOnly();
            return copy;
        });

    public static JsonTypeInfo<T> TypeInfo<T>(JsonSerializerOptions? options = null)
        => (JsonTypeInfo<T>)Resolve(options).GetTypeInfo(typeof(T));

    public static T? Deserialize<T>(string json, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize(json, TypeInfo<T>(options));

    public static T? Deserialize<T>(Stream json, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize(json, TypeInfo<T>(options));

    public static T? Deserialize<T>(ReadOnlySpan<byte> json, JsonSerializerOptions? options = null)
        => JsonSerializer.Deserialize(json, TypeInfo<T>(options));

    public static ValueTask<T?> DeserializeAsync<T>(Stream stream, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        => JsonSerializer.DeserializeAsync(stream, TypeInfo<T>(options), cancellationToken);

    public static string Serialize<T>(T value, JsonSerializerOptions? options = null)
    {
        using var output = new MemoryStream();
        Write(output, value, Resolve(options));
        return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions? options = null)
    {
        using var output = new MemoryStream();
        Write(output, value, Resolve(options));
        return output.ToArray();
    }

    private static void Write(Stream output, object? value, JsonSerializerOptions options)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = options.WriteIndented,
            Encoder = options.Encoder,
            MaxDepth = options.MaxDepth == 0 ? 64 : options.MaxDepth
        });
        WriteValue(writer, value, options);
        writer.Flush();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        if (value is JsonNode node) { node.WriteTo(writer, options); return; }
        if (value is JsonElement element) { element.WriteTo(writer); return; }
        if (value is byte[] bytes) { writer.WriteBase64StringValue(bytes); return; }
        if (value is IDictionary dictionary)
        {
            writer.WriteStartObject();
            foreach (DictionaryEntry item in dictionary)
            {
                string name = (string)item.Key;
                writer.WritePropertyName(options.DictionaryKeyPolicy?.ConvertName(name) ?? name);
                WriteValue(writer, item.Value, options);
            }
            writer.WriteEndObject();
            return;
        }
        if (value is IEnumerable sequence && value is not string)
        {
            writer.WriteStartArray();
            foreach (var item in sequence) WriteValue(writer, item, options);
            writer.WriteEndArray();
            return;
        }
        JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));
    }
}
