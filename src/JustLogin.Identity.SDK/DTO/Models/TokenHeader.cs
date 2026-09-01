namespace JustLogin.Identity.SDK.DTO.Models
{
    public class TokenHeader
    {
        public string Algorithm { get; set; } = "HS256"; // (alg)the cryptography algorithm used to sign the token
        public string TokenType { get; set; } = "JWT"; // (typ)the type of token
    }
}