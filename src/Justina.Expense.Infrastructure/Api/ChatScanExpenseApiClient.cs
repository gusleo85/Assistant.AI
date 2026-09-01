using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Submits a chat-captured receipt to <c>Receipt/chat/scan</c>.
///
/// This endpoint does not exist in the Expense system yet — it is the shape Justina needs and the mock
/// in <c>Justina.Api</c> serves it. The distinction that matters: the existing scanner enriches a receipt
/// that already exists, whereas a receipt photographed in a chat has no prior record, so this call
/// creates one. Organization and member are stated explicitly because the caller is a service acting for
/// a person, not the person themselves.
///
/// Authenticated with the system token. When the real endpoint lands, only the payload mapping and the
/// response read should need to change.
/// </summary>
public sealed class ChatScanExpenseApiClient(
    HttpClient httpClient,
    IOptions<ExpenseApiOptions> options,
    IExpenseAccessTokenProvider tokens,
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

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ChatScanPath)
        {
            Content = JsonContent.Create(BuildPayload(submission)),
        };

        ExpenseApiAuthorization.Apply(request, _options, token.Value);
        request.Headers.TryAddWithoutValidation(_options.IdempotencyHeader, submission.IdempotencyKey);
        request.Headers.TryAddWithoutValidation(_options.CorrelationHeader, submission.CorrelationId.Value);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return Failure(response.StatusCode, body);
            }

            var receiptId = ReadReceiptId(body);

            if (string.IsNullOrWhiteSpace(receiptId))
            {
                logger.LogWarning("Receipt/chat/scan accepted the submission but returned no receipt id");

                return Result.Failure<ExpenseSubmissionResult>(
                    ErrorCodes.ExternalApiFailed,
                    "The expense system accepted the receipt but did not return a reference.");
            }

            return Result.Success(new ExpenseSubmissionResult(receiptId));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Receipt/chat/scan timed out");

            return Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.ExternalApiFailed,
                "The expense system did not respond in time. Your receipt is saved and can be retried.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Receipt/chat/scan could not be reached");

            return Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.ExternalApiFailed,
                "I could not reach the expense system. Your receipt is saved and can be retried.");
        }
    }

    /// <summary>
    /// Mirrors the scanner's own <c>ReceiptRequest</c> field names so the eventual real endpoint has as
    /// little to reconcile as possible. Ids that came from the catalogue travel beside the names they
    /// resolved from — the name is what a person reads back, the id is what the system files against.
    /// </summary>
    private static JsonObject BuildPayload(ExpenseSubmission submission)
    {
        var tenant = submission.Tenant!;

        var taxIds = new JsonArray();

        foreach (var id in submission.TaxIds)
        {
            taxIds.Add(id.ToString());
        }

        var lineItems = new JsonArray();

        foreach (var item in submission.LineItems)
        {
            lineItems.Add(new JsonObject
            {
                ["description"] = item.Description,
                ["quantity"] = item.Quantity,
                ["unitPrice"] = item.UnitPrice,
                ["amount"] = item.Amount,
            });
        }

        return new JsonObject
        {
            ["organizationId"] = tenant.OrganizationId.ToString(),
            ["companyId"] = tenant.CompanyId,
            ["memberId"] = tenant.MemberId.ToString(),

            ["merchantName"] = submission.Merchant,
            ["referenceNumber"] = submission.ReceiptNumber,
            ["date"] = submission.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["amount"] = submission.Amount.ToString("0.00", CultureInfo.InvariantCulture),

            ["currencyCode"] = submission.Currency,
            ["currencyId"] = submission.CurrencyId?.ToString(),

            ["category"] = submission.Category,
            ["categoryId"] = submission.CategoryId?.ToString(),

            ["location"] = submission.Location,
            ["taxAmount"] = submission.TaxAmount,
            ["taxIds"] = taxIds,

            ["lineItems"] = lineItems,
            ["submittedBy"] = submission.SubmittedByUserId,
        };
    }

    private Result<ExpenseSubmissionResult> Failure(HttpStatusCode statusCode, string body)
    {
        // Provider detail is logged truncated and never relayed to the user (§38).
        logger.LogWarning(
            "Receipt/chat/scan rejected the submission with {StatusCode}: {Body}",
            (int)statusCode,
            body.Length > 500 ? body[..500] : body);

        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.Unauthorized,
                "The expense system refused this submission for this user."),

            HttpStatusCode.Conflict => Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.Conflict,
                "The expense system reports this receipt already exists."),

            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                Result.Failure<ExpenseSubmissionResult>(
                    ErrorCodes.Validation,
                    "The expense system rejected these details. Please check them and try again."),

            _ => Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.ExternalApiFailed,
                "The expense system could not accept the receipt. It can be retried."),
        };
    }

    private static string? ReadReceiptId(string body)
    {
        try
        {
            var root = JsonNode.Parse(body)?.AsObject();

            return root?["receiptId"]?.GetValue<string>()
                ?? root?["id"]?.GetValue<string>()
                ?? root?["data"]?["receiptId"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
