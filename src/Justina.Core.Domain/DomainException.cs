namespace Justina.Core.Domain;

/// <summary>
/// Raised when an aggregate is asked to do something its invariants forbid.
/// A defect in the caller, not an expected outcome — expected refusals are <c>Result.Failure</c>.
/// </summary>
public class DomainException : Exception
{
    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
