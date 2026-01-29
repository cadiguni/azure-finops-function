using System.Diagnostics.CodeAnalysis;
using Flurl;
using Flurl.Http;
using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Config;
using Gvdasa.GVmodeloexemploapi.Infra.Exceptions;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gvdasa.GVmodeloexemploapi.Infra.Handlers;

[ExcludeFromCodeCoverage]
public class ArquivosHandler
(
    IMediator mediator,
    ILogger<ArquivosHandler> logger,
    IOptions<ArquivosConfig> arquivosOptions,
    IOptions<ClientConfig> clientOptions
)
    : IRequestHandler<UploadDeArquivoCommand, Guid>
    , IRequestHandler<BuscarArquivoCommand, Stream>
    , IRequestHandler<RemoverArquivoCommand>
    , IRequestHandler<BuscarInfoArquivoCommand, InfoArquivo>
{
    private readonly ILogger<ArquivosHandler> _logger = logger;
    private readonly string _arquivosUrl = arquivosOptions.Value.Url;
    private readonly string _clientId = clientOptions.Value.ClientId;
    private readonly IMediator _mediator = mediator;

    public async Task<Guid> Handle(UploadDeArquivoCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Iniciando tentativa de upload de arquivo {nome} no serviço de arquivos", request.NomeArquivoComExtensao);

        string token = await ObterToken(request.IdTenant);

        var resposta = await _arquivosUrl
            .AppendPathSegment("api/v1/Arquivo/Privado")
            .WithHeader("IdTenant", request.IdTenant)
            .WithHeader("IdClient", _clientId)
            .WithCookie("jwt", token)
            .WithTimeout(TimeSpan.FromSeconds(10))
            .PostMultipartAsync(mp =>
            {
                mp.AddFile("arquivo", request.ConteudoArquivo, request.NomeArquivoComExtensao, request.ContentType);
                mp.AddString("classificacao", "arquivo de secretaria digital");
            }, cancellationToken: cancellationToken)
            .ReceiveJson<RespostaInclusaoServicoArquivos>();

        return resposta.IdArquivo;
    }

    public async Task<Stream> Handle(BuscarArquivoCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Iniciando tentativa de obter arquivo {id} do serviço de arquivos", request.IdArquivo);

        string token = await ObterToken(request.IdTenant);
        try
        {
            return await _arquivosUrl
                .AppendPathSegment("api/v1/Arquivo/Privado")
                .AppendPathSegment(request.IdArquivo)
                .WithHeader("IdTenant", request.IdTenant)
                .WithHeader("IdClient", _clientId)
                .WithCookie("jwt", token)
                .WithTimeout(TimeSpan.FromSeconds(20))
                .GetStreamAsync(cancellationToken: cancellationToken);
        }
        catch(FlurlHttpException e)
        {
            _logger.LogError(e, "Arquivo privado {id} não encontrado. Status code: {statusCode}. ResponseMessage: {msg}",
                request.IdArquivo,
                e.StatusCode,
                e.Call.Response?.ResponseMessage);
            if (e.StatusCode == 404) throw new RecursoNaoEncontradoException("Arquivo não encontrado");
            else throw;
        }
    }

    public async Task Handle(RemoverArquivoCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Iniciando tentativa de remover arquivo {id} no serviço de arquivos", request.IdArquivo);

        string token = await ObterToken(request.IdTenant);

        await _arquivosUrl
            .AppendPathSegment("api/v1/Arquivo")
            .AppendPathSegment(request.IdArquivo)
            .WithHeader("IdTenant", request.IdTenant)
            .WithHeader("IdClient", _clientId)
            .WithCookie("jwt", token)
            .WithTimeout(TimeSpan.FromSeconds(10))
            .DeleteAsync(cancellationToken: cancellationToken);
    }

    public async Task<InfoArquivo> Handle(BuscarInfoArquivoCommand request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Iniciando tentativa de obter metadados do arquivo {id} do serviço de arquivos", request.IdArquivo);

        string token = await ObterToken(request.IdTenant);

        InfoArquivo[] infoArquivos;

        try
        {
            infoArquivos = await _arquivosUrl
                .AppendPathSegment("api/v1/Arquivo/metadados")
                .WithHeader("IdTenant", request.IdTenant)
                .WithHeader("IdClient", _clientId)
                .WithCookie("jwt", token)
                .WithTimeout(TimeSpan.FromSeconds(10))
                .PostJsonAsync(new string[]{request.IdArquivo.ToString()}, cancellationToken: cancellationToken)
                .ReceiveJson<InfoArquivo[]>();
        }
        catch(FlurlHttpException e)
        {
            _logger.LogError(e, "Arquivo privado {id} não encontrado. Status code: {statusCode}. ResponseMessage: {msg}",
                request.IdArquivo,
                e.StatusCode,
                e.Call.Response?.ResponseMessage);
            if (e.StatusCode == 404) throw new RecursoNaoEncontradoException("Arquivo não encontrado");
            else throw;
        }

        return infoArquivos.First();
    }

    public async Task<string> ObterToken(string idTenant)
    {
        ObterTokenCommand command = new()
        {
            IdTenant = idTenant
        };

        return await _mediator.Send(command);
    }
}

public record RespostaInclusaoServicoArquivos(Guid IdArquivo);
