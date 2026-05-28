using Personal.FinOpsApi.AzureFunctions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace Personal.FinOpsApi.AzureFunctions.Functions;

/// <summary>
///  Function de Summary - FREQUÊNCIA PROFISSIONAL
///  PRODUÇÃO: Roda a cada 6 horas (4x por dia)
///  DEV: Roda a cada 5 minutos para testes
/// </summary>
public class DailySummaryFunction
{
    private readonly DailySummaryService _summaryService;
    private readonly ILogger<DailySummaryFunction> _logger;

    public DailySummaryFunction(
        DailySummaryService summaryService,
        ILogger<DailySummaryFunction> logger)
    {
        _summaryService = summaryService;
        _logger = logger;
    }

    /// <summary>
    ///  PRODUÇÃO: "0 0 */6 * * *" (a cada 6 horas - 00:00, 06:00, 12:00, 18:00 UTC)
    ///  DESENVOLVIMENTO: "0 */5 * * * *" (a cada 5 minutos para testes)
    /// </summary>
    [Function("DailySummary")]
    public async Task RunAsync(
        [TimerTrigger("%DailySummarySchedule%")] TimerInfo timer, //  CONFIGURADO POR VARIÁVEL DE AMBIENTE
        FunctionContext context)
    {
        _logger.LogInformation(" DailySummaryFunction iniciada em: {time}", DateTime.UtcNow);
        
        try
        {
            // Processar dados do dia atual
            var targetDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
            
            _logger.LogInformation(" Processando dados do dia: {date}", targetDate);
            
            var summary = await _summaryService.ProcessDayAsync(targetDate);
            
            _logger.LogInformation(" Summary concluído: {resources} recursos, R$ {savings} economia potencial", 
                summary.TotalResourcesAnalyzed, 
                summary.TotalPotentialSavings);

            // Log dos principais insights
            if (summary.Top10.Any())
            {
                var topSaving = summary.Top10.First();
                _logger.LogInformation(" Maior economia: {name} ({type}) - R$ {amount}", 
                    topSaving.ResourceName, 
                    topSaving.ResourceType, 
                    topSaving.PotentialSavings);
            }

            if (summary.SummaryByType.Any())
            {
                var topType = summary.SummaryByType.OrderByDescending(kvp => kvp.Value.PotentialSavings).First();
                _logger.LogInformation(" Tipo com maior impacto: {type} - {count} recursos, R$ {amount}", 
                    topType.Key, 
                    topType.Value.Count, 
                    topType.Value.PotentialSavings);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro na execução do DailySummary");
            throw; // Deixa Functions runtime saber que falhou
        }
        
        _logger.LogInformation(" DailySummaryFunction concluída");
    }

    /// <summary>
    /// Função manual para testar agregação de uma data específica
    /// Para debug e testes
    /// </summary>
    [Function("ManualDailySummary")]
    public async Task<object> RunManualAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req,
        FunctionContext context)
    {
        _logger.LogInformation(" ManualDailySummary executada via HTTP");

        try
        {
            _logger.LogInformation(" 1. Verificando req.Query...");
            
            // Pegar data da query string ou usar hoje - com proteção null
            string dateStr;
            try 
            {
                dateStr = req.Query?["date"] ?? DateTime.UtcNow.ToString("yyyy-MM-dd");
                _logger.LogInformation(" 2. Data extraída: {date}", dateStr);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, " Erro ao extrair data da query string");
                dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
                _logger.LogInformation(" 2b. Usando data padrão: {date}", dateStr);
            }
            
            _logger.LogInformation(" Processando data: {date} (manual)", dateStr);
            
            _logger.LogInformation(" 3. Verificando _summaryService...");
            if (_summaryService == null)
            {
                _logger.LogError(" _summaryService é NULL!");
                throw new InvalidOperationException("DailySummaryService não foi injetado corretamente");
            }
            
            _logger.LogInformation(" 4. Chamando ProcessDayAsync...");
            var summary = await _summaryService.ProcessDayAsync(dateStr);
            
            _logger.LogInformation(" 5. Criando response...");
            var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await response.WriteAsJsonAsync(summary);
            
            _logger.LogInformation(" ManualDailySummary executado com sucesso");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Erro na execução manual");
            
            var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Erro interno ao executar o sumário diário.");
            return errorResponse;
        }
    }
}