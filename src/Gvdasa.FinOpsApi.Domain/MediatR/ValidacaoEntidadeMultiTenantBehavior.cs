using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Domain.MediatR;

[ExcludeFromCodeCoverage]
public class ValidacaoRegrasDeNegocioBehavior<TRequest, TResponse>(IRegraDeNegocioResolver regraDeNegocioResolver)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IEscritaMultiTenantCommand<TResponse>, IRequest<TResponse>
    where TResponse : EntidadeMultiTenant
{
    private readonly IRegraDeNegocioResolver _resolver = regraDeNegocioResolver;

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // Resolve a regra de negócio com base no tipo de entidade (TResponse)
        var regraDeNegocio = _resolver.ObterRegra<TResponse>();

        // Valida as regras de negócio
        await regraDeNegocio.ValidarEscrita(request);

        // Continua para o próximo passo no pipeline (handler)
        return await next();
    }
}
