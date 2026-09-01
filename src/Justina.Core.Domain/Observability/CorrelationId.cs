namespace Justina.Core.Domain.Observability;

/// <summary>
/// Ties one inbound message to every log line, span and outbound API call it causes (§40).
/// </summary>
public readonly record struct CorrelationId(string Value)
{
    public static CorrelationId New() => new(Guid.NewGuid().ToString("N"));

    public static CorrelationId From(string? value) =>
        string.IsNullOrWhiteSpace(value) ? New() : new CorrelationId(value.Trim());

    public override string ToString() => Value;
}
