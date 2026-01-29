using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class BuscarListaEntidadeExemploSqlHandler
(
    SqlServerDataContext context,
    ILogger<BuscarListaEntidadeExemploSqlHandler> logger
)
    : BuscarListaEntidadeMultiTenantSqlHandler<EntidadeExemplo, BuscarListaEntidadeExemploCommand>(context, logger)
    , IRequestHandler<BuscarListaEntidadeExemploCommand, IEnumerable<EntidadeExemplo>>
{
    protected override DbSet<EntidadeExemplo> _dbSet => _context.EntidadesExemplo;
}
