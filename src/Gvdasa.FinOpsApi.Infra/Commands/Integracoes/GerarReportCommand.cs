using System.Diagnostics.CodeAnalysis;
using MediatR;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

[ExcludeFromCodeCoverage]
public class GerarReportCommand : IRequest<string>
{
    /// <summary>
    /// Dados que serão utilizados para gerar o conteúdo do report. São os parâmetros para geração destes.
    /// </summary>
    public required object Dados { get; init; }
}
