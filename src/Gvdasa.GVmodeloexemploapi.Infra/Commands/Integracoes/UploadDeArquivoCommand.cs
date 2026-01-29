using System.Diagnostics.CodeAnalysis;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class UploadDeArquivoCommand : IRequest<Guid>
{
    public required string IdTenant { get; init; }
    public required Stream ConteudoArquivo { get; init; }
    public required string NomeArquivoComExtensao { get; init; }
    public required string ContentType { get; init; }
}
