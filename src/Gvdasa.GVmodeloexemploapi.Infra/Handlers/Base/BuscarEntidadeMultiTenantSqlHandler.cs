using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Infra.Repositories;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.Extensions.Logging;

public abstract class BuscarEntidadeMultiTenantSqlHandler<TEntidade, TCmd>
(
    SqlServerDataContext context,
    ILogger<BuscarEntidadeMultiTenantSqlHandler<TEntidade, TCmd>> logger
)
    : MultiTenantSqlRepository<TEntidade>(context, logger)
    , IRequestHandler<TCmd, TEntidade?>
    where TEntidade : EntidadeMultiTenant
    where TCmd : BuscarEntidadeMultiTenantCommand<TEntidade>
{
    public virtual async Task<TEntidade?> Handle(TCmd request, CancellationToken cancellationToken)
    {
        return await Obter(request.IdTenant, request.Id);
    }
}
