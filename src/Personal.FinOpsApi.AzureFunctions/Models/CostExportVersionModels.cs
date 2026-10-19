using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Models;

public sealed class CostExportDailySnapshot
{
    public string SubscriptionId { get; init; } = "";
    public string PublicationMode { get; init; } = "";
    public DateTime Date { get; init; }
    public string ExportName { get; init; } = "";
    public string RunId { get; init; } = "";
    public string CostBasis { get; init; } = "ActualCost";
    public DateTimeOffset SubmittedTime { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public bool IsTombstone { get; init; }
    public string? TombstoneCurrency { get; init; }
    public IReadOnlyList<CostByServiceRow> ByService { get; init; } = [];
    public IReadOnlyList<CostByResourceRow> ByResource { get; init; } = [];
    public IReadOnlyList<CostAllocationSummary> Allocation { get; init; } = [];

    // Import time is deliberately excluded: retries of the same payload keep the same identity.
    public string Revision => Hash(JsonSerializer.Serialize(new
    {
        SubscriptionId, PublicationMode, Date, ExportName, RunId, CostBasis, SubmittedTime, IsTombstone, TombstoneCurrency,
        ByService, ByResource, Allocation
    }));

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class CostExportDayReference
{
    public string Revision { get; init; } = "";
    public string RunId { get; init; } = "";
    public DateTimeOffset SubmittedTime { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    public string CostBasis { get; init; } = "ActualCost";
}

public sealed class CostExportRevisionIndex
{
    public string SubscriptionId { get; init; } = "";
    public string PublicationMode { get; init; } = "";
    public SortedDictionary<string, CostExportDayReference> Days { get; init; } = new(StringComparer.Ordinal);
    public string Revision => CostExportDailySnapshot.Hash(string.Join("|", Days.Select(d => $"{d.Key}:{d.Value.Revision}")));

    public CostExportRevisionIndex Merge(IEnumerable<CostExportDailySnapshot> snapshots)
    {
        var days = new SortedDictionary<string, CostExportDayReference>(Days, StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            if (!snapshot.SubscriptionId.Equals(SubscriptionId, StringComparison.OrdinalIgnoreCase)
                || snapshot.PublicationMode != PublicationMode)
                throw new InvalidDataException("Snapshot fora do escopo do índice.");
            var key = snapshot.Date.ToString("yyyy-MM-dd");
            if (days.TryGetValue(key, out var current))
            {
                var order = snapshot.SubmittedTime.CompareTo(current.SubmittedTime);
                if (order < 0 || (order == 0 && string.CompareOrdinal(snapshot.RunId, current.RunId) < 0)) continue;
                if (order == 0 && snapshot.RunId == current.RunId && snapshot.Revision != current.Revision)
                    throw new InvalidDataException("O mesmo run retornou conteúdos diferentes para a mesma data.");
                if (snapshot.Revision == current.Revision) continue;
            }
            days[key] = new CostExportDayReference
            {
                Revision = snapshot.Revision, RunId = snapshot.RunId, SubmittedTime = snapshot.SubmittedTime,
                ImportedAt = snapshot.ImportedAt, CostBasis = snapshot.CostBasis
            };
        }
        return new CostExportRevisionIndex { SubscriptionId = SubscriptionId, PublicationMode = PublicationMode, Days = days };
    }
}
