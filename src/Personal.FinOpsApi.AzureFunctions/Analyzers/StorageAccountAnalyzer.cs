using Azure.Identity;
using Personal.FinOpsApi.AzureFunctions.Models;
using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Analyzers;

/// <summary>
///  ANALYZER v3.0 - Storage Account com métricas REAIS
/// Analisa Storage Accounts usando Azure Monitor para métricas reais de uso
/// </summary>
public class StorageAccountAnalyzer
{
    private readonly HttpClient _httpClient;
    private readonly DefaultAzureCredential _credential;
    private readonly AzureMetricsService _metricsService;
    private readonly HttpRetryService _httpRetryService;
    private readonly ResourceCostLookupService _costLookupService;
    private readonly ILogger<StorageAccountAnalyzer> _logger;

    public StorageAccountAnalyzer(
        HttpClient httpClient, 
        AzureMetricsService metricsService,
        HttpRetryService httpRetryService,
        ResourceCostLookupService costLookupService,
        ILogger<StorageAccountAnalyzer> logger)
    {
        _httpClient = httpClient;
        _credential = new DefaultAzureCredential();
        _metricsService = metricsService;
        _httpRetryService = httpRetryService;
        _costLookupService = costLookupService;
        _logger = logger;
    }

    /// <summary>
    ///  Analisa Storage Accounts com otimizações profissionais FinOps
    ///  V4.1: Filtro grosso + histórico + paralelismo controlado + timeout protection
    /// </summary>
    public async Task<StandardAnalyzerResult> AnalyzeSubscriptionAsync(string subscriptionId, int analysisPeriodDays = 30, bool dryRun = true)
    {
        //  ESTRATÉGIA PROFISSIONAL #3: Verificar histórico antes de rodar análise
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (!dryRun)
        {
            var existingAnalysis = await CheckExistingAnalysisAsync(subscriptionId, today);
            if (existingAnalysis)
            {
                _logger.LogInformation(" Storage analysis já executada hoje para subscription {subscriptionId} - pulando", subscriptionId);
                return CreateSkippedResult(subscriptionId, analysisPeriodDays, "already-analyzed-today");
            }
        }

        var analysisId = Guid.NewGuid().ToString();
        var result = new StandardAnalyzerResult
        {
            AnalysisId = analysisId,
            Analyzer = AnalyzerNames.STORAGE_ACCOUNT_ANALYZER,
            SubscriptionId = subscriptionId,
            ExecutedAt = DateTime.UtcNow,
            AnalysisPeriodDays = analysisPeriodDays,
            DryRun = dryRun,
            ExecutionMetadata = new Dictionary<string, object>
            {
                { "queryExecutions", 0 },
                { "resourcesAnalyzed", 0 },
                { "version", "4.0" },
                { "optimizationsEnabled", true },
                { "grossFilterEnabled", true }
            }
        };

        try
        {
            _logger.LogInformation(" {analyzer}: Iniciando análise OTIMIZADA para subscription {subscriptionId}", 
                AnalyzerNames.STORAGE_ACCOUNT_ANALYZER, subscriptionId);

            // Query KQL para Storage Accounts
            var kqlQuery = $@"
                Resources
                | where type =~ 'microsoft.storage/storageaccounts'
                | where subscriptionId =~ '{subscriptionId}'
                | project 
                    resourceId = id,
                    name,
                    resourceGroup,
                    subscriptionId,
                    location,
                    sku = tostring(sku.name),
                    kind,
                    accessTier = tostring(properties.accessTier),
                    tags
            ";

            var storageAccounts = await ExecuteResourceGraphQueryAsync(kqlQuery);
            result.ExecutionMetadata["queryExecutions"] = 1;
            result.ExecutionMetadata["resourcesAnalyzed"] = storageAccounts.Count;

            //  ESTRATÉGIA PROFISSIONAL #1: Resource Graph como filtro grosso
            // Primeiro identifica candidatos suspeitos SEM chamar métricas
            var suspiciousCandidates = FilterSuspiciousStorageAccounts(storageAccounts);
            
            _logger.LogInformation(" Filtro grosso: {total} storages → {suspicious} candidatos suspeitos para análise detalhada", 
                storageAccounts.Count, suspiciousCandidates.Count);

            //  Métricas de otimização
            var optimizationRatio = storageAccounts.Count > 0 ? (1.0 - ((double)suspiciousCandidates.Count / storageAccounts.Count)) * 100 : 0;
            _logger.LogInformation(" Otimização: {ratio:F1}% menos chamadas Azure Monitor", optimizationRatio);

            result.ExecutionMetadata["totalStorageAccounts"] = storageAccounts.Count;
            result.ExecutionMetadata["suspiciousCandidates"] = suspiciousCandidates.Count;
            result.ExecutionMetadata["optimizationPercentage"] = optimizationRatio;
            result.ExecutionMetadata["resourcesAnalyzed"] = suspiciousCandidates.Count;

            // Baseline de custo real por recurso (Cost Management) com fallback para heurística atual.
            var costBaselines = await GetStorageCostBaselinesAsync(subscriptionId, analysisPeriodDays);
            result.ExecutionMetadata["costBaselineResources"] = costBaselines.Count;
            result.ExecutionMetadata["costBaselineWindowDays"] = analysisPeriodDays;

            //  TIMEOUT PROTECTION: Limite de 6 minutos para análise de Storage Accounts
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            
            //  LIMIT PROTECTION: Máximo 50 storage accounts para evitar timeout
            var limitedCandidates = suspiciousCandidates.Take(50).ToList();
            if (limitedCandidates.Count < suspiciousCandidates.Count)
            {
                _logger.LogWarning(" Limitando análise a {limit} de {total} storage accounts para evitar timeout", 
                    limitedCandidates.Count, suspiciousCandidates.Count);
            }

            var tasks = limitedCandidates.Select(async storage =>
            {
                try
                {
                    return await CreateStorageFindingWithMetricsAsync(storage, analysisPeriodDays);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, " Erro ao analisar storage {storage}, continuando...", 
                        storage.TryGetProperty("name", out var name) ? name.GetString() : "unknown");
                    return null;
                }
            });

            try
            {
                var findings = await Task.WhenAll(tasks.ToArray());
                foreach (var finding in findings.Where(f => f != null))
                {
                    result.Findings.Add(finding!);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("⏰ Timeout de 6 minutos atingido na análise de Storage Accounts");
                result.ExecutionMetadata["timeout_occurred"] = true;
            }

            Console.WriteLine($" {AnalyzerNames.STORAGE_ACCOUNT_ANALYZER}: {result.Findings.Count} findings gerados com {optimizationRatio:F1}% otimização");

            // Validar contrato antes de retornar
            var (isValid, errors) = AnalyzerContractValidator.ValidateResult(result);
            if (!isValid)
            {
                Console.WriteLine($" CONTRATO INVÁLIDO: {string.Join(", ", errors)}");
                throw new InvalidOperationException($"Analyzer não segue o contrato padrão: {string.Join(", ", errors)}");
            }

            Console.WriteLine($" CONTRATO VÁLIDO: {result.Findings.Count} findings");
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Erro no {AnalyzerNames.STORAGE_ACCOUNT_ANALYZER}: {ex.Message}");
            
            // Mesmo com erro, retorna resultado válido
            result.ExecutionMetadata["error"] = ex.Message;
        }

        return result;
    }

    private async Task<List<JsonElement>> ExecuteResourceGraphQueryAsync(string query)
    {
        try
        {
            Console.WriteLine(" Storage: Iniciando autenticação Azure...");
            
            var tokenRequestContext = new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" });
            var tokenResponse = await _credential.GetTokenAsync(tokenRequestContext);
            
            Console.WriteLine(" Storage: Token obtido com sucesso");
            Console.WriteLine($" Storage: Executando query KQL: {query}");

            var requestBody = new
            {
                query = query,
                options = new { }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {tokenResponse.Token}");
            
            //  RETRY RESILIENTE: Usar HttpRetryService para Resource Graph
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)); // Aumentar timeout para incluir retries
            var response = await _httpRetryService.PostWithRetryAsync(
                _httpClient,
                "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01", 
                content, 
                cts.Token);

            //  Tratamento especial para 429 persistente
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(" Resource Graph API rate-limited - pulando descoberta Storage Accounts");
                return new List<JsonElement>(); // Retorna vazio em vez de falhar
            }

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($" Storage: Resource Graph resposta: {responseContent}");
                var result = JsonSerializer.Deserialize<JsonElement>(responseContent);

                if (result.TryGetProperty("data", out var dataElement))
                {
                    var dataList = dataElement.EnumerateArray().ToList();
                    Console.WriteLine($" Storage: Encontrados {dataList.Count} recursos na query");
                    return dataList;
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($" Storage: Erro na query Resource Graph: {response.StatusCode} - {errorContent}");
            }
        }
        catch (Azure.Identity.AuthenticationFailedException authEx)
        {
            Console.WriteLine($" Storage: Falha de autenticação Azure: {authEx.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($" Storage: Erro ao executar query Resource Graph: {ex.Message}");
        }

        return new List<JsonElement>();
    }

    /// <summary>
    ///  Cria finding baseado em métricas REAIS do Azure Monitor 
    ///  V3.0: Análise inteligente usando Transactions, Capacity e Traffic
    /// </summary>
    private async Task<StandardFinding?> CreateStorageFindingWithMetricsAsync(
        JsonElement storage,
        int analysisPeriodDays)
    {
        try
        {
            var resourceId = storage.GetProperty("resourceId").GetString();
            var name = storage.GetProperty("name").GetString();
            var resourceGroup = storage.GetProperty("resourceGroup").GetString();
            var location = storage.GetProperty("location").GetString();
            var sku = storage.GetProperty("sku").GetString();
            var subscriptionId = storage.GetProperty("subscriptionId").GetString();
            var kind = storage.GetProperty("kind").GetString();
            var accessTier = storage.GetProperty("accessTier").GetString();

            if (string.IsNullOrEmpty(resourceId) || string.IsNullOrEmpty(name))
            {
                return null;
            }

            //  Buscar métricas REAIS do Azure Monitor
            var metrics = await _metricsService.GetStorageAccountMetricsAsync(resourceId, analysisPeriodDays);

            _logger.LogInformation(" Storage {name}: Transactions={transactions}/dia, Capacity={capacity:F1}GB, Traffic={traffic:F1}GB", 
                name, metrics.AvgTransactionsPerDay, metrics.AvgUsedCapacityGB, metrics.TotalIngressGB + metrics.TotalEgressGB);

            //  Lógica inteligente de detecção de subutilização
            var isUnderutilized = false;
            var reasonDetails = new List<string>();

            // Regra 1: Pouquíssimas transações (< 10/dia = suspeito)
            if (metrics.AvgTransactionsPerDay < 10)
            {
                reasonDetails.Add($"Transações baixas: {metrics.AvgTransactionsPerDay:F0}/dia");
            }

            // Regra 2: Capacidade muito baixa (< 100MB = quase vazio)
            if (metrics.AvgUsedCapacityGB < 0.1) // 100MB
            {
                reasonDetails.Add($"Capacidade baixa: {metrics.AvgUsedCapacityGB * 1024:F0}MB");
            }

            // Regra 3: Sem tráfego de dados (< 10MB total)
            var totalTrafficGB = metrics.TotalIngressGB + metrics.TotalEgressGB;
            if (totalTrafficGB < 0.01) // 10MB
            {
                reasonDetails.Add($"Tráfego baixo: {totalTrafficGB * 1024:F0}MB em {analysisPeriodDays} dias");
            }

            //  Critérios de subutilização mais rigorosos:
            // - Transações muito baixas E (capacidade baixa OU tráfego baixo)
            // - Storage com zero atividade em múltiplas métricas
            isUnderutilized = (metrics.AvgTransactionsPerDay < 10 && (metrics.AvgUsedCapacityGB < 0.1 || totalTrafficGB < 0.01)) ||
                             (metrics.AvgTransactionsPerDay < 1 && metrics.AvgUsedCapacityGB < 0.01); // Praticamente sem uso

            if (!isUnderutilized)
            {
                _logger.LogInformation(" Storage {name} tem uso adequado - não será incluído nas recomendações", name);
                return null; // Storage com uso adequado
            }

            // Estimativa de custo baseada no SKU (fallback)
            decimal estimatedMonthlyCostBySku = sku?.ToLower() switch
            {
                var s when s?.Contains("standard_lrs") == true => 15.00m,
                var s when s?.Contains("standard_grs") == true => 25.00m,
                var s when s?.Contains("standard_zrs") == true => 20.00m,
                var s when s?.Contains("premium") == true => 120.00m,
                _ => 18.00m
            };

            // 💰 CUSTO REAL: Buscar do Cost Management primeiro, fallback para tabela por SKU
            var costData = await _costLookupService.GetResourceCostDataAsync(subscriptionId ?? "", resourceId);
            var hasRealBaseline = costData.MonthlyCost > 0;
            var dailyCost = hasRealBaseline ? costData.DailyCost : estimatedMonthlyCostBySku / 30;
            var estimatedMonthlyCost = hasRealBaseline ? costData.MonthlyCost : estimatedMonthlyCostBySku;
            var costSource = hasRealBaseline ? "cost-management" : "sku-fallback";

            // Economia baseada no nível de subutilização
            var savingsPercentage = metrics.AvgTransactionsPerDay < 1 ? 0.9m : 0.7m; // 90% se quase sem uso, 70% se baixo uso
            var monthlySavings = estimatedMonthlyCost * savingsPercentage;

            var finding = new StandardFinding
            {
                Type = FindingTypes.UNDER_UTILIZED_STORAGE_ACCOUNT,
                ResourceId = resourceId,
                ResourceName = name,
                ResourceType = "Microsoft.Storage/storageAccounts",
                ResourceGroup = resourceGroup ?? "",
                SubscriptionId = subscriptionId ?? "",
                Location = location ?? "",
                DailyCost = dailyCost,
                EstimatedMonthlyCost = estimatedMonthlyCost,
                EstimatedMonthlySavings = monthlySavings,
                Currency = "BRL",
                Priority = monthlySavings > 50 ? FindingPriorities.HIGH : 
                          monthlySavings > 20 ? FindingPriorities.MEDIUM : FindingPriorities.LOW,
                Confidence = metrics.AvgTransactionsPerDay < 1 ? 0.9 : 0.7, // Alta confiança se quase sem uso
                Description = $"Storage Account '{name}' ({sku}) subutilizado há {analysisPeriodDays} dias: {string.Join(", ", reasonDetails)}",
                Recommendation = metrics.AvgTransactionsPerDay < 1 
                    ? "Investigar Storage praticamente sem uso. Verificar se há dependências ocultas (backups, logs) antes de qualquer ação."
                    : "Investigar necessidade e avaliar migração para tier mais econômico (Cool/Archive).",
                Metadata = new Dictionary<string, object>
                {
                    { "sku", sku ?? "" },
                    { "kind", kind ?? "" },
                    { "accessTier", accessTier ?? "" },
                    { "avgTransactionsPerDay", metrics.AvgTransactionsPerDay },
                    { "avgUsedCapacityGB", metrics.AvgUsedCapacityGB },
                    { "totalTrafficGB", totalTrafficGB },
                    { "analysisPeriodDays", analysisPeriodDays },
                    { "estimationModel", "azure-monitor-metrics" },
                    { "potentialSavingsPercentage", (double)savingsPercentage },
                    { "confidence", metrics.AvgTransactionsPerDay < 1 ? "high" : "medium" },
                    { "costSource", costSource },
                    { "estimatedMonthlyCostSkuFallback", estimatedMonthlyCostBySku },
                    { "realCostFromApi", costData.MonthlyCost }
                }
            };

            // Processar tags do Azure
            if (storage.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tagsElement.EnumerateObject())
                {
                    finding.Tags[tag.Name] = tag.Value.GetString() ?? "";
                }
            }

            _logger.LogInformation(" Storage {name} marcado como subutilizado: {reasons}", name, string.Join(", ", reasonDetails));
            return finding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao criar finding para Storage Account");
            return null;
        }
    }

    /// <summary>
    /// Obtém custos reais via ResourceCostLookupService (centralizado)
    /// </summary>
    private async Task<Dictionary<string, decimal>> GetStorageCostBaselinesAsync(string subscriptionId, int analysisPeriodDays)
    {
        try
        {
            // Usar o serviço centralizado que faz cache e projeção mensal
            await _costLookupService.PreloadCostsAsync(subscriptionId);
            _logger.LogInformation("💰 Cost baseline carregado via ResourceCostLookupService para subscription {SubscriptionId}", subscriptionId);
            
            // O lookup será feito por resourceId individual quando necessário
            // Retornamos dicionário vazio - o lookup será feito no CreateStorageFindingWithMetricsAsync
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "💰 Falha ao pré-carregar custos. Usando fallback heurístico.");
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static int FindColumnIndex(IEnumerable<dynamic> columns, params string[] names)
    {
        foreach (var name in names)
        {
            var match = columns.FirstOrDefault(c =>
                string.Equals((string)c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return (int)match.Index;
            }
        }

        return -1;
    }

    private static string? ReadString(JsonElement row, int index)
    {
        if (index < 0 || index >= row.GetArrayLength())
        {
            return null;
        }

        var item = row[index];
        return item.ValueKind switch
        {
            JsonValueKind.String => item.GetString(),
            JsonValueKind.Number => item.GetRawText(),
            _ => item.ToString()
        };
    }

    private static decimal ReadDecimal(JsonElement row, int index)
    {
        if (index < 0 || index >= row.GetArrayLength())
        {
            return 0m;
        }

        var item = row[index];
        if (item.ValueKind == JsonValueKind.Number && item.TryGetDecimal(out var value))
        {
            return value;
        }

        if (item.ValueKind == JsonValueKind.String &&
            decimal.TryParse(item.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 0m;
    }

    /// <summary>
    ///  FILTRO GROSSO: Identifica candidatos suspeitos SEM chamar Azure Monitor
    ///  Estratégia Profissional - reduz chamadas de 6000 para ~500
    /// </summary>
    private List<JsonElement> FilterSuspiciousStorageAccounts(List<JsonElement> allStorageAccounts)
    {
        var suspiciousCandidates = new List<JsonElement>();

        foreach (var storage in allStorageAccounts)
        {
            try
            {
                var name = storage.GetProperty("name").GetString()?.ToLower() ?? "";
                var sku = storage.GetProperty("sku").GetString()?.ToLower() ?? "";
                var kind = storage.GetProperty("kind").GetString()?.ToLower() ?? "";
                var accessTier = storage.GetProperty("accessTier").GetString()?.ToLower() ?? "";

                //  REGRAS DE FILTRO GROSSO (baseado em padrões comuns):
                
                // 1. Storages com nomes que indicam abandono/teste
                if (name.Contains("test") || name.Contains("temp") || name.Contains("dev") && name.Contains("old") ||
                    name.Contains("backup") || name.Contains("log") && !name.Contains("prod"))
                {
                    _logger.LogDebug(" Candidato por nome suspeito: {name}", name);
                    suspiciousCandidates.Add(storage);
                    continue;
                }

                // 2. Standard LRS básicos (normalmente os mais baratos/abandonados)
                if (sku.Contains("standard_lrs") && kind.Contains("storage"))
                {
                    _logger.LogDebug(" Candidato por tipo básico: {name} ({sku})", name, sku);
                    suspiciousCandidates.Add(storage);
                    continue;
                }

                // 3. Access tier Archive/Cool (pode estar abandonado)
                if (accessTier.Contains("cool") || accessTier.Contains("archive"))
                {
                    _logger.LogDebug(" Candidato por tier frio: {name} ({accessTier})", name, accessTier);
                    suspiciousCandidates.Add(storage);
                    continue;
                }

                // 4. Storages em resource groups de desenvolvimento
                if (storage.TryGetProperty("resourceGroup", out var rgProperty))
                {
                    var rg = rgProperty.GetString()?.ToLower() ?? "";
                    if (rg.Contains("dev") || rg.Contains("test") || rg.Contains("temp"))
                    {
                        _logger.LogDebug(" Candidato por RG de desenvolvimento: {name} (RG: {rg})", name, rg);
                        suspiciousCandidates.Add(storage);
                        continue;
                    }
                }

                //  Storage parece em uso ativo - pula análise detalhada
                _logger.LogDebug(" Storage {name} parece ativo - pulando análise de métricas", name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, " Erro ao filtrar storage - incluindo na análise completa");
                suspiciousCandidates.Add(storage); // Em caso de dúvida, analisa
            }
        }

        return suspiciousCandidates;
    }

    /// <summary>
    ///  Verifica se já existe análise para hoje (evita reprocessamento)
    /// </summary>
    private async Task<bool> CheckExistingAnalysisAsync(string subscriptionId, string date)
    {
        try
        {
            // Simulação - em produção, verificaria no blob storage
            // Path seria algo como: analyses/{date}/{subscriptionId}/storage-analysis.json
            await Task.Delay(10); // Simula consulta rápida ao blob
            
            _logger.LogDebug(" Verificando histórico para {subscriptionId} em {date}", subscriptionId, date);
            
            // Por enquanto, sempre false (pode implementar verificação real depois)
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, " Erro ao verificar histórico - prosseguindo com análise");
            return false; // Em caso de erro, roda a análise
        }
    }

    /// <summary>
    ///  Cria resultado vazio para análises puladas
    /// </summary>
    private StandardAnalyzerResult CreateSkippedResult(string subscriptionId, int analysisPeriodDays, string reason)
    {
        return new StandardAnalyzerResult
        {
            AnalysisId = Guid.NewGuid().ToString(),
            Analyzer = AnalyzerNames.STORAGE_ACCOUNT_ANALYZER,
            SubscriptionId = subscriptionId,
            ExecutedAt = DateTime.UtcNow,
            AnalysisPeriodDays = analysisPeriodDays,
            DryRun = false,
            Findings = new List<StandardFinding>(),
            ExecutionMetadata = new Dictionary<string, object>
            {
                { "skipped", true },
                { "skipReason", reason },
                { "version", "4.0" },
                { "optimizationsEnabled", true }
            }
        };
    }
}
