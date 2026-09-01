using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Justina.Core.Application.Abstractions;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Files a chat-captured receipt through the Expense API's two chat endpoints.
///
/// <code>
/// POST Receipt/chat/scan?organizationId=&amp;memberId=   the image  -> a receipt and an expense
/// PUT  Receipt/update                                    the values -> onto that receipt
/// </code>
///
/// It is two calls because the Expense API models a receipt as a photo first and values second, and
/// because <c>Receipt/scan</c> cannot be used: that one puts the image in the receipt bucket, which
/// starts the receipt-scanner Lambda, whose reading would overwrite the values the person has already
/// confirmed in the conversation. <c>chat/scan</c> stores the image without re-reading it.
///
/// Organization and member are stated explicitly because the caller is a service acting for a person who
/// has no JustLogin session, and the whole exchange is authenticated with a company system token.
/// </summary>
public sealed class ChatScanExpenseApiClient(
    HttpClient httpClient,
    IOptions<ExpenseApiOptions> options,
    IExpenseAccessTokenProvider tokens,
    IMediaStore mediaStore,
    ILogger<ChatScanExpenseApiClient> logger)
    : IExpenseApiClient
{
    private readonly ExpenseApiOptions _options = options.Value;

    public async Task<Result<ExpenseSubmissionResult>> SubmitAsync(
        ExpenseSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (submission.Tenant is null)
        {
            // A submission without a tenant would be filed against nobody. Refusing beats guessing.
            logger.LogError("Submission for {IdempotencyKey} carries no tenant", submission.IdempotencyKey);

            return Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.NotAvailable,
                "I could not work out which company this expense belongs to.");
        }

        var token = await tokens.GetAsync(submission.Tenant, cancellationToken).ConfigureAwait(false);

        if (token.IsFailure)
        {
            // The credential is the caller's problem to report, not something to retry blindly: a
            // submission sent without one comes back as an opaque 401 that reads like the API is down.
            return Result.Failure<ExpenseSubmissionResult>(token.Error);
        }

        // A receipt an earlier attempt already created. Reusing it is what stops a failure between the
        // two calls from turning one photo into two expenses.
        var receiptId = submission.ExternalReceiptId;

        if (string.IsNullOrWhiteSpace(receiptId))
        {
            var created = await CreateReceiptAsync(submission, token.Value, cancellationToken).ConfigureAwait(false);

            if (created.IsFailure)
            {
                return Result.Failure<ExpenseSubmissionResult>(created.Error);
            }

            receiptId = created.Value;

            // Told to the caller before the values are written, because this is the point where an
            // expense exists that nobody has heard about yet.
            submission.OnReceiptCreated?.Invoke(receiptId);
        }
        else
        {
            logger.LogInformation(
                "Resuming submission onto receipt {ReceiptId}, created by an earlier attempt",
                receiptId);
        }

        return await UpdateReceiptAsync(submission, receiptId, token.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the photo. The Expense API creates the expense and its attachment from it, and answers with
    /// the receipt id the values are then written onto.
    /// </summary>
    private async Task<Result<string>> CreateReceiptAsync(
        ExpenseSubmission submission,
        string token,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(submission.SourceMediaId))
        {
            logger.LogError("Submission for {IdempotencyKey} carries no image", submission.IdempotencyKey);

            return Result.Failure<string>(
                ErrorCodes.NotAvailable,
                "I no longer have the photo for this receipt, so I cannot file it.");
        }

        var media = await mediaStore.GetAsync(submission.SourceMediaId, cancellationToken).ConfigureAwait(false);

        if (media.IsFailure)
        {
            // The store is short-lived by design (§38). A receipt confirmed long after it was sent can
            // outlive its own photo, and that is worth saying plainly rather than failing obscurely.
            logger.LogWarning(
                "The image for receipt {MediaId} is no longer in the media store",
                submission.SourceMediaId);

            return Result.Failure<string>(
                ErrorCodes.NotAvailable,
                "I no longer have the photo for this receipt, so I cannot file it.");
        }

        var tenant = submission.Tenant!;
        var path = $"{_options.ChatScanPath}?organizationId={tenant.OrganizationId:D}&memberId={tenant.MemberId:D}";

        using var content = new MultipartFormDataContent();
        using var image = new ByteArrayContent(media.Value.Content);

        image.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(media.Value.MimeType) ? "image/jpeg" : media.Value.MimeType);

        // The field is named "file" because that is the IFormFile parameter's name on the other side.
        content.Add(image, "file", media.Value.FileName ?? "receipt.jpg");

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };

        ExpenseApiAuthorization.Apply(request, _options, token);
        request.Headers.TryAddWithoutValidation(_options.IdempotencyHeader, submission.IdempotencyKey);
        request.Headers.TryAddWithoutValidation(_options.CorrelationHeader, submission.CorrelationId.Value);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failure<string>("Receipt/chat/scan", response.StatusCode, body);
            }

            var id = ReadId(body, "id");

            if (string.IsNullOrWhiteSpace(id))
            {
                logger.LogWarning("Receipt/chat/scan accepted the image but returned no receipt id");

                return Result.Failure<string>(
                    ErrorCodes.ExternalApiFailed,
                    "The expense system accepted the receipt but did not return a reference.");
            }

            return Result.Success(id);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Receipt/chat/scan timed out");

            return Result.Failure<string>(
                ErrorCodes.ExternalApiFailed,
                "The expense system did not respond in time. Your receipt is saved and can be retried.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Receipt/chat/scan could not be reached");

            return Result.Failure<string>(
                ErrorCodes.ExternalApiFailed,
                "I could not reach the expense system. Your receipt is saved and can be retried.");
        }
    }

    /// <summary>
    /// Writes the confirmed values onto the receipt the previous call created.
    ///
    /// This is the same endpoint the receipt-scanner Lambda uses to report what it read; here the values
    /// come from a person who has already agreed to them, which is why nothing re-reads the image.
    /// </summary>
    private async Task<Result<ExpenseSubmissionResult>> UpdateReceiptAsync(
        ExpenseSubmission submission,
        string receiptId,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, _options.ReceiptUpdatePath)
        {
            Content = JsonContent.Create(BuildUpdatePayload(submission, receiptId)),
        };

        ExpenseApiAuthorization.Apply(request, _options, token);

        // The idempotency key travels with the request so a retry at any layer resolves to the same
        // expense rather than a second one (§33).
        request.Headers.TryAddWithoutValidation(_options.IdempotencyHeader, submission.IdempotencyKey);
        request.Headers.TryAddWithoutValidation(_options.CorrelationHeader, submission.CorrelationId.Value);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failure<ExpenseSubmissionResult>("Receipt/update", response.StatusCode, body);
            }

            // The expense is what the user will find in JustLogin, so that is the reference worth
            // keeping; the receipt id stands in when the response does not carry one.
            var expenseId = ReadId(body, "expenseId") ?? receiptId;

            logger.LogInformation(
                "Filed receipt {ReceiptId} as expense {ExpenseId} for member {MemberId}",
                receiptId,
                expenseId,
                submission.Tenant!.MemberId);

            return Result.Success(new ExpenseSubmissionResult(expenseId));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Receipt/update timed out for receipt {ReceiptId}", receiptId);

            return Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.ExternalApiFailed,
                "The expense system did not respond in time. Your receipt is saved and can be retried.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Receipt/update could not be reached for receipt {ReceiptId}", receiptId);

            return Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.ExternalApiFailed,
                "I could not reach the expense system. Your receipt is saved and can be retried.");
        }
    }

    /// <summary>
    /// The Expense API's own <c>UpdateReceiptRequest</c>, field for field. Amount and date are strings
    /// there, so they are formatted invariantly here — a receipt filed from a machine with a comma
    /// decimal separator must not arrive as a different number.
    /// </summary>
    private static JsonObject BuildUpdatePayload(ExpenseSubmission submission, string receiptId)
    {
        var taxIds = new JsonArray();

        foreach (var id in submission.TaxIds)
        {
            taxIds.Add(id.ToString());
        }

        return new JsonObject
        {
            ["receiptId"] = receiptId,
            ["amount"] = submission.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            ["date"] = submission.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),

            // The API resolves the currency from these three; the code is the reliable one, and a symbol
            // we did not read from the receipt is not worth inventing — "$" alone resolves to the wrong
            // currency in half of Asia.
            ["currencyCode"] = submission.Currency,
            ["currencyName"] = string.Empty,
            ["currencySymbol"] = string.Empty,

            ["merchantName"] = submission.Merchant,
            ["location"] = submission.Location ?? string.Empty,
            ["referenceNumber"] = submission.ReceiptNumber ?? string.Empty,

            ["categoryId"] = submission.CategoryId?.ToString(),
            ["taxIds"] = taxIds,
        };
    }

    /// <summary>Reads one id out of a response without binding to the rest of its shape.</summary>
    private static string? ReadId(string body, string property)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(body);

            return node?[property]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // The property exists but is not a string — a number id, say. Not ours to interpret.
            return null;
        }
    }

    private Result<T> Failure<T>(string what, HttpStatusCode statusCode, string body)
    {
        // Provider detail is logged truncated and never relayed to the user (§38).
        logger.LogWarning(
            "{Endpoint} rejected the submission with {StatusCode}: {Body}",
            what,
            (int)statusCode,
            body.Length > 500 ? body[..500] : body);

        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Result.Failure<T>(
                ErrorCodes.Unauthorized,
                "The expense system refused this submission for this user."),

            HttpStatusCode.Conflict => Result.Failure<T>(
                ErrorCodes.Conflict,
                "The expense system reports this receipt already exists."),

            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => Result.Failure<T>(
                ErrorCodes.Validation,
                "The expense system rejected these details. Please check them and try again."),

            _ => Result.Failure<T>(
                ErrorCodes.ExternalApiFailed,
                "The expense system could not accept the receipt. It can be retried."),
        };
    }
}
