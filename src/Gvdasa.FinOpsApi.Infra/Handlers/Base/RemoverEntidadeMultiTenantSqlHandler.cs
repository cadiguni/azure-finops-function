using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Infra.Exceptions;
using Gvdasa.GVmodeloexemploapi.Infra.Repositories;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.Extensions.Logging;

public abstract class RemoverEntidadeMultiTenantSqlHandler<TEntidade, TCmd>
(
    SqlServerDataContext context,
    ILogger<RemoverEntidadeMultiTenantSqlHandler<TEntidade, TCmd>> logger
)
    : MultiTenantSqlRepository<TEntidade>(context, logger)
    , IRequestHandler<TCmd>
    where TEntidade : EntidadeMultiTenant
    where TCmd : RemoverEntidadeMultiTenantCommand
{
    public virtual async Task Handle(TCmd request, CancellationToken cancellationToken)
    {
        TEntidade? registro = await Obter(request.IdTenant, request.Id);

        if (registro is null)
        {
            throw new RecursoNaoEncontradoException("Registro não encontrado");
        }

        await Excluir(registro);
    }
}
