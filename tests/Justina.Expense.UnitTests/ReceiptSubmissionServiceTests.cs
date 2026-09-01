using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Observability;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Application.Receipts;
using Justina.Expense.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Justina.Expense.UnitTests;

public class ReceiptSubmissionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

    private readonly IExpenseApiClient _api = Substitute.For<IExpenseApiClient>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IExpenseTenantResolver _tenants = Substitute.For<IExpenseTenantResolver>();

    private static readonly ExpenseTenant Tenant = new(Guid.NewGuid(), "COMPANY-1", Guid.NewGuid());

    public ReceiptSubmissionServiceTests()
    {
        _clock.UtcNow.Returns(Now);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Result.Success());
        _tenants.ResolveAsync(Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Tenant));
    }

    private ReceiptSubmissionService CreateService() =>
        new(_api, _tenants, _unitOfWork, _clock, NullLogger<ReceiptSubmissionService>.Instance);

    private static RequestContext Context() =>
        new(
            new UserContext(Guid.NewGuid(), ChannelKind.Telegram, "user-1", "Test User", [Capabilities.ExpenseSubmit]),
            ChannelKind.Telegram,
            "conversation-1",
            CorrelationId.New());

    private static Receipt ConfirmedReceipt()
    {
        var receipt = Receipt.Create(Guid.NewGuid(), "media-1", null, Now);
        receipt.BeginExtraction(Now);
        receipt.CompleteExtraction(
            new ReceiptFields("Starbucks", new DateOnly(2026, 8, 31), "SGD", 12.50m, "Meals", "INV-1", 1.03m),
            [],
            Now);
        receipt.Confirm("user-1", Now);
        return receipt;
    }

    [Fact]
    public async Task A_successful_submission_records_the_external_expense_id()
    {
        _api.SubmitAsync(Arg.Any<ExpenseSubmission>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ExpenseSubmissionResult("EXP-1")));

        var receipt = ConfirmedReceipt();

        var result = await CreateService().SubmitAsync(receipt, Context(), default);

        result.IsSuccess.ShouldBeTrue();
        receipt.State.ShouldBe(ReceiptState.Submitted);
        receipt.ExternalExpenseId.ShouldBe("EXP-1");
    }

    /// <summary>
    /// Business rule 6: confirming twice must never create two expenses. An already-submitted receipt
    /// short-circuits before the API is touched at all (§33).
    /// </summary>
    [Fact]
    public async Task Submitting_an_already_submitted_receipt_does_not_call_the_api_again()
    {
        _api.SubmitAsync(Arg.Any<ExpenseSubmission>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ExpenseSubmissionResult("EXP-1")));

        var receipt = ConfirmedReceipt();
        var service = CreateService();

        await service.SubmitAsync(receipt, Context(), default);
        var second = await service.SubmitAsync(receipt, Context(), default);

        second.IsSuccess.ShouldBeTrue();
        second.Value.ExternalExpenseId.ShouldBe("EXP-1");
        await _api.Received(1).SubmitAsync(Arg.Any<ExpenseSubmission>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_submission_leaves_the_receipt_retryable()
    {
        _api.SubmitAsync(Arg.Any<ExpenseSubmission>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ExpenseSubmissionResult>(ErrorCodes.ExternalApiFailed, "timeout"));

        var receipt = ConfirmedReceipt();

        var result = await CreateService().SubmitAsync(receipt, Context(), default);

        result.IsFailure.ShouldBeTrue();
        receipt.State.ShouldBe(ReceiptState.SubmissionFailed);
        receipt.ExternalExpenseId.ShouldBeNull();
    }

    [Fact]
    public async Task An_incomplete_receipt_is_refused_before_the_api_is_called()
    {
        var receipt = Receipt.Create(Guid.NewGuid(), "media-1", null, Now);
        receipt.BeginExtraction(Now);
        receipt.CompleteExtraction(
            new ReceiptFields(null, new DateOnly(2026, 8, 31), "SGD", 12.50m, null, null, null),
            [],
            Now);
        receipt.Confirm("user-1", Now);

        var result = await CreateService().SubmitAsync(receipt, Context(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.Validation);
        await _api.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default);
    }

    [Fact]
    public async Task The_submission_carries_the_idempotency_key_and_correlation_id()
    {
        ExpenseSubmission? captured = null;

        _api.SubmitAsync(Arg.Do<ExpenseSubmission>(s => captured = s), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ExpenseSubmissionResult("EXP-1")));

        var receipt = ConfirmedReceipt();
        var context = Context();

        await CreateService().SubmitAsync(receipt, context, default);

        captured.ShouldNotBeNull();
        captured.IdempotencyKey.ShouldNotBeNullOrWhiteSpace();
        captured.CorrelationId.ShouldBe(context.CorrelationId);
        captured.Merchant.ShouldBe("Starbucks");
        captured.Amount.ShouldBe(12.50m);
    }

    /// <summary>
    /// The expense has to be filed against a company and a member. Both come from the channel identity,
    /// never from anything the caller states.
    /// </summary>
    [Fact]
    public async Task The_submission_carries_the_resolved_tenant()
    {
        ExpenseSubmission? captured = null;

        _api.SubmitAsync(Arg.Do<ExpenseSubmission>(s => captured = s), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new ExpenseSubmissionResult("EXP-1")));

        await CreateService().SubmitAsync(ConfirmedReceipt(), Context(), default);

        captured.ShouldNotBeNull();
        captured.Tenant.ShouldNotBeNull();
        captured.Tenant.OrganizationId.ShouldBe(Tenant.OrganizationId);
        captured.Tenant.MemberId.ShouldBe(Tenant.MemberId);
        captured.Tenant.CompanyId.ShouldBe("COMPANY-1");
    }

    /// <summary>
    /// Resolved before the state moves, so an unresolvable tenant leaves the receipt confirmed and
    /// retryable rather than stranded in SUBMITTING.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_tenant_refuses_before_the_receipt_leaves_Confirmed()
    {
        _tenants.ResolveAsync(Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ExpenseTenant>(ErrorCodes.NotFound, "no member"));

        var receipt = ConfirmedReceipt();

        var result = await CreateService().SubmitAsync(receipt, Context(), default);

        result.IsFailure.ShouldBeTrue();
        receipt.State.ShouldBe(ReceiptState.Confirmed);
        await _api.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default);
    }

    [Fact]
    public void The_idempotency_key_is_stable_for_the_same_receipt_content()
    {
        var receipt = ConfirmedReceipt();

        var first = ReceiptSubmissionService.BuildIdempotencyKey(receipt);
        var second = ReceiptSubmissionService.BuildIdempotencyKey(receipt);

        first.ShouldBe(second);
    }

    [Fact]
    public void Two_different_receipts_do_not_share_an_idempotency_key()
    {
        var first = ReceiptSubmissionService.BuildIdempotencyKey(ConfirmedReceipt());
        var second = ReceiptSubmissionService.BuildIdempotencyKey(ConfirmedReceipt());

        first.ShouldNotBe(second);
    }
}
