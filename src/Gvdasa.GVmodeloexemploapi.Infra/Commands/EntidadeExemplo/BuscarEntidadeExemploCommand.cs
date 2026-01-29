using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class BuscarEntidadeExemploCommand : BuscarEntidadeMultiTenantCommand<EntidadeExemplo>, IRequest<EntidadeExemplo>
{ }
