using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Personal.FinOpsApi.AzureFunctions.Services;
using Personal.FinOpsApi.AzureFunctions.Functions;
using Azure.Messaging.ServiceBus;
using System.Text.Json;
using Personal.FinOpsApi.AzureFunctions.Models;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
///  STARTER: Inicia processamento em etapas para subscriptions grandes
/// 
/// Substitui a SubscriptionAnalysisProductionQueueFunction quando timeout é problema
/// Envia mensagem "orchestrate" que quebra a análise em steps de 2-5 minutos
/// 
/// Flow:
/// 1. Recebe subscription na fila de produção
/// 2. Cria analysisId único para o dia
/// 3. Envia mensagem "orchestrate" para SubscriptionAnalysisStepFunction
/// 4. Steps individuais rodam sem timeout
/// </summary>
public class SubscriptionAnalysisStepStarterFunction
{
    private readonly ILogger<SubscriptionAnalysisStepStarterFunction> _logger;
    private readonly QueueService _queueService;

    public SubscriptionAnalysisStepStarterFunction(
        ILogger<SubscriptionAnalysisStepStarterFunction> logger,
        QueueService queueService)
    {
        _logger = logger;
        _queueService = queueService;
    }

    [Function("SubscriptionAnalysisStepStarter")]
    public async Task Run(
        [ServiceBusTrigger("subscription-analysis-production-starter", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message)
    {
        var request = ParseRequest(message.Body.ToString());
        if (request == null || string.IsNullOrWhiteSpace(request.SubscriptionId))
        {
            _logger.LogError(" [STEP-STARTER] Mensagem inválida recebida: {body}", message.Body.ToString());
            return;
        }

        var subscriptionId = request.SubscriptionId.Trim();

        _logger.LogInformation(" [STEP-STARTER] Iniciando processamento em etapas para subscription: {subscriptionId}", 
            subscriptionId);

        try
        {
            // Cria analysisId único para o dia (idempotência)
            var analysisId = $"{subscriptionId}-{DateTime.UtcNow:yyyy-MM-dd}";

            // Cria mensagem de orchestração
            var orchestrateMessage = new AnalysisStepMessage
            {
                AnalysisId = analysisId,
                SubscriptionId = subscriptionId,
                Step = "orchestrate",
                CreatedAt = DateTime.UtcNow
            };

            // Envia para processamento em etapas
            await _queueService.SendStepMessageAsync(orchestrateMessage);

            _logger.LogInformation(" [STEP-STARTER] Processamento em etapas iniciado: {analysisId} para {subscriptionId}", 
                analysisId, subscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " [STEP-STARTER] Erro ao iniciar processamento em etapas para {subscriptionId}: {error}", 
                subscriptionId, ex.Message);
            throw; // Rejeita mensagem para retry
        }
    }

    private static SubscriptionAnalysisRequest? ParseRequest(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize<SubscriptionAnalysisRequest>(body);
            if (request != null && !string.IsNullOrWhiteSpace(request.SubscriptionId))
            {
                return request;
            }
        }
        catch
        {
            // fallback abaixo
        }

        // Fallback para compatibilidade: se vier só o id em texto puro.
        return new SubscriptionAnalysisRequest
        {
            SubscriptionId = body.Trim(),
            AnalysisType = "complete",
            Timestamp = DateTime.UtcNow,
            RequestId = Guid.NewGuid().ToString()
        };
    }
}
