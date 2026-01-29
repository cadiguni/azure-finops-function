using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class IncluirEntidadeExemploSqlHandler
(
    SqlServerDataContext context,
    ILogger<IncluirEntidadeExemploSqlHandler> logger
)
    : IncluirEntidadeMultiTenantSqlHandler<EntidadeExemplo, IncluirEntidadeExemploCommand>(context, logger)
    , IRequestHandler<IncluirEntidadeExemploCommand, EntidadeExemplo>
{
    protected override DbSet<EntidadeExemplo> _dbSet => _context.EntidadesExemplo;

    protected override EntidadeExemplo CriarRegistro(IncluirEntidadeExemploCommand command)
    {
        EntidadeExemplo registro = new()
        {
            IdTenant = command.IdTenant,
            Descricao = command.Descricao,
            Nome = command.Nome,
            Identificador = command.Identificador,
            Tipo = command.Tipo,
            DataCriacao = DateTime.UtcNow,
            IdUsuarioCriacao = command.IdUsuario
        };

        return registro;
    }

}
