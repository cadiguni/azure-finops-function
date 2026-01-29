using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Controllers;

[Route("api/[controller]")]
[ApiController]
public class InfoController(IConfiguration configuration) : Controller
{
    private readonly IConfiguration _configuration = configuration;

    [HttpGet]
    public Informacao GetVersion()
    {
        return new Informacao(_configuration);
    }
}

public class Informacao(IConfiguration configuration)
{
    public InformacaoApp App { get; } = new InformacaoApp(configuration);
}

public class InformacaoApp(IConfiguration configuration)
{
    public string Versao { get; } = Assembly.GetEntryAssembly()!.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public string Nome { get; } = configuration["AppInfo:Nome"]!;
    public string Descricao { get; } = configuration["AppInfo:Descricao"]!;
    public string Time { get; } = configuration["AppInfo:Time"]!;
}
