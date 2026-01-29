using System.Diagnostics.CodeAnalysis;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

/// <summary>
/// Comando para obter token de client credentials
/// </summary>
[ExcludeFromCodeCoverage]
public class ObterTokenCommand : IRequest<string>
{
    public required string IdTenant { get; init; }
}
