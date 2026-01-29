using Gvdasa.GVmodeloexemploapi.Infra.Commands;
using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;

namespace Gvdasa.GVmodeloexemploapi.Domain.RegrasDeNegocio;

public interface IRegraDeNegocio<T> where T : EntidadeMultiTenant
{
    Task ValidarEscrita(IEscritaMultiTenantCommand<T> comando);
}
