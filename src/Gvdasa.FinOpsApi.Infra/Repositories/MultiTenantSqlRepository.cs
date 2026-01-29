using System.Linq.Expressions;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gvdasa.GVmodeloexemploapi.Infra.Repositories;

public abstract class MultiTenantSqlRepository<T>
(
    SqlServerDataContext context,
    ILogger<MultiTenantSqlRepository<T>> logger
) where T : EntidadeMultiTenant
{
    protected abstract DbSet<T> _dbSet { get; }
    protected readonly SqlServerDataContext _context = context;
    protected readonly ILogger<MultiTenantSqlRepository<T>> _logger = logger;

    public virtual async Task Inserir(T entidade)
    {
        _logger.LogInformation("Inserindo entidade do tipo {tipo} no BD. Tenant: {ten}. Id: {id}", typeof(T).Name, entidade.IdTenant, entidade.Id);
        await _dbSet.AddAsync(entidade);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Entidade do tipo {tipo} inserida no BD com sucesso. Tenant: {ten}. Id: {id}", typeof(T).Name, entidade.IdTenant, entidade.Id);
    }

    public virtual async Task<IEnumerable<T>> Obter(string idTenant, Expression<Func<T, bool>> expression)
    {
        List<T> registros = await _dbSet
            .Where(x => x.IdTenant == idTenant)
            .Where(expression)
            .ToListAsync();

        return registros;
    }

    public async Task<T?> Obter(string idTenant, Guid id)
    {
        return await _dbSet.FirstOrDefaultAsync(x => x.IdTenant == idTenant && x.Id == id);
    }

    public virtual async Task Atualizar(T entidade)
    {
        _logger.LogInformation("Atualizando entidade do tipo {tipo} no BD. Tenant: {ten}. Id: {id}", typeof(T).Name, entidade.IdTenant, entidade.Id);
        var local = _dbSet.Local.FirstOrDefault(s => s.Id == entidade.Id);

        if(local != null)
        {
            _context.Entry(local).State = EntityState.Detached;
        }

        _context.Entry(entidade).State = EntityState.Modified;
        await _context.SaveChangesAsync();
        _logger.LogInformation("Salvando entidade do tipo {tipo} em cache. Tenant: {ten}. Id: {id}", typeof(T).Name, entidade.IdTenant, entidade.Id);
    }


    public virtual async Task Excluir(T entidade)
    {
        _logger.LogInformation("Excluindo entidade do tipo {tipo} do BD. Tenant: {ten}. Id: {id}", typeof(T).Name, entidade.IdTenant, entidade.Id);
        _dbSet.Remove(entidade);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Entidade do tipo {tipo} removida no BD com sucesso. Tenant: {ten}. Id: {id}", typeof(T).Name, entidade.IdTenant, entidade.Id);
    }
}
