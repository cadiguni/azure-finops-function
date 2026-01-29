using System.Diagnostics.CodeAnalysis;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class BuscarInfoArquivoCommand : IRequest<InfoArquivo>
{
    public required string IdTenant { get; init; }
    public required Guid IdArquivo { get; init; }
}

public class InfoArquivo
{
    public required Guid IdArquivo { get; init; }
    public required string NomeOriginal { get; init; }
    public required string ContentType { get; init; }
    public required long TamanhoEmBytes { get; init; }
    public string NomeSemExtensao => NomeOriginal[..NomeOriginal.LastIndexOf('.')];
}
