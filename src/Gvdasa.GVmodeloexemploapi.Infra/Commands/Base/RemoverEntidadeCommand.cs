using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class RemoverEntidadeCommand : BaseCommand
{
    public required Guid Id { get; set; }
}
