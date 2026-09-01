using Justina.Core.Domain;
using Justina.Expense.Domain;
using Shouldly;

namespace Justina.Expense.UnitTests;

public class ReceiptStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid ConversationId = Guid.NewGuid();

    private static readonly ReceiptFields Extracted = new(
        Merchant: "Starbucks",
        Date: new DateOnly(2026, 8, 31),
        Currency: "SGD",
        Amount: 12.50m,
        Category: "Meals",
        ReceiptNumber: "INV-12345",
        TaxAmount: 1.03m);

    private static Receipt NewReceipt() => Receipt.Create(ConversationId, "media-1", null, Now);

    private static Receipt WaitingConfirmation()
    {
        var receipt = NewReceipt();
        receipt.BeginExtraction(Now);
        receipt.CompleteExtraction(Extracted, [], Now);
        return receipt;
    }

    [Fact]
    public void Create_starts_in_Received_and_records_an_event()
    {
        var receipt = NewReceipt();

        receipt.State.ShouldBe(ReceiptState.Received);
        receipt.Events.ShouldHaveSingleItem().EventType.ShouldBe("Created");
    }

    [Fact]
    public void Happy_path_reaches_Submitted()
    {
        var receipt = WaitingConfirmation();

        receipt.State.ShouldBe(ReceiptState.WaitingConfirmation);
        receipt.Merchant.ShouldBe("Starbucks");
        receipt.Amount.ShouldBe(12.50m);

        receipt.Confirm("user-1", Now);
        receipt.State.ShouldBe(ReceiptState.Confirmed);

        receipt.BeginSubmission(Now);
        receipt.CompleteSubmission("EXP-999", Now);

        receipt.State.ShouldBe(ReceiptState.Submitted);
        receipt.ExternalExpenseId.ShouldBe("EXP-999");
        receipt.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void Extraction_failure_moves_to_ExtractionFailed()
    {
        var receipt = NewReceipt();
        receipt.BeginExtraction(Now);

        receipt.FailExtraction("unreadable", Now);

        receipt.State.ShouldBe(ReceiptState.ExtractionFailed);
        receipt.FailureReason.ShouldBe("unreadable");
    }

    [Fact]
    public void Submission_failure_is_retryable()
    {
        var receipt = WaitingConfirmation();
        receipt.Confirm("user-1", Now);
        receipt.BeginSubmission(Now);
        receipt.FailSubmission("timeout", Now);

        receipt.State.ShouldBe(ReceiptState.SubmissionFailed);

        receipt.BeginSubmission(Now);
        receipt.CompleteSubmission("EXP-1000", Now);

        receipt.State.ShouldBe(ReceiptState.Submitted);
    }

    [Fact]
    public void Edit_changes_only_the_requested_field_and_stays_awaiting_confirmation()
    {
        var receipt = WaitingConfirmation();

        receipt.ApplyChanges(
            [new ReceiptFieldChange { Field = ReceiptField.Amount, DecimalValue = 15.50m }],
            "user-1",
            Now);

        receipt.Amount.ShouldBe(15.50m);
        receipt.Merchant.ShouldBe("Starbucks");
        receipt.Currency.ShouldBe("SGD");
        receipt.ReceiptNumber.ShouldBe("INV-12345");
        receipt.State.ShouldBe(ReceiptState.WaitingConfirmation);
        receipt.Events.Last().EventType.ShouldBe("Edited");
    }

    [Fact]
    public void Edit_normalizes_currency_to_upper_case()
    {
        var receipt = WaitingConfirmation();

        receipt.ApplyChanges(
            [new ReceiptFieldChange { Field = ReceiptField.Currency, StringValue = "idr" }],
            "user-1",
            Now);

        receipt.Currency.ShouldBe("IDR");
    }

    [Fact]
    public void Edit_rejects_an_invalid_currency()
    {
        var receipt = WaitingConfirmation();

        Should.Throw<DomainException>(() => receipt.ApplyChanges(
            [new ReceiptFieldChange { Field = ReceiptField.Currency, StringValue = "Dollars" }],
            "user-1",
            Now));
    }

    [Fact]
    public void Edit_rejects_a_non_positive_amount()
    {
        var receipt = WaitingConfirmation();

        Should.Throw<DomainException>(() => receipt.ApplyChanges(
            [new ReceiptFieldChange { Field = ReceiptField.Amount, DecimalValue = 0m }],
            "user-1",
            Now));
    }

    [Fact]
    public void Edit_rejects_an_empty_change_set()
    {
        var receipt = WaitingConfirmation();

        Should.Throw<DomainException>(() => receipt.ApplyChanges([], "user-1", Now));
    }

    [Fact]
    public void Cancel_is_allowed_before_submission()
    {
        var receipt = WaitingConfirmation();

        receipt.Cancel("user-1", Now);

        receipt.State.ShouldBe(ReceiptState.Cancelled);
        receipt.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void Cancel_is_rejected_after_submission()
    {
        var receipt = WaitingConfirmation();
        receipt.Confirm("user-1", Now);
        receipt.BeginSubmission(Now);
        receipt.CompleteSubmission("EXP-1", Now);

        Should.Throw<ReceiptStateException>(() => receipt.Cancel("user-1", Now));
    }

    [Fact]
    public void Confirm_is_rejected_before_extraction_completes()
    {
        var receipt = NewReceipt();

        Should.Throw<ReceiptStateException>(() => receipt.Confirm("user-1", Now));
    }

    [Fact]
    public void Confirming_twice_is_rejected_by_the_state_machine()
    {
        var receipt = WaitingConfirmation();
        receipt.Confirm("user-1", Now);

        Should.Throw<ReceiptStateException>(() => receipt.Confirm("user-1", Now));
    }

    [Fact]
    public void Editing_after_confirmation_is_rejected()
    {
        var receipt = WaitingConfirmation();
        receipt.Confirm("user-1", Now);

        Should.Throw<ReceiptStateException>(() => receipt.ApplyChanges(
            [new ReceiptFieldChange { Field = ReceiptField.Amount, DecimalValue = 99m }],
            "user-1",
            Now));
    }

    [Fact]
    public void Extraction_cannot_be_started_twice()
    {
        var receipt = NewReceipt();
        receipt.BeginExtraction(Now);

        Should.Throw<ReceiptStateException>(() => receipt.BeginExtraction(Now));
    }

    [Fact]
    public void Submission_cannot_start_before_confirmation()
    {
        var receipt = WaitingConfirmation();

        Should.Throw<ReceiptStateException>(() => receipt.BeginSubmission(Now));
    }

    /// <summary>The reported name is relayed to the user, so it must read as a field, not a property.</summary>
    [Theory]
    [InlineData(null, "SGD", 12.50, "merchant")]
    [InlineData("Starbucks", null, 12.50, "currency")]
    [InlineData("Starbucks", "SGD", 0, "amount")]
    public void IsSubmittable_reports_the_first_missing_field(
        string? merchant,
        string? currency,
        double amount,
        string expectedMissing)
    {
        var receipt = NewReceipt();
        receipt.BeginExtraction(Now);
        receipt.CompleteExtraction(
            new ReceiptFields(merchant, new DateOnly(2026, 8, 31), currency, (decimal)amount, null, null, null),
            [],
            Now);

        receipt.IsSubmittable(out var missing).ShouldBeFalse();
        missing.ShouldBe(expectedMissing);
    }

    [Fact]
    public void IsSubmittable_accepts_a_complete_receipt()
    {
        var receipt = WaitingConfirmation();

        receipt.IsSubmittable(out var missing).ShouldBeTrue();
        missing.ShouldBeNull();
    }

    [Fact]
    public void A_batch_creates_independent_receipts_that_share_a_batch_id()
    {
        var batch = ReceiptBatch.Create(ConversationId, "media-multi", Now);

        var first = batch.AddReceipt(Now);
        var second = batch.AddReceipt(Now);
        var third = batch.AddReceipt(Now);

        batch.Count.ShouldBe(3);
        new[] { first.Id, second.Id, third.Id }.Distinct().Count().ShouldBe(3);
        batch.Receipts.ShouldAllBe(r => r.BatchId == batch.Id);
        batch.Receipts.ShouldAllBe(r => r.State == ReceiptState.Received);
    }

    /// <summary>
    /// Batch members are created in the same instant, so sequence — not the timestamp — is what makes
    /// "the next receipt" deterministic.
    /// </summary>
    [Fact]
    public void Receipts_in_a_batch_are_numbered_in_reading_order()
    {
        var batch = ReceiptBatch.Create(ConversationId, "media-multi", Now);

        batch.AddReceipt(Now);
        batch.AddReceipt(Now);
        batch.AddReceipt(Now);

        batch.Receipts.Select(r => r.SequenceInBatch).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void A_single_receipt_is_sequence_one()
    {
        NewReceipt().SequenceInBatch.ShouldBe(1);
    }

    /// <summary>
    /// A Vision failure must be retryable against the already-downloaded document, otherwise the user is
    /// told to send the file again for a problem that was never theirs.
    /// </summary>
    [Fact]
    public void Extraction_can_be_retried_after_it_failed()
    {
        var receipt = NewReceipt();
        receipt.BeginExtraction(Now);
        receipt.FailExtraction("vision_failed", Now);

        receipt.BeginExtraction(Now);

        receipt.State.ShouldBe(ReceiptState.Extracting);
        receipt.FailureReason.ShouldBeNull();

        receipt.CompleteExtraction(Extracted, [], Now);
        receipt.State.ShouldBe(ReceiptState.WaitingConfirmation);
    }

    [Fact]
    public void Extraction_cannot_be_retried_once_the_receipt_is_awaiting_confirmation()
    {
        var receipt = WaitingConfirmation();

        Should.Throw<ReceiptStateException>(() => receipt.BeginExtraction(Now));
    }

    [Fact]
    public void Attaching_to_a_batch_records_the_position_in_the_document()
    {
        var receipt = NewReceipt();
        receipt.BeginExtraction(Now);
        var batchId = Guid.NewGuid();

        receipt.AttachToBatch(batchId, 2, Now);

        receipt.BatchId.ShouldBe(batchId);
        receipt.SequenceInBatch.ShouldBe(2);
    }
}
