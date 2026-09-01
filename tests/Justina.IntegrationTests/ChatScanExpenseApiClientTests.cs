using System.Net;
using System.Text;
using Justina.Core.Application.Abstractions;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Observability;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Infrastructure.Api;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Justina.IntegrationTests;

/// <summary>
/// The two-call chat submission: the image creates a receipt, the confirmed values are written onto it.
///
/// The gap between the calls is what these tests are really about. An expense exists after the first one
/// and carries no values until the second, so a failure in between must not turn one photo into two
/// claims.
/// </summary>
public class ChatScanExpenseApiClientTests : IDisposable
{
    private const string MediaId = "media-1";

    private static readonly Guid OrganizationId = Guid.Parse("1ba47eac-7ae7-4270-a3b8-a935f30c53ee");
    private static readonly Guid MemberId = Guid.Parse("4b07c8bf-dda4-40b7-8042-ceaea8ed3342");
    private static readonly Guid CategoryId = Guid.Parse("278c65ef-81a5-4508-bba0-d10b9ec176de");

    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_image_is_posted_then_the_values_are_written_onto_the_receipt()
    {
        GivenChatScanReturns("receipt-1");
        GivenUpdateReturns("expense-1");

        var result = await CreateClient().SubmitAsync(Submission(), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ExternalExpenseId.ShouldBe("expense-1");

        var scan = _server.LogEntries.Single(e => (e.RequestMessage!.Path ?? string.Empty).EndsWith("chat/scan", StringComparison.Ordinal));

        // The identity travels in the query string, and the image as multipart — the shape the real
        // endpoint's IFormFile parameter requires.
        var query = scan.RequestMessage!.Query!;
        query["organizationId"].ToString().ShouldContain(OrganizationId.ToString());
        query["memberId"].ToString().ShouldContain(MemberId.ToString());
        scan.RequestMessage!.Headers!["Content-Type"].ToString().ShouldContain("multipart/form-data");

        var update = _server.LogEntries.Single(e => (e.RequestMessage!.Path ?? string.Empty).EndsWith("Receipt/update", StringComparison.Ordinal));
        var body = update.RequestMessage!.Body ?? string.Empty;

        body.ShouldContain("receipt-1");
        body.ShouldContain("Ya Kun Kaya Toast");
        body.ShouldContain("20.40");
        body.ShouldContain("2023-06-19");
        body.ShouldContain(CategoryId.ToString());
    }

    [Fact]
    public async Task The_created_receipt_is_reported_before_the_values_are_written()
    {
        // Reported at this point precisely because the update may never return. Whoever is listening has
        // to learn the id even when the submission as a whole fails.
        GivenChatScanReturns("receipt-1");
        _server
            .Given(Request.Create().WithPath("/expense/v1/Receipt/update").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.InternalServerError));

        string? reported = null;

        var result = await CreateClient()
            .SubmitAsync(Submission() with { OnReceiptCreated = id => reported = id }, default);

        result.IsFailure.ShouldBeTrue();
        reported.ShouldBe("receipt-1");
    }

    [Fact]
    public async Task A_retry_writes_onto_the_receipt_the_first_attempt_created()
    {
        // The duplicate this whole design exists to prevent: starting over would post the image again,
        // and the user would find two identical expenses for one photo.
        GivenChatScanReturns("receipt-1");
        GivenUpdateReturns("expense-1");

        var result = await CreateClient()
            .SubmitAsync(Submission() with { ExternalReceiptId = "receipt-from-earlier" }, default);

        result.IsSuccess.ShouldBeTrue();

        _server.LogEntries.Any(e => (e.RequestMessage!.Path ?? string.Empty).EndsWith("chat/scan", StringComparison.Ordinal))
            .ShouldBeFalse("a receipt already existed, so the image must not be sent again");

        var update = _server.LogEntries.Single(e => (e.RequestMessage!.Path ?? string.Empty).EndsWith("Receipt/update", StringComparison.Ordinal));
        (update.RequestMessage!.Body ?? string.Empty).ShouldContain("receipt-from-earlier");
    }

    [Fact]
    public async Task A_receipt_whose_image_has_expired_is_refused_clearly()
    {
        // The media store is short-lived by design, and a receipt confirmed days later can outlive its
        // own photo. Better to say so than to send an empty file and let the API describe the problem.
        var client = CreateClient(new EmptyMediaStore());

        var result = await client.SubmitAsync(Submission(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.NotAvailable);
        _server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_submission_with_no_tenant_never_reaches_the_api()
    {
        var result = await CreateClient().SubmitAsync(Submission() with { Tenant = null }, default);

        result.IsFailure.ShouldBeTrue();
        _server.LogEntries.ShouldBeEmpty();
    }

    private void GivenChatScanReturns(string receiptId) =>
        _server
            .Given(Request.Create().WithPath("/expense/v1/Receipt/chat/scan").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"id":"{{receiptId}}","status":"ScanInProgress"}"""));

    private void GivenUpdateReturns(string expenseId) =>
        _server
            .Given(Request.Create().WithPath("/expense/v1/Receipt/update").UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"id":"receipt-1","expenseId":"{{expenseId}}","status":"ScanComplete"}"""));

    private ChatScanExpenseApiClient CreateClient(IMediaStore? mediaStore = null)
    {
        var options = new ExpenseApiOptions { BaseUrl = _server.Url! };

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/"),
            Timeout = TimeSpan.FromSeconds(10),
        };

        return new ChatScanExpenseApiClient(
            httpClient,
            Options.Create(options),
            new FakeAccessTokenProvider(),
            mediaStore ?? new InMemoryMediaStore(),
            NullLogger<ChatScanExpenseApiClient>.Instance);
    }

    private static ExpenseSubmission Submission() =>
        new(
            "Ya Kun Kaya Toast",
            new DateOnly(2023, 6, 19),
            "SGD",
            20.40m,
            "Meals and Entertainment",
            "8309",
            1.68m,
            [],
            "646882196",
            "idem-1",
            new CorrelationId(Guid.NewGuid().ToString("N")),
            CategoryId,
            [],
            null,
            null,
            new ExpenseTenant(OrganizationId, "khinco", MemberId),
            MediaId);

    private sealed class InMemoryMediaStore : IMediaStore
    {
        public Task<Result> SaveAsync(StoredMedia media, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<StoredMedia>> GetAsync(string mediaId, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(
                new StoredMedia(mediaId, Encoding.UTF8.GetBytes("not really a jpeg"), "image/jpeg", "receipt.jpg")));

        public Task<int> DeleteExpiredAsync(TimeSpan retention, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    private sealed class EmptyMediaStore : IMediaStore
    {
        public Task<Result> SaveAsync(StoredMedia media, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());

        public Task<Result<StoredMedia>> GetAsync(string mediaId, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Failure<StoredMedia>(ErrorCodes.NotFound, "Gone."));

        public Task<int> DeleteExpiredAsync(TimeSpan retention, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
