using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class AtualizarEntidadeMultiTenantCommand<T>
    : MultiTenantCommand
    , IEscritaMultiTenantCommand<T>
    , IRequest<T> where T : EntidadeMultiTenant
{
    [JsonIgnore]
    public required Guid Id { get; init; }
}
