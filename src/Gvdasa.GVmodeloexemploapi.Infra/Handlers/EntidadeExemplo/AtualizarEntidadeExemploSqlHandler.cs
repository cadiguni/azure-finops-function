using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class AtualizarEntidadeExemploSqlHandler
(
    SqlServerDataContext context,
    ILogger<AtualizarEntidadeExemploSqlHandler> logger
)
    : AtualizarEntidadeMultiTenantSqlHandler<EntidadeExemplo, AtualizarEntidadeExemploCommand>(context, logger)
    , IRequestHandler<AtualizarEntidadeExemploCommand, EntidadeExemplo>
{
    protected override DbSet<EntidadeExemplo> _dbSet => _context.EntidadesExemplo;

    protected override Task<EntidadeExemplo> AtualizarRegistro(EntidadeExemplo registro, AtualizarEntidadeExemploCommand command)
    {
        registro.Descricao = command.Descricao;
        registro.Nome = command.Nome;
        registro.Tipo = command.Tipo;

        return Task.FromResult(registro);
    }

}
