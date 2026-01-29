using Gvdasa.GVmodeloexemploapi.Modelos.Entidades;

namespace Gvdasa.GVmodeloexemploapi.Infra.Commands;

public interface IEscritaMultiTenantCommand<T> where T : EntidadeMultiTenant
{
    string IdTenant { get; }
    int? IdUsuario { get; }
}
