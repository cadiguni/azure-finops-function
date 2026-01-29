using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.Infra.Config;

[ExcludeFromCodeCoverage]
public class ReportConfig
{
    public required string Url { get; init; }
    public required string TemplateNotificacaoAcesso { get; init; }
}
