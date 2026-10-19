namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Classificação canônica de recursos de banco usada pelos resumos e pelas APIs de custo.
/// </summary>
public static class DatabaseResourceClassifier
{
    public const string SqlEngine = "Sql";
    public const string CosmosEngine = "Cosmos";

    public static bool IsSql(string? resourceType)
        => !string.IsNullOrWhiteSpace(resourceType)
           && resourceType.Contains("microsoft.sql", StringComparison.OrdinalIgnoreCase);

    public static bool IsCosmos(string? resourceType)
        => !string.IsNullOrWhiteSpace(resourceType)
           && (resourceType.Contains("microsoft.documentdb", StringComparison.OrdinalIgnoreCase)
               || resourceType.Contains("cosmos", StringComparison.OrdinalIgnoreCase));

    public static string? ResolveEngine(string? resourceType, string? serviceName = null)
    {
        if (IsSql(resourceType)) return SqlEngine;
        if (IsCosmos(resourceType)) return CosmosEngine;

        if (string.Equals(serviceName, "Azure SQL Database", StringComparison.OrdinalIgnoreCase)) return SqlEngine;
        if (string.Equals(serviceName, "Azure Cosmos DB", StringComparison.OrdinalIgnoreCase)) return CosmosEngine;
        return null;
    }
}
