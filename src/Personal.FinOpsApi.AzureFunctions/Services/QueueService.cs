using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Personal.FinOpsApi.AzureFunctions.Services;

/// <summary>
///  Queue Service - Abstração para Service Bus Queue processing
///  HÍBRIDO: Permite usar queues OU execução direta via feature flag
/// </summary>
public class QueueService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<QueueService> _logger;
    private readonly bool _queueProcessingEnabled;

    //  Queue names from configuration
    private readonly string _subscriptionAnalysisQueue;
    private readonly string _subscriptionAnalysisProductionQueue;  //  NOVA: Queue exclusiva para produção
    private readonly string _storageAnalysisQueue;
    private readonly string _vmAnalysisQueue;
    private readonly string _appServiceAnalysisQueue;
    private readonly string _resultsQueue;
    private readonly string _costByResourceQueue;
    private readonly string _costByResourceStarterQueue;
    
    //  CONFIG PARA PRODUÇÃO
    private readonly string _productionSubscriptionId;

    public QueueService(
        ServiceBusClient serviceBusClient,
        IConfiguration configuration,
        ILogger<QueueService> logger)
    {
        _serviceBusClient = serviceBusClient;
        _configuration = configuration;
        _logger = logger;
        
        //  Feature flag - permite desabilitar queues
        _queueProcessingEnabled = _configuration.GetValue<bool>("ENABLE_QUEUE_PROCESSING", false);
        
        //  Queue names from app settings
        _subscriptionAnalysisQueue = _configuration["QUEUE_SUBSCRIPTION_ANALYSIS"] ?? "subscription-analysis";
        _subscriptionAnalysisProductionQueue = _configuration["QUEUE_SUBSCRIPTION_ANALYSIS_PROD"] ?? "subscription-analysis-production";
        _storageAnalysisQueue = _configuration["QUEUE_STORAGE_ANALYSIS"] ?? "storage-analysis";
        _vmAnalysisQueue = _configuration["QUEUE_VM_ANALYSIS"] ?? "vm-analysis";
        _appServiceAnalysisQueue = _configuration["QUEUE_APPSERVICE_ANALYSIS"] ?? "appservice-analysis";
        _resultsQueue = _configuration["QUEUE_ANALYSIS_RESULTS"] ?? "analysis-results";
        _costByResourceQueue = _configuration["QUEUE_COST_BY_RESOURCE"] ?? "cost-by-resource-analysis";
        _costByResourceStarterQueue = _configuration["QUEUE_COST_BY_RESOURCE_STARTER"] ?? "cost-by-resource-starter";
        
        //  CONFIGURAÇÃO DE PRODUÇÃO
        _productionSubscriptionId = _configuration["PRODUCTION_SUBSCRIPTION_ID"] ?? "504a622c-3995-46c5-8ba7-8edb365ed17b";
        
        _logger.LogInformation(" QueueService iniciado - Queue processing: {enabled}, Production Sub: {prodSub}", 
            _queueProcessingEnabled, _productionSubscriptionId);
    }

    /// <summary>
    ///  Verifica se o processamento por queue está habilitado
    /// </summary>
    public bool IsQueueProcessingEnabled => _queueProcessingEnabled;
    public string CostByResourceQueueName => _costByResourceQueue;
    public string CostByResourceStarterQueueName => _costByResourceStarterQueue;

    /// <summary>
    ///  Envia subscription para análise via queue
    ///  INTELIGÊNCIA: Detecta subscription de produção e roteia para queue otimizada
    /// </summary>
    public async Task<bool> SendSubscriptionAnalysisAsync(string subscriptionId, string analysisType = "complete")
    {
        if (!_queueProcessingEnabled)
        {
            _logger.LogInformation(" Queue processing desabilitado - pulando envio para queue");
            return false;
        }

        try
        {
            //  DETECÇÃO: Subscription de produção vai para queue exclusiva
            var isProductionSubscription = subscriptionId.Equals(_productionSubscriptionId, StringComparison.OrdinalIgnoreCase);
            var isCompleteAnalysis = string.Equals(analysisType, "complete", StringComparison.OrdinalIgnoreCase);
            var useProductionQueue = isProductionSubscription && isCompleteAnalysis;
            var targetQueue = useProductionQueue ? _subscriptionAnalysisProductionQueue : _subscriptionAnalysisQueue;
            
            var message = new
            {
                SubscriptionId = subscriptionId,
                AnalysisType = analysisType,
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString(),
                IsProduction = useProductionQueue  //  FLAG para identificação
            };

            var jsonMessage = JsonSerializer.Serialize(message);
            var serviceBusMessage = new ServiceBusMessage(jsonMessage)
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = $"subscription-analysis-{analysisType}"
            };

            var sender = _serviceBusClient.CreateSender(targetQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            var queueType = useProductionQueue ? " PRODUÇÃO" : " NORMAL";
            _logger.LogInformation(" Subscription {subscriptionId} enviada para queue {queueType} ({queue}) - tipo: {analysisType}", 
                subscriptionId, queueType, targetQueue, analysisType);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao enviar subscription {subscriptionId} para queue", subscriptionId);
            return false;
        }
    }

    /// <summary>
    ///  Envia análise de Storage Account para queue especializada
    /// </summary>
    public async Task<bool> SendStorageAnalysisAsync(string subscriptionId, List<string> storageAccountIds)
    {
        if (!_queueProcessingEnabled)
        {
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                StorageAccounts = storageAccountIds,
                AnalysisType = "storage",
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var jsonMessage = JsonSerializer.Serialize(message);
            var serviceBusMessage = new ServiceBusMessage(jsonMessage)
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = "storage-analysis"
            };

            var sender = _serviceBusClient.CreateSender(_storageAnalysisQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation(" Storage analysis para {subscriptionId} enviada - {count} storage accounts", subscriptionId, storageAccountIds.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao enviar storage analysis para queue");
            return false;
        }
    }

    /// <summary>
    ///  Envia análise de VMs para queue especializada
    /// </summary>
    public async Task<bool> SendVmAnalysisAsync(string subscriptionId, List<string> vmIds)
    {
        if (!_queueProcessingEnabled)
        {
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                VmIds = vmIds,
                AnalysisType = "vm",
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var jsonMessage = JsonSerializer.Serialize(message);
            var serviceBusMessage = new ServiceBusMessage(jsonMessage)
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = "vm-analysis"
            };

            var sender = _serviceBusClient.CreateSender(_vmAnalysisQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation(" VM analysis para {subscriptionId} enviada - {count} VMs", subscriptionId, vmIds.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao enviar VM analysis para queue");
            return false;
        }
    }

    /// <summary>
    ///  Envia análise de App Services para queue
    /// </summary>
    public async Task<bool> SendAppServiceAnalysisAsync(string subscriptionId, List<string> appServiceIds)
    {
        if (!_queueProcessingEnabled)
        {
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                AppServices = appServiceIds,
                AnalysisType = "appservice",
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var jsonMessage = JsonSerializer.Serialize(message);
            var serviceBusMessage = new ServiceBusMessage(jsonMessage)
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = "appservice-analysis"
            };

            var sender = _serviceBusClient.CreateSender(_appServiceAnalysisQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation(" App Service analysis para {subscriptionId} enviada - {count} App Services", subscriptionId, appServiceIds.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao enviar App Service analysis para queue");
            return false;
        }
    }

    /// <summary>
    ///  Envia resultados consolidados para queue
    /// </summary>
    public async Task<bool> SendAnalysisResultsAsync(object analysisResults, string analysisType, string subscriptionId)
    {
        if (!_queueProcessingEnabled)
        {
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                AnalysisType = analysisType,
                AnalysisResult = analysisResults,
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var jsonMessage = JsonSerializer.Serialize(message);
            var serviceBusMessage = new ServiceBusMessage(jsonMessage)
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = $"results-{analysisType}"
            };

            var sender = _serviceBusClient.CreateSender(_resultsQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation(" Resultados de {analysisType} enviados para consolidação", analysisType);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao enviar resultados para queue");
            return false;
        }
    }

    /// <summary>
    ///  Processa múltiplas subscriptions via queues
    /// </summary>
    public async Task<int> SendBulkSubscriptionAnalysisAsync(List<string> subscriptionIds, string analysisType = "complete")
    {
        if (!_queueProcessingEnabled)
        {
            _logger.LogInformation(" Queue processing desabilitado - pulando envio bulk");
            return 0;
        }

        var successCount = 0;
        
        _logger.LogInformation(" Enviando {count} subscriptions para processamento paralelo via queues", subscriptionIds.Count);

        var tasks = subscriptionIds.Select(async subscriptionId =>
        {
            var success = await SendSubscriptionAnalysisAsync(subscriptionId, analysisType);
            if (success) Interlocked.Increment(ref successCount);
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation(" Enviadas {success}/{total} subscriptions para queues", successCount, subscriptionIds.Count);
        return successCount;
    }

    /// <summary>
    ///  Envia mensagem agendada (para retry com delay após rate limit)
    /// </summary>
    public async Task SendScheduledMessageAsync(
        string queueName, 
        string messageBody, 
        DateTimeOffset scheduledEnqueueTime,
        Dictionary<string, object>? applicationProperties = null)
    {
        try
        {
            var sender = _serviceBusClient.CreateSender(queueName);
            var message = new ServiceBusMessage(messageBody)
            {
                ScheduledEnqueueTime = scheduledEnqueueTime
            };

            //  Adicionar propriedades customizadas
            if (applicationProperties != null)
            {
                foreach (var prop in applicationProperties)
                {
                    message.ApplicationProperties[prop.Key] = prop.Value;
                }
            }

            await sender.SendMessageAsync(message);
            
            _logger.LogInformation(" Mensagem agendada enviada para {queue} - agendada para {time}", 
                queueName, scheduledEnqueueTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao enviar mensagem agendada para {queue}: {error}", 
                queueName, ex.Message);
            throw;
        }
    }

    /// <summary>
    ///  Reagenda mensagem para execução futura com propriedades customizadas
    /// </summary>
    public async Task ScheduleMessageAsync(
        string queueName, 
        string messageBody, 
        DateTimeOffset scheduledEnqueueTime,
        IDictionary<string, object>? properties = null)
    {
        try
        {
            var sender = _serviceBusClient.CreateSender(queueName);
            
            var message = new ServiceBusMessage(messageBody)
            {
                ScheduledEnqueueTime = scheduledEnqueueTime,
                MessageId = Guid.NewGuid().ToString()
            };

            // Adicionar propriedades customizadas
            if (properties != null)
            {
                foreach (var prop in properties)
                {
                    message.ApplicationProperties[prop.Key] = prop.Value;
                }
            }

            await sender.SendMessageAsync(message);
            
            _logger.LogInformation(" [QUEUE] Mensagem reagendada para {queueName} em {scheduledTime}", 
                queueName, scheduledEnqueueTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [QUEUE] Erro ao reagendar mensagem para {queueName}", queueName);
            throw;
        }
    }

    /// <summary>
    ///  ENVIA ESPECIFICAMENTE PARA FILA DE PRODUÇÃO - Subscription grande
    /// </summary>
    public async Task<bool> SendToProductionQueueAsync(string subscriptionId, string analysisType = "complete")
    {
        if (!_queueProcessingEnabled)
        {
            _logger.LogInformation(" Queue processing desabilitado - pulando envio para produção");
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                AnalysisType = analysisType,
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString(),
                IsProduction = true,  //  FLAG específica para produção
                QueueType = "PRODUCTION_DEDICATED" //  Identificação clara
            };

            var jsonMessage = JsonSerializer.Serialize(message);
            var serviceBusMessage = new ServiceBusMessage(jsonMessage)
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = $"production-analysis-{analysisType}",
                //  Propriedades específicas para produção
                ApplicationProperties =
                {
                    ["Priority"] = "High",
                    ["SubscriptionType"] = "Production",
                    ["ExpectedDuration"] = "30-60min"
                }
            };

            var sender = _serviceBusClient.CreateSender(_subscriptionAnalysisProductionQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation(" [PRODUCTION-QUEUE] Subscription {subscriptionId} enviada para fila dedicada ({analysisType})", 
                subscriptionId, analysisType);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [PRODUCTION-QUEUE] Erro ao enviar subscription {subscriptionId} para fila de produção", subscriptionId);
            return false;
        }
    }

    /// <summary>
    ///  STEPS: Envia mensagem de step para processamento em etapas
    /// Solução para timeouts do Consumption Plan
    /// </summary>
    public async Task SendStepMessageAsync(object stepMessage)
    {
        try
        {
            var json = JsonSerializer.Serialize(stepMessage);
            var message = new ServiceBusMessage(json)
            {
                MessageId = $"step-{Guid.NewGuid()}",
                Subject = "Analysis Step Processing",
                TimeToLive = TimeSpan.FromDays(1)
            };

            // Envia para fila de steps (será criada no Terraform)
            var stepsSender = _serviceBusClient.CreateSender("subscription-analysis-steps");
            await stepsSender.SendMessageAsync(message);
            await stepsSender.DisposeAsync();
            
            _logger.LogInformation(" [STEPS] Mensagem de step enviada: {step}", stepMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEPS] Erro ao enviar step: {error}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Envia solicitação de custo por recurso para fila dedicada.
    /// </summary>
    public async Task<bool> SendCostByResourceAnalysisAsync(
        string subscriptionId,
        DateTime dateUtc,
        string? serviceFilter,
        int retryCount = 0)
    {
        if (!_queueProcessingEnabled)
        {
            _logger.LogInformation(" Queue processing desabilitado - pulando envio cost-by-resource");
            return false;
        }

        try
        {
            var message = new
            {
                SubscriptionId = subscriptionId,
                Date = dateUtc.ToString("yyyy-MM-dd"),
                Service = serviceFilter,
                RetryCount = retryCount,
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var jsonMessage = JsonSerializer.Serialize(message);
            var serviceBusMessage = new ServiceBusMessage(jsonMessage)
            {
                MessageId = Guid.NewGuid().ToString(),
                Subject = "cost-by-resource-analysis"
            };
            serviceBusMessage.ApplicationProperties["retryCount"] = retryCount;

            var sender = _serviceBusClient.CreateSender(_costByResourceQueue);
            await sender.SendMessageAsync(serviceBusMessage);
            await sender.DisposeAsync();

            _logger.LogInformation(
                " CostByResource enviado para queue {queue} | sub={subscriptionId} date={date} retry={retry}",
                _costByResourceQueue,
                subscriptionId,
                dateUtc.ToString("yyyy-MM-dd"),
                retryCount);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao enviar CostByResource para queue");
            return false;
        }
    }

    /// <summary>
    /// Enfileira em lote as solicitações de custo por recurso usando um único sender.
    /// </summary>
    public async Task<(int Enqueued, int Failed)> SendBulkCostByResourceAnalysisAsync(
        IReadOnlyCollection<string> subscriptionIds,
        DateTime dateUtc,
        string? serviceFilter)
    {
        if (!_queueProcessingEnabled)
        {
            _logger.LogInformation(" Queue processing desabilitado - pulando envio em lote cost-by-resource");
            return (0, subscriptionIds.Count);
        }

        var enqueued = 0;
        var failed = 0;

        try
        {
            var sender = _serviceBusClient.CreateSender(_costByResourceQueue);
            try
            {
                foreach (var subscriptionId in subscriptionIds)
                {
                    try
                    {
                        var message = new
                        {
                            SubscriptionId = subscriptionId,
                            Date = dateUtc.ToString("yyyy-MM-dd"),
                            Service = serviceFilter,
                            RetryCount = 0,
                            Timestamp = DateTime.UtcNow,
                            RequestId = Guid.NewGuid().ToString()
                        };

                        var jsonMessage = JsonSerializer.Serialize(message);
                        var serviceBusMessage = new ServiceBusMessage(jsonMessage)
                        {
                            MessageId = Guid.NewGuid().ToString(),
                            Subject = "cost-by-resource-analysis"
                        };
                        serviceBusMessage.ApplicationProperties["retryCount"] = 0;

                        await sender.SendMessageAsync(serviceBusMessage);
                        enqueued++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogError(ex, " Erro ao enfileirar sub {subscriptionId} em lote", subscriptionId);
                    }
                }
            }
            finally
            {
                await sender.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao inicializar sender para envio em lote cost-by-resource");
            failed += subscriptionIds.Count - enqueued;
        }

        _logger.LogInformation(
            " Envio em lote cost-by-resource concluído. Enqueued={enqueued}, Failed={failed}",
            enqueued,
            failed);

        return (enqueued, failed);
    }

    /// <summary>
    /// Envia mensagem starter para iniciar processamento de custo por recurso.
    /// </summary>
    public async Task<bool> SendCostByResourceStarterAsync(
        DateTime dateUtc,
        string? serviceFilter,
        string subscriptionFilter = "all")
    {
        if (!_queueProcessingEnabled)
        {
            _logger.LogInformation(" Queue processing desabilitado - pulando starter cost-by-resource");
            return false;
        }

        try
        {
            var message = new
            {
                Date = dateUtc.ToString("yyyy-MM-dd"),
                Service = serviceFilter,
                SubscriptionFilter = subscriptionFilter,
                Timestamp = DateTime.UtcNow,
                RequestId = Guid.NewGuid().ToString()
            };

            var sender = _serviceBusClient.CreateSender(_costByResourceStarterQueue);
            try
            {
                await sender.SendMessageAsync(new ServiceBusMessage(JsonSerializer.Serialize(message))
                {
                    MessageId = Guid.NewGuid().ToString(),
                    Subject = "cost-by-resource-starter"
                });
            }
            finally
            {
                await sender.DisposeAsync();
            }

            _logger.LogInformation(
                "Starter CostByResource enviado para queue {queue} | date={date} filter={filter}",
                _costByResourceStarterQueue,
                dateUtc.ToString("yyyy-MM-dd"),
                subscriptionFilter);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro ao enviar starter CostByResource para queue");
            return false;
        }
    }
}
