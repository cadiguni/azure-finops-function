using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.Infra.Config;

[ExcludeFromCodeCoverage]
public class ClientConfig
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
}
