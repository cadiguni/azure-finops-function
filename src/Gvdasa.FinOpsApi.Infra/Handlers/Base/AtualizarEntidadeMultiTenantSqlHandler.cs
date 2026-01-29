using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Infra.Exceptions;
using Gvdasa.GVmodeloexemploapi.Infra.Repositories;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.Extensions.Logging;

public abstract class AtualizarEntidadeMultiTenantSqlHandler<TEntidade, TCmd>
(
    SqlServerDataContext context,
    ILogger<AtualizarEntidadeMultiTenantSqlHandler<TEntidade, TCmd>> logger
)
    : MultiTenantSqlRepository<TEntidade>(context, logger)
    , IRequestHandler<TCmd, TEntidade>
    where TEntidade : EntidadeMultiTenant
    where TCmd : AtualizarEntidadeMultiTenantCommand<TEntidade>
{
    public virtual async Task<TEntidade> Handle(TCmd request, CancellationToken cancellationToken)
    {
        TEntidade? registro = await Obter(request.IdTenant, request.Id)
            ?? throw new RecursoNaoEncontradoException("Registro não encontrado");

        await AtualizarRegistro(registro, request);

        await Atualizar(registro);

        return registro;
    }

    protected abstract Task<TEntidade> AtualizarRegistro(TEntidade registro, TCmd request);
}
