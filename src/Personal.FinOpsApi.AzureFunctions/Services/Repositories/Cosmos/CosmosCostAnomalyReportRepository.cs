using Personal.FinOpsApi.AzureFunctions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Services.Repositories.Cosmos;

public class CosmosCostAnomalyReportRepository : CosmosRepositoryBase, ICostAnomalyReportRepository
{
    private const string DocumentType = "costAnomalyReport";
    private readonly ILogger<CosmosCostAnomalyReportRepository> _logger;

    public CosmosCostAnomalyReportRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<CosmosCostAnomalyReportRepository> logger)
        : base(cosmosClient, configuration, "CosmosCostAnomaliesContainerName", "costAnomalies")
    {
        _logger = logger;
    }

    public async Task SaveReportAsync(CostAnomalyReport report, CancellationToken cancellationToken = default)
    {
        var requestedDate = string.IsNullOrWhiteSpace(report.RequestedDate) ? report.Date : report.RequestedDate;
        if (string.IsNullOrWhiteSpace(requestedDate))
        {
            requestedDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        }

        var document = new CostAnomalyReportDocument
        {
            Id = requestedDate,
            Type = DocumentType,
            RequestedDate = requestedDate,
            UpdatedAt = DateTime.UtcNow,
            Report = report
        };

        await Container.UpsertItemAsync(
            document,
            new PartitionKey(document.RequestedDate),
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<CostAnomalyReport>> LoadReportsAsync(DateTime baseDate, int days, CancellationToken cancellationToken = default)
    {
        var boundedDays = Math.Clamp(days, 1, 30);
        var requestedDates = Enumerable.Range(0, boundedDays)
            .Select(offset => baseDate.AddDays(-offset).ToString("yyyy-MM-dd"))
            .ToArray();

        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.type = @type AND ARRAY_CONTAINS(@dates, c.requestedDate)")
            .WithParameter("@type", DocumentType)
            .WithParameter("@dates", requestedDates);

        var iterator = Container.GetItemQueryIterator<CostAnomalyReportDocument>(query);
        var reportsByDate = new Dictionary<string, CostAnomalyReport>(StringComparer.OrdinalIgnoreCase);
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            foreach (var item in page)
            {
                if (item.Report == null)
                {
                    continue;
                }

                var key = string.IsNullOrWhiteSpace(item.RequestedDate) ? item.Id : item.RequestedDate;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                reportsByDate[key] = item.Report;
            }
        }

        if (reportsByDate.Count == 0)
        {
            _logger.LogInformation("[COST-ANOMALY] Nenhum snapshot encontrado no Cosmos para {baseDate} (days={days}).", baseDate.ToString("yyyy-MM-dd"), boundedDays);
            return Array.Empty<CostAnomalyReport>();
        }

        return requestedDates
            .Where(date => reportsByDate.ContainsKey(date))
            .Select(date => reportsByDate[date])
            .ToList();
    }

    public async Task<CostAnomalyReport?> LoadLatestReportAsync(CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition(
            "SELECT TOP 1 * FROM c WHERE c.type = @type ORDER BY c.requestedDate DESC")
            .WithParameter("@type", DocumentType);

        var iterator = Container.GetItemQueryIterator<CostAnomalyReportDocument>(
            query,
            requestOptions: new QueryRequestOptions { MaxItemCount = 1 });
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            var latest = page.FirstOrDefault();
            if (latest?.Report != null)
            {
                return latest.Report;
            }
        }

        return null;
    }

    private sealed class CostAnomalyReportDocument
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = DocumentType;
        public string RequestedDate { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
        public CostAnomalyReport? Report { get; set; }
    }
}
