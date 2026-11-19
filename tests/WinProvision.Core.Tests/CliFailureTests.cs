using System.ComponentModel;
using WinProvision.Cli;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class CliFailureTests
{
    [Theory]
    [InlineData(1618, "Outra instalação está em andamento.")]
    [InlineData(112, "Não há espaço suficiente em disco.")]
    [InlineData(unchecked((int)0x8A150011), "O hash do instalador não corresponde ao esperado.")]
    [InlineData(unchecked((int)0x8A150014), "O pacote não foi encontrado no catálogo.")]
    [InlineData(987654, "A operação retornou o código 987654.")]
    public void BackendFailuresExposeOnlyAShortCause(int code, string expected)
    {
        var result = new WingetExecutionResult { ExitCode = code, Output = "stack trace https://host/?token=segredo C:\\privado\\arquivo" };
        Assert.Equal(expected, CliFailure.Describe(result));
    }

    [Fact]
    public void CanonicalReasonTakesPrecedenceOverBackendText()
    {
        var result = new WingetExecutionResult
        {
            ExitCode = 1, FailureReason = WingetFailureReason.DownloadError,
            Output = "Access is denied. token=segredo"
        };
        Assert.Equal("Falha ao baixar o instalador.", CliFailure.Describe(result));
        result.WingetUnavailable = true;
        Assert.Equal("O WinGet está indisponível.", CliFailure.Describe(result));
    }

    [Fact]
    public void ExceptionsNeverExposeRawTechnicalDetails()
    {
        Assert.Equal("Acesso negado.", CliFailure.Describe(new UnauthorizedAccessException("C:\\privado\\arquivo")));
        Assert.Equal("Falha ao acessar o serviço remoto.", CliFailure.Describe(new HttpRequestException("https://host/?token=segredo")));
        Assert.Equal("Falha ao acessar o serviço remoto.", CliFailure.Describe(new InvalidOperationException("Falha na consulta.", new HttpRequestException("token=segredo"))));
        Assert.Equal("A autorização administrativa não foi concluída.", CliFailure.Describe(new Win32Exception(1223)));
        Assert.Equal("Falha inesperada na operação.", CliFailure.Describe(new Exception("COM HRESULT token=segredo")));
        Assert.Equal("O arquivo de destino já existe.", CliFailure.Describe(new IOException("C:\\privado\\arquivo", unchecked((int)0x80070050))));
    }
}
