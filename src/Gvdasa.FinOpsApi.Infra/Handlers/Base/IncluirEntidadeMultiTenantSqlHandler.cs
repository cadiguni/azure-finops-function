using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Infra.Repositories;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.Extensions.Logging;

public abstract class IncluirEntidadeMultiTenantSqlHandler<TEntidade, TCmd>
(
    SqlServerDataContext context,
    ILogger<IncluirEntidadeMultiTenantSqlHandler<TEntidade, TCmd>> logger
)
    : MultiTenantSqlRepository<TEntidade>(context, logger)
    , IRequestHandler<TCmd, TEntidade>
    where TEntidade : EntidadeMultiTenant
    where TCmd : IncluirEntidadeMultiTenantCommand<TEntidade>
{
    public virtual async Task<TEntidade> Handle(TCmd request, CancellationToken cancellationToken)
    {
        TEntidade registro = CriarRegistro(request);
        await Inserir(registro);
        return registro;
    }

    protected abstract TEntidade CriarRegistro(TCmd incluir);
}
