using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Gvdasa.GVmodeloexemploapi.Infra.Contexts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;

namespace Gvdasa.GVmodeloexemploapi.Infra.DependencyInjection;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    public static void AdicionarInfra(this IServiceCollection collection, IConfiguration configuration)
    {
        collection.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly()));

        collection.AddScoped<SqlServerDataContext, SqlServerDataContext>();
        collection.AddDbContext<SqlServerDataContext>(opt =>
        {
            opt.UseSqlServer(configuration.GetConnectionString("SQLServer")!);
        });


    }
}
