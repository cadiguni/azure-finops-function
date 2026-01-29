using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public abstract class MultiTenantCommand : BaseCommand
{
    [JsonIgnore]
    public required string IdTenant { get; init; }
}
