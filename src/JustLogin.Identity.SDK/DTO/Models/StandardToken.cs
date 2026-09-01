namespace JustLogin.Identity.SDK.DTO.Models
{
    public class StandardToken
    {
        public TokenHeader Header { get; set; } = new TokenHeader();
        public Payload TokenData { get; set; } = new Payload();

        public string Signature { get; set; } = "";
    }
}