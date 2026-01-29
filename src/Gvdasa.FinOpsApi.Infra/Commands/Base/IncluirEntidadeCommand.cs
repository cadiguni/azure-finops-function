using System.Diagnostics.CodeAnalysis;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class IncluirEntidadeCommand<T> : BaseCommand, IRequest<T>
{}
