using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Justina.Core.Application.Documents;
using Justina.Core.Application.Vision;
using Justina.Core.Domain.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Core.Infrastructure.Vision;

/// <summary>
/// OpenAI implementation of the shared Vision capability, using the Responses API with a strict JSON
/// schema so the model cannot answer off-contract (§20, §21).
///
/// Document content is always attached as a separate input part — a file, an image, or a clearly delimited
/// text block — and never spliced into the instruction. Text printed inside a receipt is therefore data the
/// model is told to extract, not an instruction it can follow (§38).
/// </summary>
public sealed class OpenAiVisionProvider(
    HttpClient httpClient,
    IOptions<OpenAiVisionOptions> options,
    ILogger<OpenAiVisionProvider> logger)
    : IVisionProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OpenAiVisionOptions _options = options.Value;

    public async Task<Result<VisionExtractionResult>> ExtractAsync(
        VisionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            logger.LogError("Vision is not configured: no API key supplied");
            return Result.Failure<VisionExtractionResult>(
                ErrorCodes.VisionFailed,
                "Document reading is not available right now.");
        }

        var content = BuildContent(request);

        if (content.Count == 0)
        {
            return Result.Failure<VisionExtractionResult>(
                ErrorCodes.DocumentUnreadable,
                "I could not read anything from that document.");
        }

        var payload = new JsonObject
        {
            ["model"] = _options.Model,
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = content,
            }),
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = request.SchemaName,
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(request.JsonSchema),
                },
            },
        };

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync("responses", payload, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Log a truncated body for diagnosis; the user gets a generic message, never provider detail.
                logger.LogWarning(
                    "Vision request failed with {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    Truncate(body, 500));

                return Result.Failure<VisionExtractionResult>(
                    ErrorCodes.VisionFailed,
                    "I could not read that document right now. Please try again.");
            }

            return ParseResponse(body);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Vision request timed out");
            return Result.Failure<VisionExtractionResult>(
                ErrorCodes.VisionFailed,
                "Reading that document took too long. Please try again.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Vision request could not be sent");
            return Result.Failure<VisionExtractionResult>(
                ErrorCodes.VisionFailed,
                "I could not reach the document reader. Please try again.");
        }
    }

    private JsonArray BuildContent(VisionRequest request)
    {
        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = request.Instruction,
            },
        };

        var document = request.Document;

        if (document.Kind == DocumentKind.Image)
        {
            content.Add(ImagePart(document.MimeType, document.Content));
            return content;
        }

        if (document.SupportsDirectProviderUpload)
        {
            content.Add(new JsonObject
            {
                ["type"] = "input_file",
                ["filename"] = SafeFileName(document.FileName),
                ["file_data"] = DataUri("application/pdf", document.Content),
            });

            return content;
        }

        var renderedPages = document.Pages
            .Where(page => page.RenderedPng is not null)
            .Take(_options.MaxRenderedPages)
            .ToList();

        if (renderedPages.Count > 0)
        {
            foreach (var page in renderedPages)
            {
                content.Add(ImagePart("image/png", page.RenderedPng!));
            }

            return content;
        }

        var text = string.Join(
            "\n\n",
            document.Pages
                .Where(page => !string.IsNullOrWhiteSpace(page.Text))
                .Select(page => $"--- page {page.Number} ---\n{page.Text}"));

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // Delimited and labelled as document content, so the model treats it as the material to extract from.
        content.Add(new JsonObject
        {
            ["type"] = "input_text",
            ["text"] = $"<document_content>\n{Truncate(text, _options.MaxTextCharacters)}\n</document_content>",
        });

        return content;
    }

    private static JsonObject ImagePart(string mimeType, byte[] bytes) =>
        new()
        {
            ["type"] = "input_image",
            ["image_url"] = DataUri(mimeType, bytes),
        };

    private Result<VisionExtractionResult> ParseResponse(string body)
    {
        try
        {
            var root = JsonNode.Parse(body)?.AsObject();
            var text = ExtractOutputText(root);

            if (string.IsNullOrWhiteSpace(text))
            {
                logger.LogWarning("Vision response contained no output text");
                return Result.Failure<VisionExtractionResult>(
                    ErrorCodes.VisionFailed,
                    "I could not read that document. Please try a clearer copy.");
            }

            var usage = root?["usage"];

            return Result.Success(new VisionExtractionResult(
                text,
                root?["model"]?.GetValue<string>() ?? _options.Model,
                usage?["input_tokens"]?.GetValue<int>(),
                usage?["output_tokens"]?.GetValue<int>()));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            logger.LogWarning(exception, "Vision response could not be parsed");
            return Result.Failure<VisionExtractionResult>(
                ErrorCodes.VisionFailed,
                "I could not read that document. Please try again.");
        }
    }

    private static string? ExtractOutputText(JsonObject? root)
    {
        if (root?["output"] is not JsonArray output)
        {
            return null;
        }

        foreach (var item in output)
        {
            if (item?["content"] is not JsonArray parts)
            {
                continue;
            }

            foreach (var part in parts)
            {
                var text = part?["text"]?.GetValue<string>();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static string DataUri(string mimeType, byte[] bytes) =>
        $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";

    /// <summary>A channel-supplied file name is untrusted; only a safe stem is passed on.</summary>
    private static string SafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "document.pdf";
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var safe = new string(stem.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(64).ToArray());

        return safe.Length == 0 ? "document.pdf" : $"{safe}.pdf";
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
