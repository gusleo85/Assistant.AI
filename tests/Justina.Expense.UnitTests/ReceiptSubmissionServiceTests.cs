using Justina.Core.Application.Abstractions;
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

    public ReceiptSubmissionServiceTests()
    {
        _clock.UtcNow.Returns(Now);
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    private ReceiptSubmissionService CreateService() =>
        new(_api, _unitOfWork, _clock, NullLogger<ReceiptSubmissionService>.Instance);

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

        var result = await CreateService().SubmitAsync(receipt, "user-1", CorrelationId.New(), default);

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

        await service.SubmitAsync(receipt, "user-1", CorrelationId.New(), default);
        var second = await service.SubmitAsync(receipt, "user-1", CorrelationId.New(), default);

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

        var result = await CreateService().SubmitAsync(receipt, "user-1", CorrelationId.New(), default);

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

        var result = await CreateService().SubmitAsync(receipt, "user-1", CorrelationId.New(), default);

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
        var correlation = CorrelationId.New();

        await CreateService().SubmitAsync(receipt, "user-1", correlation, default);

        captured.ShouldNotBeNull();
        captured.IdempotencyKey.ShouldNotBeNullOrWhiteSpace();
        captured.CorrelationId.ShouldBe(correlation);
        captured.Merchant.ShouldBe("Starbucks");
        captured.Amount.ShouldBe(12.50m);
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
