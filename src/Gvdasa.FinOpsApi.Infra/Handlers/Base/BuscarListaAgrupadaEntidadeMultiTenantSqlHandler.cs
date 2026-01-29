using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Infra.Repositories;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.Extensions.Logging;

public abstract class BuscarListaAgrupadaEntidadeMultiTenantSqlHandler<TEntidade, TCmd>
(
    SqlServerDataContext context,
    ILogger<BuscarListaAgrupadaEntidadeMultiTenantSqlHandler<TEntidade, TCmd>> logger
)
    : MultiTenantSqlRepository<TEntidade>(context, logger)
    , IRequestHandler<TCmd, IEnumerable<TEntidade>>
    where TEntidade : EntidadeMultiTenant
    where TCmd : BuscarListaAgrupadaEntidadeMultiTenantCommand<TEntidade>
{
    public virtual async Task<IEnumerable<TEntidade>> Handle(TCmd request, CancellationToken cancellationToken)
    {
        return await Obter(request.IdTenant, request.GerarExpressionPesquisa());
    }
}
