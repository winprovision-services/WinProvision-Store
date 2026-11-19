using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(OperationHistoryRecord))]
internal partial class HistoryJsonContext : JsonSerializerContext;

internal sealed class HistoryWriteJsonContext(JsonSerializerOptions options) : HistoryJsonContext(options);
