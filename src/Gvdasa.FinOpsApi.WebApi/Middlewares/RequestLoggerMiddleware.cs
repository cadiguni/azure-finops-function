using Microsoft.IO;
using Gvdasa.GVmodeloexemploapi.Infra.Services;
using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.WebApi.Extensions;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Middlewares;

[ExcludeFromCodeCoverage]
public class RequestLoggerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RecyclableMemoryStreamManager _recyclableMemoryStreamManager;
    private const string _loggerMessageTemplate = "Request Logger - {Mensagem}";

    public RequestLoggerMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _recyclableMemoryStreamManager = new RecyclableMemoryStreamManager();
    }

    public async Task InvokeAsync(HttpContext context, ILogService logService)
    {
        var idTenant = context.Request.Headers.GetValueOrDefault("IdTenant");
        var idCorrelacao = context.Request.Headers.GetValueOrDefault("IdCorrelacao");

        await logService.InvokeWithCustomPropertiesAsync(
            function: async () =>
            {
                await LogRequestAsync(context, logService);
                await LogResponseAsync(context, logService);
            },
            customProperties: new Dictionary<string, object>()
            {
                    { "IdTenant", idTenant ?? "n/a" },
                    { "IdCorrelacao", idCorrelacao ?? "n/a" }
            }
        );
    }

    private async Task LogRequestAsync(HttpContext context, ILogService logService)
    {
        context.Request.EnableBuffering();

        using (var requestStream = _recyclableMemoryStreamManager.GetStream())
        {
            try
            {
                logService.Logger.LogInformation(_loggerMessageTemplate, "Leitura do corpo da requisição iniciada.");

                await context.Request.Body.CopyToAsync(requestStream);
                context.Request.Body.Position = 0;

                logService.EnrichDiagnosticContext("RequestBody", await requestStream.ReadStreamInChunksAsync());
            }
            catch (Exception e)
            {
                logService.EnrichDiagnosticContext("RequestBody",
                    "Falha na leitura do corpo da requisição. " +
                    "Verifique os logs de erro da requisição para mais informações."
                );
                logService.Logger.LogError(e, _loggerMessageTemplate, "Falha na leitura do corpo da requisição");
            }
            finally
            {
                logService.Logger.LogInformation(_loggerMessageTemplate, "Leitura do corpo da requisição finalizada.");
            }
        }
    }

    private async Task LogResponseAsync(HttpContext context, ILogService logService)
    {
        var originalBody = context.Response.Body;

        using (var responseStream = _recyclableMemoryStreamManager.GetStream())
        {
            context.Response.Body = responseStream;

            try
            {
                logService.Logger.LogInformation(_loggerMessageTemplate, "Execução do endpoint iniciada.");
                logService.EnrichDiagnosticContext("DateTimeRequestStarted", DateTime.UtcNow.ToString("o"));

                await _next.Invoke(context);
            }
            catch (Exception e)
            {
                logService.EnrichDiagnosticContext("ResponseBody",
                    "Erro interno na execução do endpoint. " +
                    "Verifique os logs de erro da requisição para mais informações."
                );
                logService.Logger.LogError(e, _loggerMessageTemplate, "Erro interno na execução do endpoint");

                context.Response.Body = originalBody;

                throw;
            }
            finally
            {
                logService.EnrichDiagnosticContext("DateTimeRequestFinished", DateTime.UtcNow.ToString("o"));
                logService.Logger.LogInformation(_loggerMessageTemplate, "Execução do endpoint finalizada.");
            }

            try
            {
                logService.Logger.LogInformation(_loggerMessageTemplate, "Leitura do corpo da resposta iniciada.");

                responseStream.Position = 0;
                await responseStream.CopyToAsync(originalBody);

                logService.EnrichDiagnosticContext("ResponseBody", await responseStream.ReadStreamInChunksAsync());
            }
            catch (Exception e)
            {
                logService.EnrichDiagnosticContext("ResponseBody",
                    "Falha na leitura do corpo da resposta. " +
                    "Verifique os logs de erro da requisição para mais informações."
                );
                logService.Logger.LogError(e, _loggerMessageTemplate, "Falha na leitura do corpo da resposta");
            }
            finally
            {
                logService.Logger.LogInformation(_loggerMessageTemplate, "Leitura do corpo da resposta finalizada.");

                context.Response.Body = originalBody;
            }
        }
    }
}
