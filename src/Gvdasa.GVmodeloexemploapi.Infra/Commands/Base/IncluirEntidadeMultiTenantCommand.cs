using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class IncluirEntidadeMultiTenantCommand<T>
    : MultiTenantCommand
    , IRequest<T>
    , IEscritaMultiTenantCommand<T>
    where T : EntidadeMultiTenant
{
    [JsonIgnore]
    public Guid Id { get; init; } = Guid.NewGuid();
}
