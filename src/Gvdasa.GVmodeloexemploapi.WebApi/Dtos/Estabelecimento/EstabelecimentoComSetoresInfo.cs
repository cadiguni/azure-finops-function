using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Dtos;

[ExcludeFromCodeCoverage]
public record EstabelecimentoComSetoresInfo : EstabelecimentoInfo
{
    public IEnumerable<SetorResumoInfo> Setores { get; init; } = new List<SetorResumoInfo>();

    public override IEnumerable<EstabelecimentoInfo> ObterEstabelecimentosDesencadeados()
    {
        return new List<EstabelecimentoInfo> { this }.Concat(Estabelecimentos.SelectMany(x => x.ObterEstabelecimentosDesencadeados()));
    }
}
