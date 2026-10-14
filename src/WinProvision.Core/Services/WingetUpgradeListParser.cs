using System.Linq;
using System.Text.RegularExpressions;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Faz o parsing da saída de texto de <c>winget upgrade</c> (tabela alinhada por coluna).
///
/// Diferente de <c>winget export</c>, o <c>winget upgrade</c> não tem uma opção de saída
/// estruturada (JSON) — só imprime uma tabela de texto formatada pro console. Esse parser
/// usa a técnica padrão pra esse cenário: acha a linha de cabeçalho (a que vem logo antes
/// da linha de traços "----...") e usa a posição inicial de cada palavra do cabeçalho como
/// o limite de cada coluna nas linhas de dados seguintes — como as colunas do winget são
/// alinhadas com espaços (padding), o valor de cada linha de dados começa na mesma posição
/// que o título da coluna correspondente.
///
/// Limitação conhecida: assume que cada cabeçalho de coluna é uma única palavra (verdade em
/// pt-BR e en-US: "Nome/Id/Versão/Disponível/Origem" e "Name/Id/Version/Available/Source").
/// Colunas usam células de terminal, não índices UTF-16: nomes CJK, emojis e grafemas
/// compostos devem preservar o alinhamento do identificador. TryParse rejeita formatos
/// desconhecidos; Parse lança FormatException para distinguir falha de resultado vazio.
/// </summary>
public static class WingetUpgradeListParser
{
    public static List<UpgradablePackage> Parse(string rawOutput)
    {
        if (!TryParse(rawOutput, out var result))
        {
            throw new FormatException("A saída do WinGet não corresponde a uma tabela de atualizações reconhecida.");
        }

        return result;
    }

    public static bool TryParse(string rawOutput, out List<UpgradablePackage> packages,
        bool requireAvailableVersion = true) => TryParse(rawOutput, out packages, requireAvailableVersion, false);

    public static bool TryParse(
        string rawOutput,
        out List<UpgradablePackage> packages,
        bool requireAvailableVersion,
        bool allowMissingAvailableColumn)
    {
        packages = [];
        var parsedPackages = new List<UpgradablePackage>();
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return false;
        }

        if (IsNoUpdatesMessage(rawOutput))
        {
            return true;
        }

        string[] lines = rawOutput.Replace("\r\n", "\n").Split('\n');

        int separatorIndex = Array.FindIndex(lines, line =>
        {
            string trimmed = line.Trim();
            return trimmed.Length >= 5 && trimmed.All(c => c == '-');
        });

        if (separatorIndex <= 0)
        {
            return false;
        }

        string headerLine = lines[separatorIndex - 1];
        var headerMatches = Regex.Matches(headerLine, @"\S+");

        if (headerMatches.Count < 3)
        {
            return false;
        }

        bool hasRecognizedHeader = headerMatches.Any(match =>
            match.Value.Equals("Nome", StringComparison.OrdinalIgnoreCase)
            || match.Value.Equals("Name", StringComparison.OrdinalIgnoreCase)
            || match.Value.Equals("Identificação", StringComparison.OrdinalIgnoreCase)
            || match.Value.Equals("Id", StringComparison.OrdinalIgnoreCase)
            || match.Value.Equals("ID", StringComparison.OrdinalIgnoreCase)
            || match.Value.Equals("Versão", StringComparison.OrdinalIgnoreCase)
            || match.Value.Equals("Version", StringComparison.OrdinalIgnoreCase)
            || match.Value.Contains("Dispon", StringComparison.OrdinalIgnoreCase)
            || match.Value.Contains("Avail", StringComparison.OrdinalIgnoreCase));
        if (!hasRecognizedHeader)
        {
            return false;
        }

        int[] columnStarts = headerMatches.Select(m => TerminalColumnWidth.Measure(headerLine[..m.Index])).ToArray();

        // Identifica os índices das colunas a partir do cabeçalho
        int nameCol = -1, idCol = -1, versionCol = -1, availableCol = -1, sourceCol = -1;
        for (int c = 0; c < headerMatches.Count; c++)
        {
            string val = headerMatches[c].Value;
            if (val.Equals("Nome", StringComparison.OrdinalIgnoreCase) || val.Equals("Name", StringComparison.OrdinalIgnoreCase))
                nameCol = c;
            else if (val.Equals("Identificação", StringComparison.OrdinalIgnoreCase) || val.Equals("Id", StringComparison.OrdinalIgnoreCase) || val.Equals("ID", StringComparison.OrdinalIgnoreCase))
                idCol = c;
            else if (val.Equals("Versão", StringComparison.OrdinalIgnoreCase) || val.Equals("Version", StringComparison.OrdinalIgnoreCase))
                versionCol = c;
            else if (val.Contains("Dispon", StringComparison.OrdinalIgnoreCase) || val.Contains("Avail", StringComparison.OrdinalIgnoreCase))
                availableCol = c;
            else if (val.Equals("Origem", StringComparison.OrdinalIgnoreCase) || val.Equals("Source", StringComparison.OrdinalIgnoreCase))
                sourceCol = c;
        }

        // Fallbacks posicionais caso o idioma do cabeçalho seja inesperado
        if (nameCol < 0) nameCol = 0;
        if (idCol < 0) idCol = 1;
        if (versionCol < 0 && headerMatches.Count > 2) versionCol = 2;
        if (availableCol < 0 && headerMatches.Count >= 5 && !allowMissingAvailableColumn) availableCol = 3;
        if (sourceCol < 0 && headerMatches.Count > 4) sourceCol = headerMatches.Count - 1;

        bool hasAvailableColumn = availableCol >= 0;
        if ((!hasAvailableColumn && !allowMissingAvailableColumn) || (hasAvailableColumn && (availableCol == nameCol || availableCol == idCol || availableCol == versionCol)))
        {
            return false;
        }

        for (int i = separatorIndex + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                // Linha em branco = fim da tabela (o rodapé de resumo do winget vem depois dela).
                break;
            }

            // Detecta linhas de resumo/rodapé do winget (ex: "2 atualizações disponíveis.", "2 upgrades available.")
            if (IsFooterOrSummaryLine(trimmed))
            {
                break;
            }

            if (TerminalColumnWidth.Measure(line) <= columnStarts[idCol])
            {
                return false;
            }

            string[] columns = SliceByColumns(line, columnStarts);

            string name = nameCol < columns.Length ? columns[nameCol].Trim() : string.Empty;
            string id = idCol < columns.Length ? columns[idCol].Trim() : string.Empty;

            if (string.IsNullOrWhiteSpace(id) || IsFooterOrSummaryLine(name) || IsFooterOrSummaryLine(id))
            {
                return false;
            }

            string currentVersion = versionCol >= 0 && versionCol < columns.Length ? columns[versionCol].Trim() : string.Empty;
            string availableVersion = availableCol >= 0 && availableCol < columns.Length ? columns[availableCol].Trim() : string.Empty;
            string source = sourceCol >= 0 && sourceCol < columns.Length ? columns[sourceCol].Trim() : string.Empty;

            // Se a tabela possui coluna de versão disponível (como em "winget upgrade"),
            // a linha precisa obrigatoriamente ter uma versão disponível válida.
            // Se for vazia, não é uma linha de pacote atualizável válida (ex.: resto de rodapé).
            if (requireAvailableVersion && string.IsNullOrWhiteSpace(availableVersion))
            {
                return false;
            }

            // Identificador do winget não deve conter espaços e não deve ser resquício de palavra cortada (ex.: "is.")
            if (id.Contains(' ') && !(allowMissingAvailableColumn && id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            parsedPackages.Add(new UpgradablePackage
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name,
                CurrentVersion = currentVersion,
                AvailableVersion = availableVersion,
                Source = source
            });
        }

        packages = parsedPackages;
        return true;
    }

    private static bool IsNoUpdatesMessage(string output) =>
        Regex.IsMatch(
            output,
            @"(?:no applicable upgrades? found|no upgrades? available|nenhuma atualização(?: aplicável)? encontrada|nenhuma atualização disponível|não há atualizações disponíveis)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool IsFooterOrSummaryLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();

        // Linhas que iniciam com contagem: "2 atualizações disponíveis.", "3 upgrades available.", etc.
        if (Regex.IsMatch(trimmed, @"^\d+\s+(atualizaç|upgrade|pacote|package)", RegexOptions.IgnoreCase))
            return true;

        // Frases típicas de resumo de atualizações do winget
        if (Regex.IsMatch(trimmed, @"(atualizaç[õo]es?\s+dispon[íi]ve|upgrades?\s+available)", RegexOptions.IgnoreCase))
            return true;

        if (Regex.IsMatch(trimmed, @"(vers[õo]es?\s+fixadas?|pinned\s+versions?)", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private static string[] SliceByColumns(string line, int[] columnStarts)
    {
        var columns = new string[columnStarts.Length];
        int[] offsets = TerminalColumnWidth.GetStringOffsets(line, columnStarts);

        for (int i = 0; i < columnStarts.Length; i++)
        {
            int start = offsets[i];
            int end = i < offsets.Length - 1 ? offsets[i + 1] : line.Length;

            columns[i] = end > start ? line[start..end] : string.Empty;
        }

        return columns;
    }
}
