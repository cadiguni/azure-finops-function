using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ManagementGroups;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
/// Serviço para consultar Management Groups reais do tenant Azure
/// </summary>
public class AzureManagementGroupService
{
    private readonly ArmClient _armClient;
    private readonly TokenCredential _credential;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureManagementGroupService> _logger;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, ManagementGroupData> _cache = new();
    private DateTime? _lastCacheUpdate;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromHours(1);
    private Dictionary<string, string>? _cachedManagementGroups;
    private Dictionary<string, ParentManagementGroupInfo>? _cachedSubscriptionParents;

    public AzureManagementGroupService(
        ArmClient armClient,
        TokenCredential credential,
        IConfiguration configuration,
        ILogger<AzureManagementGroupService> logger)
    {
        _armClient = armClient;
        _credential = credential;
        _configuration = configuration;
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// Obtém todos os Management Groups do tenant (implementação simplificada)
    /// </summary>
    public async Task<Dictionary<string, string>> GetManagementGroupsAsync()
    {
        try
        {
            await EnsureHierarchyCacheAsync();
            return _cachedManagementGroups != null
                ? new Dictionary<string, string>(_cachedManagementGroups, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Erro ao consultar Management Groups do tenant");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Mapeia subscription para Management Group baseado nos dados reais
    /// </summary>
    public async Task<string?> GetManagementGroupForSubscriptionAsync(string subscriptionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                return null;
            }

            await EnsureHierarchyCacheAsync();
            var normalizedSubscriptionId = subscriptionId.Trim();
            if (_cachedSubscriptionParents != null
                && _cachedSubscriptionParents.TryGetValue(normalizedSubscriptionId, out var parent))
            {
                return parent.ManagementGroupId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Não foi possível obter Management Group para subscription {sub}", 
                subscriptionId.Length >= 8 ? subscriptionId[..8] : subscriptionId);
        }

        return null;
    }

    public async Task<Dictionary<string, ParentManagementGroupInfo>> GetParentManagementGroupsForSubscriptionsAsync(
        IEnumerable<string> subscriptionIds)
    {
        await EnsureHierarchyCacheAsync();

        var result = new Dictionary<string, ParentManagementGroupInfo>(StringComparer.OrdinalIgnoreCase);
        if (_cachedSubscriptionParents == null)
        {
            return result;
        }

        foreach (var subscriptionId in subscriptionIds.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var normalizedSubscriptionId = subscriptionId.Trim();
            if (_cachedSubscriptionParents.TryGetValue(normalizedSubscriptionId, out var parent))
            {
                result[normalizedSubscriptionId] = parent;
            }
        }

        return result;
    }

    public async Task<Dictionary<string, ParentManagementGroupInfo>> GetAllSubscriptionParentManagementGroupsAsync()
    {
        await EnsureHierarchyCacheAsync();

        return _cachedSubscriptionParents != null
            ? new Dictionary<string, ParentManagementGroupInfo>(_cachedSubscriptionParents, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ParentManagementGroupInfo>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task EnsureHierarchyCacheAsync()
    {
        if (_lastCacheUpdate.HasValue
            && (DateTime.UtcNow - _lastCacheUpdate.Value) < _cacheExpiration
            && _cachedManagementGroups != null
            && _cachedSubscriptionParents != null)
        {
            return;
        }

        var rootManagementGroupId = _configuration["AZURE_MANAGEMENT_GROUP_ID"] ?? _configuration["root_management_group"];
        if (string.IsNullOrWhiteSpace(rootManagementGroupId))
        {
            _logger.LogWarning("⚠️ AZURE_MANAGEMENT_GROUP_ID não configurado. Não foi possível carregar hierarquia de Management Groups.");
            _cachedManagementGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _cachedSubscriptionParents = new Dictionary<string, ParentManagementGroupInfo>(StringComparer.OrdinalIgnoreCase);
            _lastCacheUpdate = DateTime.UtcNow;
            return;
        }

        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
            CancellationToken.None);

        JsonDocument? doc = null;
        var apiVersions = new[] { "2023-04-01", "2021-04-01", "2020-05-01" };
        foreach (var apiVersion in apiVersions)
        {
            var endpoint = $"https://management.azure.com/providers/Microsoft.Management/managementGroups/{Uri.EscapeDataString(rootManagementGroupId)}/descendants?api-version={apiVersion}";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "[MG-HIERARCHY] Falha ao consultar descendants. mg={mg}, apiVersion={apiVersion}, status={status}, body={body}",
                    rootManagementGroupId, apiVersion, (int)response.StatusCode, errorBody);
                continue;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            doc = JsonDocument.Parse(responseBody);
            _logger.LogInformation("[MG-HIERARCHY] Descendants carregados com apiVersion={apiVersion}", apiVersion);
            break;
        }

        if (doc == null)
        {
            _logger.LogError("[MG-HIERARCHY] Não foi possível carregar hierarchy do MG {mg} em nenhuma api-version.", rootManagementGroupId);
            _cachedManagementGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _cachedSubscriptionParents = new Dictionary<string, ParentManagementGroupInfo>(StringComparer.OrdinalIgnoreCase);
            _lastCacheUpdate = DateTime.UtcNow;
            return;
        }

        var managementGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parentBySubscription = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            _cachedManagementGroups = managementGroups;
            _cachedSubscriptionParents = new Dictionary<string, ParentManagementGroupInfo>(StringComparer.OrdinalIgnoreCase);
            _lastCacheUpdate = DateTime.UtcNow;
            doc.Dispose();
            return;
        }

        foreach (var item in values.EnumerateArray())
        {
            var itemType = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
            var itemId = item.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            var itemName = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;

            var displayName = itemName;
            if (item.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("displayName", out var displayNameElement)
                && displayNameElement.ValueKind == JsonValueKind.String)
            {
                displayName = displayNameElement.GetString() ?? itemName;
            }

            if (itemType.Contains("managementGroups", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(itemName))
                {
                    managementGroups[itemName] = displayName;
                }

                if (!string.IsNullOrWhiteSpace(itemId))
                {
                    managementGroups[itemId] = displayName;
                }
            }

            if (!itemType.Contains("subscriptions", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var subscriptionId = ExtractSubscriptionId(itemName, itemId);
            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                continue;
            }

            var parentId = string.Empty;
            if (item.TryGetProperty("properties", out var subProperties)
                && subProperties.TryGetProperty("parent", out var parent)
                && parent.TryGetProperty("id", out var parentIdElement)
                && parentIdElement.ValueKind == JsonValueKind.String)
            {
                parentId = parentIdElement.GetString() ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(parentId))
            {
                parentBySubscription[subscriptionId] = parentId;
            }
        }

        var parents = new Dictionary<string, ParentManagementGroupInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parentBySubscription)
        {
            var parentId = pair.Value;
            var parentName = ExtractManagementGroupName(parentId);
            var displayName = managementGroups.GetValueOrDefault(parentName, parentName);

            parents[pair.Key] = new ParentManagementGroupInfo
            {
                ManagementGroupId = parentName,
                ManagementGroupDisplayName = displayName
            };
        }

        _cachedManagementGroups = managementGroups;
        _cachedSubscriptionParents = parents;
        _lastCacheUpdate = DateTime.UtcNow;
        _logger.LogInformation("✅ Hierarquia de Management Groups carregada: {mgCount} MGs, {subCount} subscriptions com parent",
            managementGroups.Count, parents.Count);
        doc.Dispose();
    }

    private static string ExtractSubscriptionId(string itemName, string itemId)
    {
        if (!string.IsNullOrWhiteSpace(itemName) && Guid.TryParse(itemName, out _))
        {
            return itemName;
        }

        if (!string.IsNullOrWhiteSpace(itemId))
        {
            var parts = itemId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i].Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
                {
                    return parts[i + 1];
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractManagementGroupName(string managementGroupResourceId)
    {
        if (string.IsNullOrWhiteSpace(managementGroupResourceId))
        {
            return string.Empty;
        }

        var parts = managementGroupResourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("managementGroups", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return managementGroupResourceId;
    }

    /// <summary>
    /// Extrai informações de time do nome do Management Group
    /// </summary>
    private string ExtractTeamFromName(string name)
    {
        // Patterns comuns de nomenclatura
        var teamPatterns = new[]
        {
            // Time-Prod, Time-Dev, etc.
            @"^(\w+)-(prod|dev|test|hml)",
            // Financeiro, Comercial, TI, etc.
            @"(financeiro|comercial|ti|tecnologia|dev|desenvolvimento|producao|produção)",
            // Departamentos
            @"(depto|dept|departamento)[\s-](\w+)",
            // Áreas
            @"(area|área)[\s-](\w+)"
        };

        var nameLower = name.ToLowerInvariant();
        
        foreach (var pattern in teamPatterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(nameLower, pattern);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        // Fallback: usar o próprio nome limpo
        return name.Replace("Management Group", "").Replace("MG", "").Trim();
    }
}

public class ParentManagementGroupInfo
{
    public string ManagementGroupId { get; set; } = string.Empty;
    public string ManagementGroupDisplayName { get; set; } = string.Empty;
}
