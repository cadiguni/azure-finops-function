using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gvdasa.GVmodeloexemploapi.Infra.DependencyInjection;
using Gvdasa.GVmodeloexemploapi.Domain.MediatR;
using MediatR;
using Gvdasa.GVmodeloexemploapi.Domain.RegrasDeNegocio;

namespace Gvdasa.GVmodeloexemploapi.Domain.DependencyInjection;

[ExcludeFromCodeCoverage]
public static class ServiceCollectionExtensions
{
    public static void AdicionarDominio(this IServiceCollection collection, IConfiguration configuration)
    {
        collection.AdicionarInfra(configuration);
        collection.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(Assembly.GetCallingAssembly()));
        collection.AdicionarRegrasDeNegocio();
    }

    public static IServiceCollection AdicionarRegrasDeNegocio(this IServiceCollection collection)
    {
        collection.AddTransient<IRegraDeNegocioResolver, RegraDeNegocioResolver>();
        collection.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidacaoRegrasDeNegocioBehavior<,>));

        var assembly = Assembly.GetExecutingAssembly();

        // Busca todos os tipos que implementam a interface IRegraDeNegocio<>
        var tiposComRegraDeNegocio = assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface) // Filtra classes concretas
            .SelectMany(t => t.GetInterfaces(), (t, i) => new { Classe = t, Interface = i })
            .Where(t => t.Interface.IsGenericType && t.Interface.GetGenericTypeDefinition() == typeof(IRegraDeNegocio<>))
            .ToList();

        // Para cada tipo encontrado, registra a implementação no DI
        foreach (var tipo in tiposComRegraDeNegocio)
        {
            collection.AddTransient(tipo.Interface, tipo.Classe);
        }

        return collection;
    }
}
