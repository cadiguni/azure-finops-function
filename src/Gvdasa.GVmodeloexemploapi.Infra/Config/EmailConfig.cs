using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.Infra.Config;

[ExcludeFromCodeCoverage]
public class EmailConfig
{
    public required string Url { get; init; }
}
