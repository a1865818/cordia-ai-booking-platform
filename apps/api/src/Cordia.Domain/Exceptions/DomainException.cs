namespace Cordia.Domain.Exceptions;

/// <summary>Base class for all typed domain exceptions in Cordia.</summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message) { }

    protected DomainException(string message, Exception inner) : base(message, inner) { }
}
