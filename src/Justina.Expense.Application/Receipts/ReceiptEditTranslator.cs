using Justina.Core.Domain.Results;
using Justina.Expense.Domain;

namespace Justina.Expense.Application.Receipts;

/// <summary>One requested edit as it arrives from the agent: a field name and a raw string value.</summary>
public sealed record ReceiptEditRequest(string Field, string Value);

/// <summary>
/// The AI decides *what* the user meant to change; this decides whether that change is legal (§10, §29).
/// An unknown field or an unparseable value is refused here, before the aggregate is touched.
/// </summary>
public static class ReceiptEditTranslator
{
    public static Result<IReadOnlyCollection<ReceiptFieldChange>> Translate(
        IReadOnlyCollection<ReceiptEditRequest> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);

        if (edits.Count == 0)
        {
            return Result.Failure<IReadOnlyCollection<ReceiptFieldChange>>(
                ErrorCodes.Validation,
                "No field changes were supplied.");
        }

        var changes = new List<ReceiptFieldChange>(edits.Count);
        var seen = new HashSet<ReceiptField>();

        foreach (var edit in edits)
        {
            if (!TryParseField(edit.Field, out var field))
            {
                return Result.Failure<IReadOnlyCollection<ReceiptFieldChange>>(
                    ErrorCodes.Validation,
                    $"'{edit.Field}' is not an editable receipt field.");
            }

            if (!seen.Add(field))
            {
                return Result.Failure<IReadOnlyCollection<ReceiptFieldChange>>(
                    ErrorCodes.Validation,
                    $"The field '{field}' was supplied more than once.");
            }

            var change = BuildChange(field, edit.Value);

            if (change.IsFailure)
            {
                return Result.Failure<IReadOnlyCollection<ReceiptFieldChange>>(change.Error);
            }

            changes.Add(change.Value);
        }

        return Result.Success<IReadOnlyCollection<ReceiptFieldChange>>(changes);
    }

    private static bool TryParseField(string? name, out ReceiptField field)
    {
        field = default;

        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Trim().ToLowerInvariant() switch
        {
            "merchant" or "vendor" or "store" => Set(ReceiptField.Merchant, out field),
            "date" or "receiptdate" or "receipt_date" => Set(ReceiptField.Date, out field),
            "currency" => Set(ReceiptField.Currency, out field),
            "amount" or "total" => Set(ReceiptField.Amount, out field),
            "category" => Set(ReceiptField.Category, out field),
            "receiptnumber" or "receipt_number" or "invoice" or "invoicenumber" =>
                Set(ReceiptField.ReceiptNumber, out field),
            "tax" or "taxamount" or "tax_amount" or "gst" or "vat" => Set(ReceiptField.TaxAmount, out field),
            _ => false,
        };

        static bool Set(ReceiptField value, out ReceiptField target)
        {
            target = value;
            return true;
        }
    }

    private static Result<ReceiptFieldChange> BuildChange(ReceiptField field, string? rawValue)
    {
        switch (field)
        {
            case ReceiptField.Merchant:
            case ReceiptField.Category:
            case ReceiptField.ReceiptNumber:
            {
                var text = ReceiptNormalizer.Text(rawValue);

                return text is null
                    ? Invalid(field, "a non-empty value")
                    : Result.Success(new ReceiptFieldChange { Field = field, StringValue = text });
            }

            case ReceiptField.Currency:
            {
                var currency = ReceiptNormalizer.Currency(rawValue);

                return currency is null
                    ? Invalid(field, "a three-letter ISO-4217 currency code, for example SGD")
                    : Result.Success(new ReceiptFieldChange { Field = field, StringValue = currency });
            }

            case ReceiptField.Date:
            {
                var date = ReceiptNormalizer.Date(rawValue);

                return date is null
                    ? Invalid(field, "a date, for example 2026-08-31")
                    : Result.Success(new ReceiptFieldChange { Field = field, DateValue = date });
            }

            case ReceiptField.Amount:
            {
                var amount = ReceiptNormalizer.PositiveAmount(rawValue);

                return amount is null
                    ? Invalid(field, "an amount greater than zero")
                    : Result.Success(new ReceiptFieldChange { Field = field, DecimalValue = amount });
            }

            case ReceiptField.TaxAmount:
            {
                var tax = ReceiptNormalizer.NonNegativeAmount(rawValue);

                return tax is null
                    ? Invalid(field, "a tax amount of zero or more")
                    : Result.Success(new ReceiptFieldChange { Field = field, DecimalValue = tax });
            }

            default:
                return Result.Failure<ReceiptFieldChange>(
                    ErrorCodes.Validation,
                    $"'{field}' cannot be edited.");
        }
    }

    private static Result<ReceiptFieldChange> Invalid(ReceiptField field, string expectation) =>
        Result.Failure<ReceiptFieldChange>(
            ErrorCodes.Validation,
            $"{field} needs {expectation}.");
}
