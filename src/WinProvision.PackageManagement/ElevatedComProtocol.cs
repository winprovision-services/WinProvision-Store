using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

// Private, versioned host/worker protocol. No shell commands or manifest URLs are accepted.
public sealed record ElevatedComRequest(string Operation, string PackageId, string? InstallLocation);

public static class ElevatedComProtocol
{
    public const string Command = "--internal-elevated-com-v2";
    public const string ResultPrefix = "WINPROVISION-COM-RESULT-V1:";
    public const string ProgressPrefix = "WINPROVISION-COM-PROGRESS-V1:";

    public static string EncodeRequest(ElevatedComRequest request)
    {
        Validate(request);
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request, ElevatedComJsonContext.Default.ElevatedComRequest));
    }

    public static ElevatedComRequest DecodeRequest(string encoded)
    {
        if (encoded.Length > 16384) throw new ArgumentException("Pedido COM excede o limite.");
        var request = JsonSerializer.Deserialize(Convert.FromBase64String(encoded), ElevatedComJsonContext.Default.ElevatedComRequest)
            ?? throw new ArgumentException("Pedido COM inválido.");
        Validate(request);
        return request;
    }

    private static void Validate(ElevatedComRequest request)
    {
        if (request.Operation is not ("install" or "update" or "uninstall") || string.IsNullOrWhiteSpace(request.PackageId)
            || request.PackageId.Length > 256 || request.PackageId.Any(char.IsControl)
            || !char.IsLetterOrDigit(request.PackageId[0])
            || request.PackageId.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '-')))
            throw new ArgumentException("Operação ou identificador COM inválido.");
        if (request.InstallLocation is { } location && (location.Length > 4096
            || !Path.IsPathFullyQualified(location) || location.Any(char.IsControl)))
            throw new ArgumentException("Diretório de instalação inválido.");
    }

    public static string EncodeResult(WingetExecutionResult result)
        => ResultPrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(result, ElevatedComJsonContext.Default.WingetExecutionResult));
    public static WingetExecutionResult DecodeResult(string line)
        => JsonSerializer.Deserialize(Convert.FromBase64String(line[ResultPrefix.Length..]), ElevatedComJsonContext.Default.WingetExecutionResult)
            ?? throw new InvalidDataException("Resultado COM vazio.");
    public static string EncodeProgress(InstallProgressUpdate progress)
        => ProgressPrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(progress, ElevatedComJsonContext.Default.InstallProgressUpdate));
    public static InstallProgressUpdate DecodeProgress(string line)
        => JsonSerializer.Deserialize(Convert.FromBase64String(line[ProgressPrefix.Length..]), ElevatedComJsonContext.Default.InstallProgressUpdate)
            ?? throw new InvalidDataException("Progresso COM vazio.");
}

[JsonSerializable(typeof(ElevatedComRequest))]
[JsonSerializable(typeof(WingetExecutionResult))]
[JsonSerializable(typeof(InstallProgressUpdate))]
internal partial class ElevatedComJsonContext : JsonSerializerContext;
