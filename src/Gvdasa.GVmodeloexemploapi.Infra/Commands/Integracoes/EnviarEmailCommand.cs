using System.Diagnostics.CodeAnalysis;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class EnviarEmailCommand : IRequest<Guid>
{
    public required string IdTenant { get; init; }
    public required IEnumerable<Destinatario> Destinatarios { get; init; }
    public required string Assunto { get; init; }
    public required string Corpo { get; init; }
    public required bool CorpoHtml { get; init; }
}

public record Destinatario(string EnderecoEmail, string? Nome);
