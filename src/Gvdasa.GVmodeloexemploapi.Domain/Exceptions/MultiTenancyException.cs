using System.Diagnostics.CodeAnalysis;

namespace Gvdasa.GVmodeloexemploapi.Domain.Exceptions;

[Serializable]
[ExcludeFromCodeCoverage]
public class MultiTenancyException : Exception
{
    public MultiTenancyException(string message) : base(message) { }
    public MultiTenancyException(string message, Exception inner) : base(message, inner) { }
}
