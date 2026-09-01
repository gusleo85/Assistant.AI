using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Observability;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Domain;
using Microsoft.Extensions.Logging;

namespace Justina.Expense.Application.Receipts;

/// <summary>
/// The single place a receipt becomes an external expense. Shared by the confirm and the retry paths so
/// the duplicate-prevention rules cannot drift apart between them (§33).
/// </summary>
public interface IReceiptSubmissionService
{
    Task<Result<ReceiptSnapshot>> SubmitAsync(
        Receipt receipt,
        RequestContext context,
        CancellationToken cancellationToken);
}

public sealed class ReceiptSubmissionService(
    IExpenseApiClient expenseApi,
    IExpenseTenantResolver tenants,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<ReceiptSubmissionService> logger)
    : IReceiptSubmissionService
{
    public async Task<Result<ReceiptSnapshot>> SubmitAsync(
        Receipt receipt,
        RequestContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        // Already submitted: return the original outcome rather than creating a second expense.
        if (receipt.State == ReceiptState.Submitted)
        {
            return Result.Success(ReceiptSnapshot.From(receipt));
        }

        if (!receipt.IsSubmittable(out var missingField))
        {
            return Result.Failure<ReceiptSnapshot>(
                ErrorCodes.Validation,
                $"This receipt is missing {missingField} and cannot be submitted yet.");
        }

        // Resolved before the state moves, so a tenant that cannot be resolved leaves the receipt
        // confirmed and retryable rather than stuck in SUBMITTING.
        var tenant = await tenants.ResolveAsync(context, cancellationToken).ConfigureAwait(false);

        if (tenant.IsFailure)
        {
            logger.LogWarning(
                "Could not resolve the expense tenant for receipt {ReceiptId}: {ErrorCode}",
                receipt.Id,
                tenant.Error.Code);

            return Result.Failure<ReceiptSnapshot>(tenant.Error);
        }

        receipt.BeginSubmission(clock.UtcNow);

        // Persist SUBMITTING before the external call so a crash mid-flight leaves a retryable state,
        // never a silently lost or silently duplicated submission.
        var checkpoint = await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (checkpoint.IsFailure)
        {
            return Result.Failure<ReceiptSnapshot>(checkpoint.Error);
        }

        var submission = BuildSubmission(receipt, context, tenant.Value);
        var response = await expenseApi.SubmitAsync(submission, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccess)
        {
            receipt.CompleteSubmission(response.Value.ExternalExpenseId, clock.UtcNow);
            logger.LogInformation(
                "Receipt {ReceiptId} submitted as expense {ExternalExpenseId}",
                receipt.Id,
                response.Value.ExternalExpenseId);
        }
        else
        {
            receipt.FailSubmission(response.Error.Code, clock.UtcNow);
            logger.LogWarning(
                "Submission of receipt {ReceiptId} failed with {ErrorCode}",
                receipt.Id,
                response.Error.Code);
        }

        var saved = await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (saved.IsFailure)
        {
            return Result.Failure<ReceiptSnapshot>(saved.Error);
        }

        return response.IsSuccess
            ? Result.Success(ReceiptSnapshot.From(receipt))
            : Result.Failure<ReceiptSnapshot>(response.Error);
    }

    private static ExpenseSubmission BuildSubmission(
        Receipt receipt,
        RequestContext context,
        ExpenseTenant tenant) =>
        new(
            receipt.Merchant!,
            receipt.ReceiptDate!.Value,
            receipt.Currency!,
            receipt.Amount!.Value,
            receipt.Category,
            receipt.ReceiptNumber,
            receipt.TaxAmount,
            receipt.LineItems
                .Select(i => new ExpenseLineItem(i.Description, i.Quantity, i.UnitPrice, i.Amount))
                .ToList(),
            context.User.UserId,
            BuildIdempotencyKey(receipt),
            context.CorrelationId,
            receipt.CategoryId,
            receipt.TaxIds,
            receipt.Location,
            receipt.CurrencyId,
            tenant);

    /// <summary>
    /// Deterministic from the receipt identity and its confirmed content, so a retry of the same
    /// submission carries the same key while a genuinely different receipt never collides.
    /// </summary>
    internal static string BuildIdempotencyKey(Receipt receipt)
    {
        var content = string.Join(
            '|',
            receipt.Id.ToString("N"),
            receipt.Merchant,
            receipt.ReceiptDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            receipt.Currency,
            receipt.Amount?.ToString("0.00", CultureInfo.InvariantCulture),
            receipt.ReceiptNumber,
            receipt.TaxAmount?.ToString("0.00", CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
