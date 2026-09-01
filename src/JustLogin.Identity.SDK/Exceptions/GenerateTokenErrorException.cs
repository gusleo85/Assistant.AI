namespace JustLogin.Identity.SDK.Exceptions
{
    public class GenerateTokenErrorException : JustLoginIdentitySdkException
    {
        public GenerateTokenErrorException(Exception exception) : base("Unknown Exception while generating Token", exception)
        {
        }
    }
}