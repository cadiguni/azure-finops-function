namespace Personal.FinOpsApi.AzureFunctions.Models;

public class CostByServiceRow
{
    public string Label { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public string Currency { get; set; } = "BRL";
    public int Count { get; set; } = 1;
    public string? SubscriptionId { get; set; }
}

public class CostByServiceTrendRow
{
    public string Date { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public string Currency { get; set; } = "BRL";
}

public class CostByServiceQueryRecord
{
    public string Label { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public string Currency { get; set; } = "BRL";
    public DateTime? UsageDate { get; set; }
    public int Count { get; set; } = 1;
    public string SubscriptionId { get; set; } = string.Empty;
}

public class CostByServiceQueryResponse
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string Currency { get; set; } = "BRL";
    public List<CostByServiceQueryRecord> Rows { get; set; } = new();
    public string RawJson { get; set; } = string.Empty;
}

public class CostByResourceRow
{
    public string Label { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public string Currency { get; set; } = "BRL";
    public int Count { get; set; } = 1;
    public string? SubscriptionId { get; set; }
}

public class CostByResourceTrendRow
{
    public string Date { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public string Currency { get; set; } = "BRL";
}

public class CostByResourceQueryRecord
{
    public string Label { get; set; } = string.Empty;
    public string ResourceId { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public decimal TotalCost { get; set; }
    public string Currency { get; set; } = "BRL";
    public DateTime? UsageDate { get; set; }
    public int Count { get; set; } = 1;
    public string SubscriptionId { get; set; } = string.Empty;
}

public class CostByResourceQueryResponse
{
    public string SubscriptionId { get; set; } = string.Empty;
    public string Currency { get; set; } = "BRL";
    public List<CostByResourceQueryRecord> Rows { get; set; } = new();
    public string RawJson { get; set; } = string.Empty;
}
