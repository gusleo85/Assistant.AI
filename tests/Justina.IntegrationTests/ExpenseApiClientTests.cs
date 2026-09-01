using System.Net;
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
/// Exercises the Expense API client against a stub, covering the failure modes the plan requires:
/// authentication, timeout, retry, server failure and an invalid response (§46).
///
/// The stub speaks the provisional contract described in <see cref="ExpenseApiClient"/>; when the real
/// specification arrives (plan risk R1) these expectations move with the mapping.
/// </summary>
public sealed class ExpenseApiClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    private static ExpenseSubmission Submission(string idempotencyKey = "key-1") =>
        new(
            "Starbucks",
            new DateOnly(2026, 8, 31),
            "SGD",
            12.50m,
            "Meals",
            "INV-12345",
            1.03m,
            [new ExpenseLineItem("Latte", 1, 12.50m, 12.50m)],
            "user-1",
            idempotencyKey,
            CorrelationId.From("corr-1"));

    private ExpenseApiClient CreateClient(Action<ExpenseApiOptions>? configure = null, TimeSpan? timeout = null)
    {
        var options = new ExpenseApiOptions
        {
            BaseUrl = _server.Url!,
            ApiKey = "test-key",
            SubmitPath = "expenses",
        };

        configure?.Invoke(options);

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };

        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            options.ApiKeyHeader,
            $"{options.ApiKeyPrefix}{options.ApiKey}");

        return new ExpenseApiClient(
            httpClient,
            Options.Create(options),
            new FakeAccessTokenProvider(),
            NullLogger<ExpenseApiClient>.Instance);
    }

    [Fact]
    public async Task A_successful_submission_returns_the_external_expense_id()
    {
        _server
            .Given(Request.Create().WithPath("/expenses").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.Created)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"id":"EXP-9001"}"""));

        var result = await CreateClient().SubmitAsync(Submission(), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ExternalExpenseId.ShouldBe("EXP-9001");
    }

    [Fact]
    public async Task The_request_carries_the_credentials_the_idempotency_key_and_the_correlation_id()
    {
        _server
            .Given(Request.Create().WithPath("/expenses").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"id":"EXP-1"}"""));

        await CreateClient().SubmitAsync(Submission("stable-key"), default);

        var request = _server.LogEntries.Single().RequestMessage!;
        var headers = request.Headers!;

        string Header(string name) => string.Join(',', headers[name]);

        Header("Authorization").ShouldBe("Bearer test-key");
        Header("Idempotency-Key").ShouldBe("stable-key");
        Header("X-Correlation-Id").ShouldBe("corr-1");

        var body = request.Body!;
        body.ShouldContain("Starbucks");
        body.ShouldContain("2026-08-31");
    }

    [Fact]
    public async Task A_rejected_credential_is_reported_as_unauthorized()
    {
        _server
            .Given(Request.Create().WithPath("/expenses").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Unauthorized));

        var result = await CreateClient().SubmitAsync(Submission(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task A_validation_rejection_is_reported_as_a_validation_failure()
    {
        _server
            .Given(Request.Create().WithPath("/expenses").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.BadRequest).WithBody("bad currency"));

        var result = await CreateClient().SubmitAsync(Submission(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact]
    public async Task A_conflict_is_reported_as_a_conflict_rather_than_a_generic_failure()
    {
        _server
            .Given(Request.Create().WithPath("/expenses").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Conflict));

        var result = await CreateClient().SubmitAsync(Submission(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.Conflict);
    }

    [Fact]
    public async Task A_server_failure_is_retryable_and_never_leaks_provider_detail_to_the_user()
    {
        _server
            .Given(Request.Create().WithPath("/expenses").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.InternalServerError)
                .WithBody("stack trace with internal detail"));

        var result = await CreateClient().SubmitAsync(Submission(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.ExternalApiFailed);
        result.Error.Message.ShouldNotContain("stack trace");
        result.Error.Message.ShouldContain("retried");
    }

    [Fact]
    public async Task A_timeout_is_reported_as_retryable_and_the_receipt_is_not_lost()
    {
        _server
            .Given(Request.Create().WithPath("/expenses").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(3)));

        var result = await CreateClient(timeout: TimeSpan.FromMilliseconds(300)).SubmitAsync(Submission(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.ExternalApiFailed);
        result.Error.Message.ShouldContain("retried");
    }

    [Fact]
    public async Task A_success_without_an_expense_id_is_treated_as_a_failure()
    {
        _server
            .Given(Request.Create().WithPath("/expenses").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"status":"accepted"}"""));

        var result = await CreateClient().SubmitAsync(Submission(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.ExternalApiFailed);
    }

    [Fact]
    public async Task An_unparseable_success_body_is_treated_as_a_failure()
    {
        _server
            .Given(Request.Create().WithPath("/expenses").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("<html>not json</html>"));

        var result = await CreateClient().SubmitAsync(Submission(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.ExternalApiFailed);
    }

    [Fact]
    public async Task An_unconfigured_expense_api_refuses_instead_of_calling_an_empty_address()
    {
        var client = CreateClient(o => o.BaseUrl = _server.Url!);
        var unconfigured = new ExpenseApiClient(
            new HttpClient(),
            Options.Create(new ExpenseApiOptions { BaseUrl = string.Empty }),
            new FakeAccessTokenProvider(),
            NullLogger<ExpenseApiClient>.Instance);

        client.ShouldNotBeNull();

        var result = await unconfigured.SubmitAsync(Submission(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.NotAvailable);
    }

    public void Dispose() => _server.Dispose();
}
