using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class BuscarListaAgrupadaEntidadeExemploSqlHandler
(
    SqlServerDataContext context,
    ILogger<BuscarListaAgrupadaEntidadeExemploSqlHandler> logger
)
    : BuscarListaAgrupadaEntidadeMultiTenantSqlHandler<EntidadeExemplo, BuscarListaAgrupadaEntidadeExemploCommand>(context, logger)
    , IRequestHandler<BuscarListaAgrupadaEntidadeExemploCommand, IEnumerable<EntidadeExemplo>>
{
    protected override DbSet<EntidadeExemplo> _dbSet => _context.EntidadesExemplo;
}
