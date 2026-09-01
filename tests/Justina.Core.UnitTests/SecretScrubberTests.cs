using Justina.Core.Infrastructure.Security;
using Shouldly;

namespace Justina.Core.UnitTests;

public class SecretScrubberTests
{
    /// <summary>
    /// The reason this class exists: Telegram puts the bot token in the URL path, and anything that
    /// records request URLs would otherwise record the credential (§40).
    /// </summary>
    [Fact]
    public void A_telegram_bot_token_is_removed_from_the_url_path()
    {
        var scrubbed = SecretScrubber.Redact("https://api.telegram.org/bot123456:AAH-SECRET-TOKEN/getFile?file_id=abc");

        scrubbed.ShouldNotContain("AAH-SECRET-TOKEN");
        scrubbed.ShouldNotContain("123456");
        scrubbed.ShouldBe("https://api.telegram.org/bot***/getFile?file_id=abc");
    }

    [Fact]
    public void The_file_download_path_is_scrubbed_too()
    {
        var scrubbed = SecretScrubber.Redact("https://api.telegram.org/file/bot999:SECRET/documents/file_1.pdf");

        scrubbed.ShouldNotContain("SECRET");
        scrubbed.ShouldContain("/documents/file_1.pdf");
    }

    [Theory]
    [InlineData("https://graph.facebook.com/v21.0/media?access_token=EAAG-SECRET", "EAAG-SECRET")]
    [InlineData("https://example.test/x?api_key=abc123&page=2", "abc123")]
    [InlineData("https://example.test/x?a=1&token=zzz", "zzz")]
    [InlineData("https://example.test/x?signature=deadbeef", "deadbeef")]
    public void Sensitive_query_values_are_removed(string url, string secret)
    {
        var scrubbed = SecretScrubber.Redact(url);

        scrubbed.ShouldNotContain(secret);
        scrubbed.ShouldContain("***");
    }

    [Fact]
    public void Non_sensitive_query_parameters_survive_so_the_trace_is_still_useful()
    {
        var scrubbed = SecretScrubber.Redact("https://example.test/x?api_key=abc123&page=2");

        scrubbed.ShouldContain("page=2");
    }

    [Fact]
    public void An_ordinary_url_is_left_alone()
    {
        const string url = "https://api.openai.com/v1/responses";

        SecretScrubber.Redact(url).ShouldBe(url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_in_gives_nothing_out(string? value)
    {
        SecretScrubber.Redact(value).ShouldBe(string.Empty);
    }

    [Fact]
    public void A_uri_overload_scrubs_the_same_way()
    {
        var uri = new Uri("https://api.telegram.org/bot42:SECRET/sendMessage");

        SecretScrubber.Redact(uri).ShouldNotContain("SECRET");
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("X-Justina-Tool-Key")]
    [InlineData("X-Hub-Signature-256")]
    [InlineData("Api-Key")]
    [InlineData("X-Auth-Token")]
    public void Credential_bearing_headers_are_recognised(string header)
    {
        SecretScrubber.IsSensitiveHeader(header).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("X-Correlation-Id")]
    [InlineData("Idempotency-Key")]
    public void Ordinary_headers_are_not_treated_as_secrets(string header)
    {
        SecretScrubber.IsSensitiveHeader(header).ShouldBeFalse();
    }
}
