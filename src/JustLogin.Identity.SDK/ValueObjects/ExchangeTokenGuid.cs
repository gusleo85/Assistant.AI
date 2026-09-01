namespace JustLogin.Identity.SDK.ValueObjects;

public record ExchangeTokenGuid
{
    public string Value { get; }

    public ExchangeTokenGuid(string value)
    {
        if (value.Length != 32)
        {
            throw new ArgumentException("CoExchange Token must be 32 characters in length.", nameof(value));
        }

        Value = value.ToLower();
    }

    public static implicit operator string(ExchangeTokenGuid source) => source.Value;
    public static implicit operator ExchangeTokenGuid(string value) => new(value);
}