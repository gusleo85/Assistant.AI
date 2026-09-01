namespace JustLogin.Identity.SDK.DTO.Models
{
    public class Payload
    {
        public string ID { get; set; } = ""; // (jti)unique identifier for a token
        public string Audience { get; set; } = ""; // (aud)token is intend for whom
        public string Issuer { get; set; } = ""; // (iss)token issuer
        public string TokenExpire { get; set; } = ""; // (exp)expiration date(may be string or double)
        public string NotBefore { get; set; } = ""; // (nbf)not before date(>= specific date) to be valid
    }
}