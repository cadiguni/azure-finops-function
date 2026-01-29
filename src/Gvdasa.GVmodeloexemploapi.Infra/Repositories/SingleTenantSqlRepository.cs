using System.Linq.Expressions;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gvdasa.GVmodeloexemploapi.Infra.Repositories;

public abstract class SingleTenantSqlRepository<T>
(
    SqlServerDataContext context,
    ILogger<SingleTenantSqlRepository<T>> logger,
    DbSet<T> dbSet
) where T : Entidade
{
    private readonly SqlServerDataContext _context = context;
    private readonly ILogger<SingleTenantSqlRepository<T>> _logger = logger;
    protected readonly DbSet<T> _dbSet = dbSet;

    public virtual async Task Inserir(T entidade)
    {
        _logger.LogInformation("Inserindo entidade do tipo {tipo} no BD. Id: {id}", typeof(T).Name, entidade.Id);
        await _dbSet.AddAsync(entidade);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Entidade do tipo {tipo} inserida no BD com sucesso. Id: {id}", typeof(T).Name, entidade.Id);
    }

    public virtual async Task<IEnumerable<T>> Obter(Expression<Func<T, bool>> expression)
    {
        List<T> registros = await _dbSet
            .Where(expression)
            .ToListAsync();

        return registros;
    }

    public async Task<T?> Obter(Guid id)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.Id == id);
    }

    public virtual async Task Atualizar(T entidade)
    {
        _logger.LogInformation("Atualizando entidade do tipo {tipo} no BD. Id: {id}", typeof(T).Name, entidade.Id);
        var local = _dbSet.Local.FirstOrDefault(s => s.Id == entidade.Id);

        if(local != null)
        {
            _context.Entry(local).State = EntityState.Detached;
        }

        _context.Entry(entidade).State = EntityState.Modified;
        await _context.SaveChangesAsync();
        _logger.LogInformation("Salvando entidade do tipo {tipo} em cache. Id: {id}", typeof(T).Name, entidade.Id);
    }


    public virtual async Task Excluir(T entidade)
    {
        _logger.LogInformation("Excluindo entidade do tipo {tipo} do BD. Id: {id}", typeof(T).Name, entidade.Id);
        _dbSet.Remove(entidade);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Entidade do tipo {tipo} removida no BD com sucesso. Id: {id}", typeof(T).Name, entidade.Id);
    }
}
