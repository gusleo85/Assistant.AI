using Justina.Core.Domain.Results;
using Justina.Core.Infrastructure.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Justina.Core.UnitTests;

/// <summary>
/// The staged path arrives as a tool argument from a language model, so it is untrusted in the strongest
/// sense. These tests are the containment boundary (§38).
/// </summary>
public sealed class StagedMediaReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"justina-staged-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"justina-outside-{Guid.NewGuid():N}");

    public StagedMediaReaderTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    private StagedMediaReader CreateReader(string? root = null, long maxBytes = 32 * 1024 * 1024) =>
        new(
            Options.Create(new StagedMediaOptions { RootPath = root ?? _root, MaxBytes = maxBytes }),
            NullLogger<StagedMediaReader>.Instance);

    private string WriteFile(string directory, string name, byte[] content)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task A_staged_file_is_read()
    {
        var path = WriteFile(_root, "receipt.jpg", [0xFF, 0xD8, 0xFF, 0xE0]);

        var result = await CreateReader().ReadAsync(path, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Content.Length.ShouldBe(4);
        result.Value.FileName.ShouldBe("receipt.jpg");
        result.Value.MimeType.ShouldBe("image/jpeg");
    }

    [Fact]
    public async Task A_nested_staged_file_is_read()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "openclaw-staged-abc")).FullName;
        var path = WriteFile(nested, "input.pdf", "%PDF-1.4"u8.ToArray());

        var result = await CreateReader().ReadAsync(path, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.MimeType.ShouldBe("application/pdf");
    }

    /// <summary>The reason this class exists.</summary>
    [Fact]
    public async Task A_path_outside_the_root_is_refused()
    {
        var path = WriteFile(_outside, "secret.txt", "secret"u8.ToArray());

        var result = await CreateReader().ReadAsync(path, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Traversal_out_of_the_root_is_refused()
    {
        var path = WriteFile(_outside, "secret.txt", "secret"u8.ToArray());
        var traversal = Path.Combine(_root, "..", Path.GetFileName(_outside), "secret.txt");

        var result = await CreateReader().ReadAsync(traversal, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.NotFound);
        File.Exists(path).ShouldBeTrue("the test file must exist, or this would pass for the wrong reason");
    }

    /// <summary>
    /// A plain StartsWith check would accept a sibling directory whose name merely begins with the root.
    /// </summary>
    [Fact]
    public async Task A_sibling_directory_with_the_root_as_a_name_prefix_is_refused()
    {
        var evil = Directory.CreateDirectory(_root + "-evil").FullName;

        try
        {
            var path = WriteFile(evil, "receipt.jpg", [0xFF, 0xD8, 0xFF]);

            var result = await CreateReader().ReadAsync(path, default);

            result.IsFailure.ShouldBeTrue();
            result.Error.Code.ShouldBe(ErrorCodes.NotFound);
        }
        finally
        {
            Directory.Delete(evil, recursive: true);
        }
    }

    /// <summary>
    /// Distinguishable refusals would let a caller map the filesystem one guess at a time, so a file
    /// outside the root and a file that does not exist must be indistinguishable.
    /// </summary>
    [Fact]
    public async Task Refusals_do_not_reveal_whether_the_file_exists()
    {
        var outsideFile = WriteFile(_outside, "real.txt", "x"u8.ToArray());
        var reader = CreateReader();

        var outside = await reader.ReadAsync(outsideFile, default);
        var missing = await reader.ReadAsync(Path.Combine(_root, "does-not-exist.jpg"), default);

        outside.Error.Code.ShouldBe(missing.Error.Code);
        outside.Error.Message.ShouldBe(missing.Error.Message);
    }

    [Fact]
    public async Task An_oversized_file_is_refused_before_it_is_read()
    {
        var path = WriteFile(_root, "big.jpg", new byte[2048]);

        var result = await CreateReader(maxBytes: 1024).ReadAsync(path, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.MediaTooLarge);
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var path = WriteFile(_root, "empty.jpg", []);

        var result = await CreateReader().ReadAsync(path, default);

        result.IsFailure.ShouldBeTrue();
    }

    /// <summary>Fails closed: a deployment that does not share the staging volume reads nothing.</summary>
    [Fact]
    public async Task Staged_reads_are_refused_when_no_root_is_configured()
    {
        var reader = CreateReader(root: string.Empty);

        reader.IsConfigured.ShouldBeFalse();

        var result = await reader.ReadAsync("/anything", default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.NotAvailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_path_is_refused(string path)
    {
        var result = await CreateReader().ReadAsync(path, default);

        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task A_directory_is_not_mistaken_for_a_file()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "a-directory")).FullName;

        var result = await CreateReader().ReadAsync(directory, default);

        result.IsFailure.ShouldBeTrue();
    }

    /// <summary>
    /// The extension is a logging hint only. The document pipeline sniffs the real type from the bytes,
    /// so a lie here changes nothing downstream.
    /// </summary>
    [Fact]
    public async Task An_unknown_extension_still_reads_and_defers_the_type_to_sniffing()
    {
        var path = WriteFile(_root, "receipt.bin", [0x89, 0x50, 0x4E, 0x47]);

        var result = await CreateReader().ReadAsync(path, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.MimeType.ShouldBe("application/octet-stream");
    }

    /// <summary>
    /// The gateway may describe an attachment relative to its workspace rather than absolutely.
    /// </summary>
    [Fact]
    public async Task A_relative_path_resolves_against_the_staging_root()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "staged-1")).FullName;
        WriteFile(nested, "input.jpg", [0xFF, 0xD8, 0xFF]);

        var result = await CreateReader().ReadAsync("staged-1/input.jpg", default);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_relative_path_that_repeats_the_media_inbound_prefix_still_resolves()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_root, "staged-2")).FullName;
        WriteFile(nested, "input.jpg", [0xFF, 0xD8, 0xFF]);

        var result = await CreateReader().ReadAsync("media/inbound/staged-2/input.jpg", default);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_relative_path_cannot_climb_out_of_the_root()
    {
        WriteFile(_outside, "secret.txt", "secret"u8.ToArray());

        var result = await CreateReader().ReadAsync("../" + Path.GetFileName(_outside) + "/secret.txt", default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.NotFound);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _root, _outside })
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Test cleanup only.
            }
        }
    }
}
