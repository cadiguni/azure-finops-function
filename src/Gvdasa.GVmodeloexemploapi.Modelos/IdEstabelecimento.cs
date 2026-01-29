using System.Text.RegularExpressions;

namespace Gvdasa.GVmodeloexemploapi.Modelos;

public partial class IdEstabelecimento(string valor) : IEquatable<IdEstabelecimento>
{
    private readonly string _valor = Normalizar(valor);

    [GeneratedRegex(@"^(?!.*(\.0|^0|\.\d?0(\.|$)|0$))\d{1,2}(\.\d{1,2}){0,2}$")]
    private static partial Regex RegexIdEstabelecimento();

    public static string Normalizar(string str)
    {
        string normalizado = str.Replace('_', '.').Replace('-', '.');
        Validar(normalizado);
        return normalizado;
    }

    public static void Validar(string normalizado)
    {
        if(!RegexIdEstabelecimento().IsMatch(normalizado))
        {
            throw new ArgumentException("Identificador de estabelecimento inválido");
        }
    }

    // Operador de conversão implícito de string para IdEstabelecimento
    public static implicit operator IdEstabelecimento(string id)
    {
        return new IdEstabelecimento(id);
    }

    // Operador de conversão implícito de IdEstabelecimento para string
    public static implicit operator string(IdEstabelecimento idEstabelecimento)
    {
        return idEstabelecimento._valor;
    }

    public override string ToString()
    {
        return _valor;
    }

    public bool Equals(IdEstabelecimento? other)
    {
        return _valor == other?._valor;
    }

    public bool Equals(string other)
    {
        return _valor == Normalizar(other);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
        {
            return false;
        }

        if (obj is IdEstabelecimento id)
        {
            return Equals(id);
        }
        else if (obj is string str)
        {
            return Equals(str);
        }
        else
        {
            throw new ArgumentException($"Tipo não suportardo por {nameof(IdEstabelecimento)}");
        }
    }

    public override int GetHashCode()
    {
        return _valor.GetHashCode();
    }

    public static bool operator ==(IdEstabelecimento left, IdEstabelecimento right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(IdEstabelecimento left, IdEstabelecimento right)
    {
        return !left.Equals(right);
    }

    public static bool operator ==(IdEstabelecimento left, string right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(IdEstabelecimento left, string right)
    {
        return !left.Equals(right);
    }
}
