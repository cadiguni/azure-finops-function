using Gvdasa.GVmodeloexemploapi.Modelos.FinOps;

namespace Gvdasa.GVmodeloexemploapi.Infra.Services.FinOps;

public interface IMetricsService
{
    Task<ResourceUsage?> GetResourceUsageAsync(string resourceId, DateTime startDate, DateTime endDate);
    Task<IEnumerable<ResourceUsage>> GetResourceUsageForMultipleResourcesAsync(IEnumerable<string> resourceIds, DateTime startDate, DateTime endDate);
    Task<ResourceUsage?> GetVmMetricsAsync(string resourceId, DateTime startDate, DateTime endDate);
    Task<ResourceUsage?> GetAppServiceMetricsAsync(string resourceId, DateTime startDate, DateTime endDate);
    Task<ResourceUsage?> GetSqlDatabaseMetricsAsync(string resourceId, DateTime startDate, DateTime endDate);
}

public class MetricsService : IMetricsService
{
    private readonly ILogger<MetricsService> _logger;
    private readonly HttpClient _httpClient;

    public MetricsService(ILogger<MetricsService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<ResourceUsage?> GetResourceUsageAsync(string resourceId, DateTime startDate, DateTime endDate)
    {
        try
        {
            _logger.LogInformation("Buscando métricas para recurso {ResourceId}", resourceId);
            
            var resourceType = ExtractResourceTypeFromId(resourceId);
            
            return resourceType.ToLower() switch
            {
                "microsoft.compute/virtualmachines" => await GetVmMetricsAsync(resourceId, startDate, endDate),
                "microsoft.web/sites" => await GetAppServiceMetricsAsync(resourceId, startDate, endDate),
                "microsoft.sql/servers/databases" => await GetSqlDatabaseMetricsAsync(resourceId, startDate, endDate),
                _ => await GetGenericMetricsAsync(resourceId, startDate, endDate)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar métricas para recurso {ResourceId}", resourceId);
            return null;
        }
    }

    public async Task<IEnumerable<ResourceUsage>> GetResourceUsageForMultipleResourcesAsync(IEnumerable<string> resourceIds, DateTime startDate, DateTime endDate)
    {
        var tasks = resourceIds.Select(id => GetResourceUsageAsync(id, startDate, endDate));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).Cast<ResourceUsage>();
    }

    public async Task<ResourceUsage?> GetVmMetricsAsync(string resourceId, DateTime startDate, DateTime endDate)
    {
        try
        {
            // Implementar chamada para Azure Monitor Metrics API para VMs
            // Métricas importantes: Percentage CPU, Available Memory Bytes, Disk Read/Write
            
            _logger.LogInformation("Buscando métricas de VM para {ResourceId}", resourceId);
            
            // TODO: Implementar chamada real
            // https://management.azure.com/{resourceId}/providers/microsoft.insights/metrics
            
            await Task.Delay(50); // Simular latência
            
            return new ResourceUsage
            {
                ResourceId = resourceId,
                ResourceName = ExtractResourceNameFromId(resourceId),
                ResourceType = "Microsoft.Compute/virtualMachines",
                MeasurementDate = DateTime.UtcNow,
                CpuPercentage = 2.5, // Exemplo: VM idle
                MemoryPercentage = 15.0,
                DiskIOPercentage = 1.0,
                IsRunning = true,
                LastStartTime = DateTime.UtcNow.AddDays(-5),
                DaysInactive = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar métricas de VM para {ResourceId}", resourceId);
            return null;
        }
    }

    public async Task<ResourceUsage?> GetAppServiceMetricsAsync(string resourceId, DateTime startDate, DateTime endDate)
    {
        try
        {
            _logger.LogInformation("Buscando métricas de App Service para {ResourceId}", resourceId);
            
            // Métricas importantes: Http Requests, Average Response Time, CPU Percentage, Memory Percentage
            
            await Task.Delay(50);
            
            return new ResourceUsage
            {
                ResourceId = resourceId,
                ResourceName = ExtractResourceNameFromId(resourceId),
                ResourceType = "Microsoft.Web/sites",
                MeasurementDate = DateTime.UtcNow,
                CpuPercentage = 5.0,
                MemoryPercentage = 20.0,
                HttpRequests = 10, // Poucas requests
                ResponseTime = 150.0,
                IsRunning = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar métricas de App Service para {ResourceId}", resourceId);
            return null;
        }
    }

    public async Task<ResourceUsage?> GetSqlDatabaseMetricsAsync(string resourceId, DateTime startDate, DateTime endDate)
    {
        try
        {
            _logger.LogInformation("Buscando métricas de SQL Database para {ResourceId}", resourceId);
            
            // Métricas importantes: DTU Percentage, Storage Percentage, Connection Count
            
            await Task.Delay(50);
            
            return new ResourceUsage
            {
                ResourceId = resourceId,
                ResourceName = ExtractResourceNameFromId(resourceId),
                ResourceType = "Microsoft.Sql/servers/databases",
                MeasurementDate = DateTime.UtcNow,
                DtuPercentage = 8.0,
                StoragePercentage = 25.0,
                IsRunning = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar métricas de SQL Database para {ResourceId}", resourceId);
            return null;
        }
    }

    private async Task<ResourceUsage?> GetGenericMetricsAsync(string resourceId, DateTime startDate, DateTime endDate)
    {
        // Implementar métricas genéricas para outros tipos de recursos
        await Task.Delay(30);
        
        return new ResourceUsage
        {
            ResourceId = resourceId,
            ResourceName = ExtractResourceNameFromId(resourceId),
            ResourceType = ExtractResourceTypeFromId(resourceId),
            MeasurementDate = DateTime.UtcNow,
            IsRunning = true
        };
    }

    private string ExtractResourceTypeFromId(string resourceId)
    {
        // Exemplo: /subscriptions/.../providers/Microsoft.Compute/virtualMachines/vm1
        var parts = resourceId.Split('/');
        var providerIndex = Array.IndexOf(parts, "providers");
        if (providerIndex >= 0 && providerIndex + 2 < parts.Length)
        {
            return $"{parts[providerIndex + 1]}/{parts[providerIndex + 2]}";
        }
        return "Unknown";
    }

    private string ExtractResourceNameFromId(string resourceId)
    {
        return resourceId.Split('/').LastOrDefault() ?? "Unknown";
    }
}