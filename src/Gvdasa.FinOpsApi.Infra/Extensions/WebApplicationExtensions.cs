using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Gvdasa.GVmodeloexemploapi.Infra.Extensions;

[ExcludeFromCodeCoverage]
public static class WebApplicationExtensions
{
    public static void ExecutarMigrations(this WebApplication app)
    {
        IServiceScope scope = app.Services.CreateScope();
        SqlServerDataContext dbContext = scope.ServiceProvider.GetRequiredService<SqlServerDataContext>();
        dbContext.Database.SetCommandTimeout(120); // aumenta o timeout de forma temporária
        dbContext.Database.Migrate();
    }
}
