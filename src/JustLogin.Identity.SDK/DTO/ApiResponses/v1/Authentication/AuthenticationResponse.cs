using System.Text.Json.Serialization;

namespace JustLogin.Identity.SDK.DTO.ApiResponses.v1.Authentication;
public class AuthenticationResponse
{
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; }
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; }
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; }
    [JsonPropertyName("id_token")]
    public string IdentityToken { get; set; }
    [JsonPropertyName("Token_Provider")]
    public string TokenProvider { get; set; }
    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
    [JsonPropertyName("scope")]
    public string Scope { get; set; }
}