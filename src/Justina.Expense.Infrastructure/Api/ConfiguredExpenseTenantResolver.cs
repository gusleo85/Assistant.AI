using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Resolves every conversation to one configured organization and member.
///
/// This is the live tenant seam as it can honestly be built today: nothing in expense-api maps a phone
/// number or an email to a member (plan risk R12), and a Telegram update carries neither. Until that
/// lookup exists, a live deployment serves a single company, and its identifiers come from configuration
/// rather than from the conversation.
///
/// It is not a stub — the identifiers are real and the expenses it files are real. What it cannot do is
/// tell two people apart, which is why the values are required rather than defaulted.
/// </summary>
public sealed class ConfiguredExpenseTenantResolver(IOptions<ExpenseApiOptions> options)
    : IExpenseTenantResolver
{
    private readonly ExpenseApiOptions _options = options.Value;

    public Task<Result<ExpenseTenant>> ResolveAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_options.OrganizationId is not { } organizationId || _options.MemberId is not { } memberId)
        {
            return Task.FromResult(Result.Failure<ExpenseTenant>(
                ErrorCodes.NotAvailable,
                "Expenses are not available right now."));
        }

        var companyId = string.IsNullOrWhiteSpace(_options.ConfiguredCompanyId)
            ? organizationId.ToString("N").ToUpperInvariant()
            : _options.ConfiguredCompanyId;

        return Task.FromResult(Result.Success(new ExpenseTenant(organizationId, companyId, memberId)));
    }
}
