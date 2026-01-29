using System.Diagnostics.CodeAnalysis;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class RemoverArquivoCommand : IRequest
{
    public required string IdTenant { get; init; }
    public required Guid IdArquivo { get; init; }
}
