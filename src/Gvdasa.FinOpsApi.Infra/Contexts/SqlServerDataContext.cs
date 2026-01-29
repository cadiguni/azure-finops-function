using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using Microsoft.EntityFrameworkCore;

namespace Gvdasa.GVmodeloexemploapi.Infra.Contexts;

[ExcludeFromCodeCoverage]
public class SqlServerDataContext : DbContext
{
    public virtual DbSet<EntidadeExemplo> EntidadesExemplo { get; set; } = default!;

    public SqlServerDataContext(DbContextOptions<SqlServerDataContext> options) : base(options) { }
    public SqlServerDataContext() { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema("GVmodeloexemploapi");

        // adicionar mapeamentos
        modelBuilder.Entity<EntidadeExemplo>().ToTable("EntidadesExemplo");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlServer();
        }

        base.OnConfiguring(optionsBuilder);
    }
}
