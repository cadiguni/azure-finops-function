using System.Diagnostics.CodeAnalysis;
using Flurl;
using Flurl.Http;
using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Config;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gvdasa.GVmodeloexemploapi.Infra.Handlers;

[ExcludeFromCodeCoverage]
public class EnviarEmailHandler
(
    IMediator mediator,
    IOptions<EmailConfig> options,
    ILogger<EnviarEmailHandler> logger
)
    : IRequestHandler<EnviarEmailCommand, Guid>
{
    private readonly IMediator _mediator = mediator;
    private readonly ILogger<EnviarEmailHandler> _logger = logger;
    private readonly string _urlServicoEmail = options.Value.Url;

    public async Task<Guid> Handle(EnviarEmailCommand request, CancellationToken cancellationToken)
    {
        ObterTokenCommand tokenCommand = new()
        {
            IdTenant = request.IdTenant
        };

        string token = await _mediator.Send(tokenCommand);

        MultipartFormDataContent formData = new()
        {
            { new StringContent("GVmodeloexemploapi"), "IdProduto" },
            { new StringContent(request.Assunto), "Assunto" },
            { new StringContent(request.Corpo), "CorpoEmail" },
            { new StringContent(request.CorpoHtml.ToString()), "CorpoHtml" },
            { new StringContent("Normal"), "Prioridade" }
        };

        for (int i = 0; i < request.Destinatarios.Count(); i++)
        {
            Destinatario destinatario = request.Destinatarios.ElementAt(i);
            formData.Add(new StringContent(destinatario.EnderecoEmail), $"Destinatarios[{i}][enderecoEmail]");
            formData.Add(new StringContent("Normal"), $"Destinatarios[{i}][tipoDeEnvio]");

            if (!string.IsNullOrWhiteSpace(destinatario.Nome))
            {
                formData.Add(new StringContent(destinatario.Nome), $"Destinatarios[{i}][nome]");
            }
        }

        ValorDto registro = await _urlServicoEmail
            .AppendPathSegment("api/v1/Email")
            .WithHeader("IdTenant", request.IdTenant)
            .WithCookie("jwt", token)
            .WithTimeout(TimeSpan.FromSeconds(10))
            .PostAsync(formData, cancellationToken: cancellationToken)
            .ReceiveJson<ValorDto>();

        return registro.Valor;
    }

    public record ValorDto(Guid Valor);
}
