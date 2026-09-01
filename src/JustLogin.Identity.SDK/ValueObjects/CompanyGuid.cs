namespace JustLogin.Identity.SDK.ValueObjects;

public record CompanyGuid
{
    public string Value { get; }

    public CompanyGuid(string value)
    {
        if (value.Length != 32)
        {
            throw new ArgumentException("CompanyId must be 32 characters in length.", nameof(value));
        }

        Value = value.ToUpper();
    }
    
    public static implicit operator string(CompanyGuid source) => source.Value;
    public static implicit operator CompanyGuid(string value) => new(value);
}