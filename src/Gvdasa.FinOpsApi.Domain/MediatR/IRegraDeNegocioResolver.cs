using Gvdasa.GVmodeloexemploapi.Domain.RegrasDeNegocio;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;

namespace Gvdasa.GVmodeloexemploapi.Domain.MediatR;

public interface IRegraDeNegocioResolver
{
    IRegraDeNegocio<T> ObterRegra<T>() where T : EntidadeMultiTenant;
}
