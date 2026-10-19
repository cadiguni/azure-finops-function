using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Lê a coluna Tags do Cost Management Export. O formato varia entre versões de export: ora um
/// objeto JSON completo, ora só os pares internos, ora vazio.
/// </summary>
public static class CostTagParser
{
    /// <summary>
    /// Retorna o valor da tag informada, ou null quando ausente, vazia ou ilegível.
    /// A comparação do nome ignora maiúsculas e acentuação de espaços.
    /// </summary>
    public static string? TryGetTagValue(string? rawTags, string tagName)
    {
        if (string.IsNullOrWhiteSpace(rawTags) || string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        foreach (var (key, value) in ParseTags(rawTags))
        {
            if (key.Equals(tagName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }

        return null;
    }

    public static IReadOnlyList<KeyValuePair<string, string>> ParseTags(string? rawTags)
    {
        if (string.IsNullOrWhiteSpace(rawTags))
        {
            return [];
        }

        var text = rawTags.Trim();
        // Exports que trazem apenas os pares (sem as chaves externas) viram um objeto válido.
        if (!text.StartsWith('{'))
        {
            text = "{" + text + "}";
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var tags = new List<KeyValuePair<string, string>>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                tags.Add(new KeyValuePair<string, string>(
                    property.Name.Trim(),
                    property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.ToString()));
            }

            return tags;
        }
        catch (JsonException)
        {
            return ParseLoosePairs(rawTags);
        }
    }

    /// <summary>
    /// Reserva para exports que escapam aspas de forma inconsistente e não formam JSON válido.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, string>> ParseLoosePairs(string rawTags)
    {
        var tags = new List<KeyValuePair<string, string>>();
        foreach (var pair in rawTags.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = Clean(pair[..separator]);
            var value = Clean(pair[(separator + 1)..]);
            if (!string.IsNullOrEmpty(key))
            {
                tags.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return tags;
    }

    private static string Clean(string value) => value.Trim().Trim('"', '\'', '{', '}').Trim();
}
