using System.Diagnostics.CodeAnalysis;
using Gvdasa.GVmodeloexemploapi.WebApi.Dtos;

namespace Gvdasa.GVmodeloexemploapi.WebApi.Extensions;

[ExcludeFromCodeCoverage]
public static class UsuariosInfoExtensions
{
    public static string ObterIdentificacaoUsuario(this IEnumerable<UsuarioInfo> usuariosInfo, int idUsuario)
    {
        UsuarioInfo? usuarioInfo = usuariosInfo.FirstOrDefault(u => u.Id.Equals(idUsuario));

        return usuarioInfo != null
            ? (usuarioInfo.Nome ?? usuarioInfo.Email ?? usuarioInfo.Username)
            : "indefinido";
    }

    public static string? ObterIdentificacaoUsuario(this IEnumerable<UsuarioInfo> usuariosInfo, int? idUsuario)
    {
        return idUsuario is null ? null : usuariosInfo.ObterIdentificacaoUsuario(idUsuario.Value);
    }

}
