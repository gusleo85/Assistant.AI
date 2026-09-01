namespace JustLogin.Identity.SDK.Exceptions;

public class JustLoginIdentitySdkException : Exception
{
    public JustLoginIdentitySdkException(string? message) : base(message)
    {
        
    }
    
    public JustLoginIdentitySdkException(string? message, Exception exception) : base(message, exception)
    {
        
    }
}