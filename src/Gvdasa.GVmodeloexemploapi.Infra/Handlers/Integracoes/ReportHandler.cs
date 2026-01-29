using System.Diagnostics.CodeAnalysis;
using System.Text;
using Flurl;
using Flurl.Http;
using Gvdasa.GVmodeloexemploapi.Infra.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class ReportHandler
(
    IOptions<ReportConfig> options,
    ILogger<ReportHandler> logger
)
: IRequestHandler<GerarReportCommand, string>
{
    private readonly ReportConfig _config = options.Value;
    private readonly ILogger<ReportHandler> _logger = logger;

    public async Task<string> Handle(GerarReportCommand request, CancellationToken cancellationToken)
    {
        return await ObterHtml(_config.TemplateNotificacaoAcesso, request.Dados);
    }

    private async Task<string> ObterHtml(string nomeTemplate, object dados)
    {
        var template = new TemplateDto(nomeTemplate, "html");
        var req = new RequisicaoBodyDto(template, dados);

        var resultado = await Obter(req);
        using var reader = new StreamReader(resultado, Encoding.UTF8);

        string html = await reader.ReadToEndAsync();
        return html;
    }

    private async Task<Stream> Obter(RequisicaoBodyDto requisicao)
    {
        return await _config.Url
            .AppendPathSegment("/api/report")
            .WithTimeout(TimeSpan.FromSeconds(10))
            .WithHeader("Authorization", "Basic Z3ZzaWduX3VzZXI6aE5KOFp0MUdOSg==")
            .PostJsonAsync(requisicao)
            .ReceiveStream();
    }

    record RequisicaoBodyDto(TemplateDto template, object data);
    record TemplateDto(string name, string recipe);
}
