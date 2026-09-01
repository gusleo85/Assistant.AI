using Justina.Core.Application.Documents;
using Justina.Core.Domain.Results;
using Justina.Core.Infrastructure.Documents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Justina.Core.UnitTests;

public class DocumentProcessorTests
{
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03,
    ];

    private readonly IPdfPageRenderer _renderer = Substitute.For<IPdfPageRenderer>();

    private DocumentProcessor CreateProcessor(Action<DocumentProcessingOptions>? configure = null)
    {
        var options = new DocumentProcessingOptions();
        configure?.Invoke(options);

        return new DocumentProcessor(
            _renderer,
            Options.Create(options),
            NullLogger<DocumentProcessor>.Instance);
    }

    [Fact]
    public async Task Empty_content_is_rejected()
    {
        var result = await CreateProcessor().ProcessAsync([], "application/pdf", null, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.DocumentUnreadable);
    }

    [Fact]
    public async Task Oversized_content_is_rejected_before_parsing()
    {
        var processor = CreateProcessor(o => o.MaxBytes = 8);

        var result = await processor.ProcessAsync(TestPdf.WithText("hello"), "application/pdf", null, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.MediaTooLarge);
    }

    [Fact]
    public async Task An_unsupported_format_is_rejected()
    {
        var executable = "MZ\0"u8.ToArray();

        var result = await CreateProcessor().ProcessAsync(executable, "application/pdf", null, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.UnsupportedMedia);
    }

    /// <summary>The declared type is attacker-controlled; the sniffed type is what we act on (§38).</summary>
    [Fact]
    public async Task A_file_lying_about_its_type_is_treated_as_what_it_actually_is()
    {
        var result = await CreateProcessor().ProcessAsync(PngBytes, "application/pdf", "receipt.pdf", default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Kind.ShouldBe(DocumentKind.Image);
        result.Value.MimeType.ShouldBe("image/png");
    }

    [Fact]
    public async Task A_corrupt_pdf_is_a_user_facing_refusal_not_an_exception()
    {
        var corrupt = "%PDF-1.4\nthis is not a pdf body at all"u8.ToArray();

        var result = await CreateProcessor().ProcessAsync(corrupt, "application/pdf", null, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.DocumentUnreadable);
    }

    [Fact]
    public async Task A_text_pdf_is_classified_and_every_page_is_read()
    {
        var pdf = TestPdf.WithText(
            new string('a', 400),
            new string('b', 400),
            new string('c', 400));

        var result = await CreateProcessor().ProcessAsync(pdf, "application/pdf", "receipt.pdf", default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Kind.ShouldBe(DocumentKind.TextPdf);
        result.Value.PageCount.ShouldBe(3);
        result.Value.Pages.Count.ShouldBe(3);

        // Page 1 is never assumed to be the whole document (§24).
        result.Value.Pages[2].Text.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_pdf_without_a_text_layer_is_classified_as_scanned()
    {
        var result = await CreateProcessor().ProcessAsync(TestPdf.WithoutText(2), "application/pdf", null, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Kind.ShouldBe(DocumentKind.ScannedPdf);
    }

    [Fact]
    public async Task Too_many_pages_is_rejected_with_the_limit_stated()
    {
        var processor = CreateProcessor(o => o.MaxPages = 2);

        var result = await processor.ProcessAsync(TestPdf.WithoutText(5), "application/pdf", null, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.TooManyPages);
        result.Error.Message.ShouldContain("2");
    }

    [Fact]
    public async Task A_pdf_within_provider_limits_is_marked_for_direct_upload_and_not_rasterized()
    {
        var result = await CreateProcessor().ProcessAsync(TestPdf.WithoutText(1), "application/pdf", null, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.SupportsDirectProviderUpload.ShouldBeTrue();

        _renderer.DidNotReceiveWithAnyArgs().RenderPages(default!, default, default, default);
    }

    [Fact]
    public async Task A_scanned_pdf_beyond_the_provider_limit_falls_back_to_rasterization()
    {
        _renderer
            .RenderPages(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyDictionary<int, byte[]>>(
                new Dictionary<int, byte[]> { [1] = [1, 2, 3], [2] = [4, 5, 6] }));

        var processor = CreateProcessor(o => o.ProviderMaxDirectUploadPages = 1);

        var result = await processor.ProcessAsync(TestPdf.WithoutText(2), "application/pdf", null, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.SupportsDirectProviderUpload.ShouldBeFalse();
        result.Value.Pages.ShouldAllBe(p => p.RenderedPng != null);
    }

    [Fact]
    public async Task A_rasterization_failure_is_surfaced_rather_than_thrown()
    {
        _renderer
            .RenderPages(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyDictionary<int, byte[]>>(
                ErrorCodes.DocumentUnreadable,
                "render failed"));

        var processor = CreateProcessor(o => o.AllowDirectPdfUpload = false);

        var result = await processor.ProcessAsync(TestPdf.WithoutText(1), "application/pdf", null, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.DocumentUnreadable);
    }
}

/// <summary>
/// The sniffer is the security boundary for media type, so every format it claims to support has a test.
/// JPEG and WEBP were previously unexercised.
/// </summary>
public class MediaTypeSnifferTests
{
    [Fact]
    public void A_jpeg_is_recognised()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

        MediaTypeSniffer.Sniff(jpeg).ShouldBe(MediaTypeSniffer.Jpeg);
    }

    [Fact]
    public void A_png_is_recognised()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        MediaTypeSniffer.Sniff(png).ShouldBe(MediaTypeSniffer.Png);
    }

    [Fact]
    public void A_webp_is_recognised_by_its_riff_container()
    {
        var webp = new List<byte>();
        webp.AddRange("RIFF"u8.ToArray());
        webp.AddRange([0x20, 0x00, 0x00, 0x00]);
        webp.AddRange("WEBP"u8.ToArray());

        MediaTypeSniffer.Sniff(webp.ToArray()).ShouldBe(MediaTypeSniffer.Webp);
    }

    [Fact]
    public void A_riff_container_that_is_not_webp_is_refused()
    {
        var wav = new List<byte>();
        wav.AddRange("RIFF"u8.ToArray());
        wav.AddRange([0x20, 0x00, 0x00, 0x00]);
        wav.AddRange("WAVE"u8.ToArray());

        MediaTypeSniffer.Sniff(wav.ToArray()).ShouldBeNull();
    }

    [Fact]
    public void A_pdf_is_recognised()
    {
        MediaTypeSniffer.Sniff("%PDF-1.7"u8.ToArray()).ShouldBe(MediaTypeSniffer.Pdf);
    }

    [Theory]
    [InlineData("MZ")]
    [InlineData("<html>")]
    [InlineData("GIF89a")]
    public void Anything_else_is_refused(string content)
    {
        MediaTypeSniffer.Sniff(System.Text.Encoding.ASCII.GetBytes(content)).ShouldBeNull();
    }

    [Fact]
    public void Content_too_short_to_identify_is_refused_rather_than_throwing()
    {
        MediaTypeSniffer.Sniff([0xFF]).ShouldBeNull();
        MediaTypeSniffer.Sniff([]).ShouldBeNull();
    }

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData("application/pdf", false)]
    public void Image_types_are_distinguished_from_pdf(string mimeType, bool isImage)
    {
        MediaTypeSniffer.IsImage(mimeType).ShouldBe(isImage);
    }
}
