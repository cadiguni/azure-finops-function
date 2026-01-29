using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public class RemoverEntidadeExemploSqlHandler
(
    SqlServerDataContext context,
    ILogger<RemoverEntidadeExemploSqlHandler> logger
)
    : RemoverEntidadeMultiTenantSqlHandler<EntidadeExemplo, RemoverEntidadeExemploCommand>(context, logger)
    , IRequestHandler<RemoverEntidadeExemploCommand>
{
    protected override DbSet<EntidadeExemplo> _dbSet => _context.EntidadesExemplo;
}
