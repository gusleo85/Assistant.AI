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
/// Whether the Expense integration talks to JustLogin or to local stand-ins.
/// </summary>
public enum ExpenseApiMode
{
    /// <summary>
    /// Catalogue, submission and tenant resolution are all local stand-ins. Nothing leaves the process
    /// and no JustLogin credential is required. The default, because the live contracts are still open.
    /// </summary>
    Stub = 0,

    /// <summary>
    /// Speaks HTTP to a stand-in that serves the Expense contract — the mock in Justina.Api. Unlike
    /// Stub, the request really is built, serialized, authenticated and parsed, so the wiring is
    /// exercised end to end without touching the real system.
    /// </summary>
    Mock = 2,

    /// <summary>The real Expense API.</summary>
    Live = 1,
}

public sealed class ExpenseApiOptions
{
    public const string SectionName = "ExpenseApi";

    /// <summary>Key within <see cref="SectionName"/> selecting stub or live implementations.</summary>
    public const string ModeKey = "Mode";

    /// <summary>
    /// The default for every seam. Defaults to <see cref="ExpenseApiMode.Stub"/> so a missing
    /// configuration can never accidentally point a half-configured build at the real expense system.
    /// </summary>
    public ExpenseApiMode Mode { get; set; } = ExpenseApiMode.Stub;

    /// <summary>
    /// Per-seam overrides. Each falls back to <see cref="Mode"/> when unset, so the catalogue can be
    /// live against the real API while submission is still stubbed — which is the order these will
    /// actually go live in, since reading a category list needs far less than filing an expense does.
    /// </summary>
    public ExpenseApiMode? CatalogueMode { get; set; }

    public ExpenseApiMode? TenantMode { get; set; }

    public ExpenseApiMode? SubmissionMode { get; set; }

    /// <summary>
    /// Where the API credential comes from. Live mints a company token per company through JustLogin
    /// identity; anything else sends <see cref="ApiKey"/> as it stands.
    ///
    /// It is its own seam rather than following <see cref="Mode"/>, because the two are genuinely
    /// independent: the mock endpoints want the static key even while the catalogue is live, and a live
    /// submission against a real company needs a real token even while everything else is stubbed.
    /// </summary>
    public ExpenseApiMode? IdentityMode { get; set; }

    /// <summary>
    /// Where the company's <c>CompanyID</c> comes from — the identifier the token request needs and the
    /// only reason the membership API is in this flow. Stub reads the embedded fixture, which is exact
    /// for the one dev company; Live calls <c>membership/v2/companies/{guid}</c>.
    ///
    /// Separate from <see cref="IdentityMode"/> so a real identity server can mint real tokens while the
    /// company mapping is still served locally.
    /// </summary>
    public ExpenseApiMode? MembershipMode { get; set; }

    public ExpenseApiMode ResolvedCatalogueMode => CatalogueMode ?? Mode;

    /// <summary>Defaults to the static key, so no existing configuration changes behaviour.</summary>
    public ExpenseApiMode ResolvedIdentityMode => IdentityMode ?? ExpenseApiMode.Stub;

    public ExpenseApiMode ResolvedMembershipMode => MembershipMode ?? ExpenseApiMode.Stub;

    public ExpenseApiMode ResolvedTenantMode => TenantMode ?? Mode;

    public ExpenseApiMode ResolvedSubmissionMode => SubmissionMode ?? Mode;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Category list, with <c>{0}</c> replaced by the organization id. This route takes the organization
    /// in the path and never reads the token's company claim, so a system token is enough — no
    /// company-scoped token is needed to read a catalogue.
    /// </summary>
    public string CategoriesPath { get; set; } = "expense/v1/Categories/list/{0}";

    public string TaxesPath { get; set; } = "expense/v1/Taxes/list/{0}";

    public string CurrenciesPath { get; set; } = "expense/v1/Currencies/list/{0}";

    /// <summary>
    /// Creates a receipt captured in a chat. Does not exist in the Expense system yet — the mock in
    /// Justina.Api serves this shape so the whole path can be exercised before the real one lands.
    /// </summary>
    public string ChatScanPath { get; set; } = "expense/v1/Receipt/chat/scan";

    /// <summary>
    /// How long a company's catalogue is reused. The Lambda re-reads it per event because it is
    /// short-lived; Justina is long-lived and would otherwise re-read it for every photo.
    /// </summary>
    public int CatalogueCacheMinutes { get; set; } = 10;

    /// <summary>
    /// The organization a live deployment acts for while member lookup by phone or email does not exist
    /// (see plan risk R12). Required when the tenant seam is Live.
    ///
    /// Held as strings, not <see cref="Guid"/>: these arrive as environment variables that compose
    /// supplies as "" when unset, and binding "" to a Guid throws during startup rather than leaving the
    /// value empty. <see cref="OrganizationId"/> and <see cref="MemberId"/> do the parsing.
    /// </summary>
    public string ConfiguredOrganizationId { get; set; } = string.Empty;

    public string ConfiguredMemberId { get; set; } = string.Empty;

    public string ConfiguredCompanyId { get; set; } = string.Empty;

    public Guid? OrganizationId => Parse(ConfiguredOrganizationId);

    public Guid? MemberId => Parse(ConfiguredMemberId);

    private static Guid? Parse(string value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;

    /// <summary>From configuration only; never logged and never sent to the agent layer (§38).</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Header the API expects the key in. Configurable because the contract is not yet fixed.</summary>
    public string ApiKeyHeader { get; set; } = "Authorization";

    public string ApiKeyPrefix { get; set; } = "Bearer ";

    public string SubmitPath { get; set; } = "expenses";

    public string IdempotencyHeader { get; set; } = "Idempotency-Key";

    public string CorrelationHeader { get; set; } = "X-Correlation-Id";

    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// The only code that talks to the external Expense API (§31, §32).
///
/// PROVISIONAL CONTRACT — the real Expense API specification is not available yet (plan risk R1).
/// The wire shape below is Justina's assumption; when the specification arrives, only the mapping in
/// <see cref="BuildPayload"/> and <see cref="ReadExpenseId"/> changes. Everything above this class —
/// validation, state machine, idempotency, authorization — is unaffected by that change.
/// </summary>
public sealed class ExpenseApiClient(
    HttpClient httpClient,
    IOptions<ExpenseApiOptions> options,
    IExpenseAccessTokenProvider tokens,
    ILogger<ExpenseApiClient> logger)
    : IExpenseApiClient
{
    private readonly ExpenseApiOptions _options = options.Value;

    public async Task<Result<ExpenseSubmissionResult>> SubmitAsync(
        ExpenseSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            logger.LogError("The Expense API is not configured: no base URL supplied");

            return Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.NotAvailable,
                "Expense submission is not available right now.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.SubmitPath)
        {
            Content = JsonContent.Create(BuildPayload(submission)),
        };

        // Company-scoped credential, when we know which company. Without a tenant the request falls back
        // to the configured static key: this client predates tenant resolution and its contract is still
        // provisional (R1), so a missing tenant must not turn into a hard failure here.
        if (submission.Tenant is { } tenant)
        {
            var token = await tokens.GetAsync(tenant, cancellationToken).ConfigureAwait(false);

            if (token.IsFailure)
            {
                return Result.Failure<ExpenseSubmissionResult>(token.Error);
            }

            ExpenseApiAuthorization.Apply(request, _options, token.Value);
        }

        // The idempotency key travels with the request so a retry at any layer — ours, the network's, or
        // the API's own — resolves to the same expense (§33).
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

            var expenseId = ReadExpenseId(body);

            if (string.IsNullOrWhiteSpace(expenseId))
            {
                logger.LogWarning("The Expense API accepted the submission but returned no expense id");

                return Result.Failure<ExpenseSubmissionResult>(
                    ErrorCodes.ExternalApiFailed,
                    "The expense system accepted the receipt but did not return a reference.");
            }

            return Result.Success(new ExpenseSubmissionResult(expenseId));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The Expense API timed out");

            return Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.ExternalApiFailed,
                "The expense system did not respond in time. Your receipt is saved and can be retried.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "The Expense API could not be reached");

            return Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.ExternalApiFailed,
                "I could not reach the expense system. Your receipt is saved and can be retried.");
        }
    }

    private Result<ExpenseSubmissionResult> Failure(HttpStatusCode statusCode, string body)
    {
        // Provider detail is logged (truncated) but never relayed to the user (§38).
        logger.LogWarning(
            "The Expense API rejected the submission with {StatusCode}: {Body}",
            (int)statusCode,
            body.Length > 500 ? body[..500] : body);

        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.Unauthorized,
                "The expense system refused this submission for this user."),

            HttpStatusCode.Conflict => Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.Conflict,
                "The expense system reports this expense already exists."),

            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                Result.Failure<ExpenseSubmissionResult>(
                    ErrorCodes.Validation,
                    "The expense system rejected these details. Please check them and try again."),

            _ => Result.Failure<ExpenseSubmissionResult>(
                ErrorCodes.ExternalApiFailed,
                "The expense system could not accept the receipt. It can be retried."),
        };
    }

    private static JsonObject BuildPayload(ExpenseSubmission submission)
    {
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

        var taxIds = new JsonArray();

        foreach (var taxId in submission.TaxIds)
        {
            taxIds.Add(taxId);
        }

        return new JsonObject
        {
            ["merchant"] = submission.Merchant,
            ["date"] = submission.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["currency"] = submission.Currency,
            ["currencyId"] = submission.CurrencyId,
            ["amount"] = submission.Amount,
            ["category"] = submission.Category,
            ["categoryId"] = submission.CategoryId,
            ["receiptNumber"] = submission.ReceiptNumber,
            ["taxAmount"] = submission.TaxAmount,
            ["taxIds"] = taxIds,
            ["location"] = submission.Location,
            ["submittedBy"] = submission.SubmittedByUserId,
            ["lineItems"] = lineItems,
        };
    }

    private static string? ReadExpenseId(string body)
    {
        try
        {
            var root = JsonNode.Parse(body)?.AsObject();

            return root?["id"]?.GetValue<string>()
                ?? root?["expenseId"]?.GetValue<string>()
                ?? root?["data"]?["id"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
