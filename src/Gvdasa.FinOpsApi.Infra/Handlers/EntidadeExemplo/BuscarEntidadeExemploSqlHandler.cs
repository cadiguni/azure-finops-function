using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class BuscarEntidadeExemploSqlHandler
(
    SqlServerDataContext context,
    ILogger<BuscarEntidadeExemploSqlHandler> logger
)
    : BuscarEntidadeMultiTenantSqlHandler<EntidadeExemplo, BuscarEntidadeExemploCommand>(context, logger)
    , IRequestHandler<BuscarEntidadeExemploCommand, EntidadeExemplo?>
{
    protected override DbSet<EntidadeExemplo> _dbSet => _context.EntidadesExemplo;
}
