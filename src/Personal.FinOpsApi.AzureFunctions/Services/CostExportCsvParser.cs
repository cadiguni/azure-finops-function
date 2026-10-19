using Personal.FinOpsApi.AzureFunctions.Models;
using System.Globalization;
using System.Text;

namespace Personal.FinOpsApi.AzureFunctions.Services;

public class CostExportCsvParser
{
    private static readonly string[] SubscriptionColumns = ["SubscriptionId", "SubscriptionGuid"];
    private static readonly string[] DateColumns = ["UsageDateTime", "Date", "UsageDate", "UsageStart"];
    private static readonly string[] ServiceColumns = ["ServiceName", "ConsumedService", "MeterCategory", "ProductName"];
    private static readonly string[] ResourceColumns = ["InstanceId", "ResourceId"];
    private static readonly string[] CostColumns = ["CostInBillingCurrency", "PreTaxCost", "Cost"];
    private static readonly string[] CurrencyColumns = ["BillingCurrency", "BillingCurrencyCode", "Currency"];
    private static readonly string[] ResourceGroupColumns = ["ResourceGroup", "ResourceGroupName"];
    private static readonly string[] ResourceLocationColumns = ["ResourceLocation", "Location"];
    private static readonly string[] ResourceTypeColumns = ["ResourceType"];
    private static readonly string[] TagsColumns = ["Tags"];

    public IEnumerable<CostExportRecord> Parse(TextReader reader, string fallbackSubscriptionId)
    {
        var header = ReadRecord(reader);
        if (header == null)
        {
            yield break;
        }

        var columns = header
            .Select((name, index) => new { Name = name.Trim().TrimStart('\uFEFF'), Index = index })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var dateIndex = FindColumn(columns, DateColumns);
        var costIndex = FindColumn(columns, CostColumns);
        if (dateIndex < 0 || costIndex < 0)
        {
            throw new InvalidDataException("O CSV de Cost Management não contém as colunas obrigatórias de data e custo.");
        }

        var subscriptionIndexes = FindColumns(columns, SubscriptionColumns);
        var serviceIndexes = FindColumns(columns, ServiceColumns);
        var resourceIndexes = FindColumns(columns, ResourceColumns);
        var currencyIndex = FindColumn(columns, CurrencyColumns);
        var resourceGroupIndex = FindColumn(columns, ResourceGroupColumns);
        var resourceLocationIndex = FindColumn(columns, ResourceLocationColumns);
        var resourceTypeIndex = FindColumn(columns, ResourceTypeColumns);
        var tagsIndex = FindColumn(columns, TagsColumns);

        var logicalLine = 1;
        IReadOnlyList<string>? values;
        while ((values = ReadRecord(reader)) != null)
        {
            logicalLine++;
            if (values.Count == 0 || values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var rawDate = Read(values, dateIndex);
            if (!TryReadDate(rawDate, out var usageDate))
            {
                throw new InvalidDataException(
                    $"Data inválida na linha lógica {logicalLine}: '{rawDate}'.");
            }

            var rawCost = Read(values, costIndex);
            if (!TryReadDecimal(rawCost, out var cost))
            {
                throw new InvalidDataException(
                    $"Custo inválido na linha lógica {logicalLine}: '{rawCost}'.");
            }

            var subscriptionId = ReadFirst(values, subscriptionIndexes);
            if (!string.IsNullOrWhiteSpace(subscriptionId) &&
                !subscriptionId.Trim().Equals(fallbackSubscriptionId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SubscriptionId do CSV não corresponde ao path do export na linha lógica {logicalLine}: '{subscriptionId.Trim()}'.");
            }

            var serviceName = ReadFirst(values, serviceIndexes);
            var currency = Read(values, currencyIndex);
            var resourceId = ReadFirst(values, resourceIndexes)?.Trim() ?? string.Empty;
            var resourceGroup = Read(values, resourceGroupIndex)?.Trim() ?? string.Empty;
            var resourceType = Read(values, resourceTypeIndex)?.Trim() ?? string.Empty;
            DeriveResourceMetadata(resourceId, ref resourceGroup, ref resourceType);

            yield return new CostExportRecord
            {
                SubscriptionId = fallbackSubscriptionId,
                UsageDate = usageDate,
                ServiceName = string.IsNullOrWhiteSpace(serviceName) ? "Unknown" : serviceName.Trim(),
                ResourceId = resourceId,
                ResourceGroup = resourceGroup,
                ResourceLocation = Read(values, resourceLocationIndex)?.Trim() ?? string.Empty,
                ResourceType = resourceType,
                Tags = Read(values, tagsIndex)?.Trim() ?? string.Empty,
                Cost = cost,
                Currency = string.IsNullOrWhiteSpace(currency) ? "BRL" : currency.Trim()
            };
        }
    }

    private static void DeriveResourceMetadata(string resourceId, ref string resourceGroup, ref string resourceType)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            return;

        var segments = resourceId.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index + 1 < segments.Length; index++)
        {
            if (segments[index].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(resourceGroup))
                resourceGroup = segments[index + 1];

            if (segments[index].Equals("providers", StringComparison.OrdinalIgnoreCase) && index + 2 < segments.Length && string.IsNullOrWhiteSpace(resourceType))
            {
                // Preserve nested resource types (for example Microsoft.Sql/servers/databases).
                var typeSegments = new List<string> { segments[index + 1], segments[index + 2] };
                for (var nestedTypeIndex = index + 4; nestedTypeIndex < segments.Length; nestedTypeIndex += 2)
                {
                    typeSegments.Add(segments[nestedTypeIndex]);
                }
                resourceType = string.Join('/', typeSegments);
                break;
            }
        }
    }

    private static IReadOnlyList<string>? ReadRecord(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        while (true)
        {
            var value = reader.Read();
            if (value < 0)
            {
                if (inQuotes)
                {
                    throw new InvalidDataException("CSV inválido: campo entre aspas não foi encerrado.");
                }

                if (!hasContent && field.Length == 0 && fields.Count == 0)
                {
                    return null;
                }

                fields.Add(field.ToString());
                return fields;
            }

            hasContent = true;
            var character = (char)value;
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            if (character == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (character == '\n')
            {
                fields.Add(field.ToString().TrimEnd('\r'));
                return fields;
            }
            else
            {
                field.Append(character);
            }
        }
    }

    private static int FindColumn(IReadOnlyDictionary<string, int> columns, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (columns.TryGetValue(candidate, out var index))
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<int> FindColumns(
        IReadOnlyDictionary<string, int> columns,
        IEnumerable<string> candidates)
    {
        return candidates
            .Where(columns.ContainsKey)
            .Select(candidate => columns[candidate])
            .ToList();
    }

    private static string? Read(IReadOnlyList<string> values, int index)
    {
        return index >= 0 && index < values.Count ? values[index] : null;
    }

    private static string? ReadFirst(IReadOnlyList<string> values, IEnumerable<int> indexes)
    {
        return indexes
            .Select(index => Read(values, index))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool TryReadDate(string? value, out DateTime date)
    {
        var formats = new[]
        {
            "yyyy-MM-dd",
            "yyyyMMdd",
            "MM/dd/yyyy",
            "M/d/yyyy",
            "dd/MM/yyyy",
            "d/M/yyyy"
        };

        if (DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out date))
        {
            date = date.Date;
            return true;
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dateWithOffset))
        {
            date = dateWithOffset.Date;
            return true;
        }

        date = default;
        return false;
    }

    private static bool TryReadDecimal(string? value, out decimal cost)
    {
        return decimal.TryParse(
            value,
            NumberStyles.Number | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture,
            out cost);
    }
}
