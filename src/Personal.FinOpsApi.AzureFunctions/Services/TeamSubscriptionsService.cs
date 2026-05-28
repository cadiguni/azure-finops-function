using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services
{
    /// <summary>
    /// Modelo simples de configuração de times e subscriptions
    /// </summary>
    public class TeamSubscriptionsConfig
    {
        public List<TeamConfig> Teams { get; set; } = new();
        public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.UtcNow;
    }

    public class TeamConfig
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Email { get; set; }
        public List<string> SubscriptionIds { get; set; } = new();
        public List<string> SubscriptionNames { get; set; } = new();
    }

    /// <summary>
    /// Serviço simplificado para gerenciar mapeamento de times para subscriptions
    /// Armazena no blob config/team-subscriptions.json
    /// </summary>
    public class TeamSubscriptionsService
    {
        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger<TeamSubscriptionsService> _logger;
        private readonly string _containerName;
        private const string BlobPath = "config/team-subscriptions.json";
        
        private TeamSubscriptionsConfig? _cachedConfig;
        private readonly SemaphoreSlim _loadSemaphore = new(1, 1);

        public TeamSubscriptionsService(
            BlobServiceClient blobServiceClient,
            IConfiguration configuration,
            ILogger<TeamSubscriptionsService> logger)
        {
            _blobServiceClient = blobServiceClient;
            _logger = logger;
            _containerName = configuration["CONFIG_CONTAINER_NAME"] ?? "finops-config";
        }

        /// <summary>
        /// Obtém a configuração de times e subscriptions
        /// </summary>
        public async Task<TeamSubscriptionsConfig> GetConfigAsync()
        {
            await _loadSemaphore.WaitAsync();
            try
            {
                if (_cachedConfig != null)
                    return _cachedConfig;

                var container = _blobServiceClient.GetBlobContainerClient(_containerName);
                var blob = container.GetBlobClient(BlobPath);

                if (!await blob.ExistsAsync())
                {
                    _logger.LogInformation("📄 Arquivo team-subscriptions.json não existe, retornando config vazia");
                    _cachedConfig = new TeamSubscriptionsConfig();
                    return _cachedConfig;
                }

                var content = await blob.DownloadContentAsync();
                _cachedConfig = JsonSerializer.Deserialize<TeamSubscriptionsConfig>(
                    content.Value.Content.ToString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? new TeamSubscriptionsConfig();

                _logger.LogInformation("✅ Carregadas configurações de {Count} times", _cachedConfig.Teams.Count);
                return _cachedConfig;
            }
            finally
            {
                _loadSemaphore.Release();
            }
        }

        /// <summary>
        /// Salva a configuração de times
        /// </summary>
        public async Task SaveConfigAsync(TeamSubscriptionsConfig config)
        {
            config.LastUpdated = DateTimeOffset.UtcNow;
            
            var container = _blobServiceClient.GetBlobContainerClient(_containerName);
            await container.CreateIfNotExistsAsync();
            
            var blob = container.GetBlobClient(BlobPath);
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions 
            { 
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });
            
            await blob.UploadAsync(BinaryData.FromString(json), overwrite: true);
            _cachedConfig = config;
            
            _logger.LogInformation("💾 Salvas configurações de {Count} times", config.Teams.Count);
        }

        /// <summary>
        /// Adiciona ou atualiza um time
        /// </summary>
        public async Task<TeamConfig> UpsertTeamAsync(TeamConfig team)
        {
            var config = await GetConfigAsync();
            
            var existing = config.Teams.FirstOrDefault(t => 
                t.Id.Equals(team.Id, StringComparison.OrdinalIgnoreCase));
            
            if (existing != null)
            {
                existing.Name = team.Name;
                existing.Email = team.Email;
                existing.SubscriptionIds = team.SubscriptionIds;
                existing.SubscriptionNames = team.SubscriptionNames;
            }
            else
            {
                config.Teams.Add(team);
            }
            
            await SaveConfigAsync(config);
            return team;
        }

        /// <summary>
        /// Remove um time
        /// </summary>
        public async Task<bool> DeleteTeamAsync(string teamId)
        {
            var config = await GetConfigAsync();
            var team = config.Teams.FirstOrDefault(t => 
                t.Id.Equals(teamId, StringComparison.OrdinalIgnoreCase));
            
            if (team == null) return false;
            
            config.Teams.Remove(team);
            await SaveConfigAsync(config);
            return true;
        }

        /// <summary>
        /// Obtém um time específico
        /// </summary>
        public async Task<TeamConfig?> GetTeamAsync(string teamId)
        {
            var config = await GetConfigAsync();
            return config.Teams.FirstOrDefault(t => 
                t.Id.Equals(teamId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Obtém as subscription IDs de um time
        /// </summary>
        public async Task<List<string>> GetTeamSubscriptionIdsAsync(string teamId)
        {
            var team = await GetTeamAsync(teamId);
            return team?.SubscriptionIds ?? new List<string>();
        }

        /// <summary>
        /// Verifica se uma subscription pertence a um time
        /// </summary>
        public async Task<bool> IsSubscriptionInTeamAsync(string subscriptionId, string teamId)
        {
            var subscriptions = await GetTeamSubscriptionIdsAsync(teamId);
            return subscriptions.Any(s => s.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Encontra o time de uma subscription
        /// </summary>
        public async Task<TeamConfig?> FindTeamBySubscriptionAsync(string subscriptionId)
        {
            var config = await GetConfigAsync();
            return config.Teams.FirstOrDefault(t => 
                t.SubscriptionIds.Any(s => s.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Recarrega a configuração forçando leitura do blob
        /// </summary>
        public async Task ReloadConfigAsync()
        {
            await _loadSemaphore.WaitAsync();
            try
            {
                _cachedConfig = null;
            }
            finally
            {
                _loadSemaphore.Release();
            }
            await GetConfigAsync();
        }

        /// <summary>
        /// Lista todas as subscriptions de todos os times
        /// </summary>
        public async Task<Dictionary<string, string>> GetAllSubscriptionTeamMappingsAsync()
        {
            var config = await GetConfigAsync();
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var team in config.Teams)
            {
                foreach (var subId in team.SubscriptionIds)
                {
                    mappings[subId] = team.Id;
                }
            }
            
            return mappings;
        }

        /// <summary>
        /// Obtém mapeamento de subscriptionId → subscriptionName de todos os times
        /// </summary>
        public async Task<Dictionary<string, string>> GetSubscriptionNameMappingsAsync()
        {
            var config = await GetConfigAsync();
            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var team in config.Teams)
            {
                // Mapear IDs para nomes usando índice correspondente
                for (int i = 0; i < team.SubscriptionIds.Count; i++)
                {
                    var subId = team.SubscriptionIds[i];
                    var subName = i < team.SubscriptionNames.Count 
                        ? team.SubscriptionNames[i] 
                        : subId; // Fallback para ID se não tiver nome
                    
                    if (!mappings.ContainsKey(subId))
                    {
                        mappings[subId] = subName;
                    }
                }
            }
            
            return mappings;
        }
    }
}
