using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Resolves a channel identity to a JustLogin member from the embedded mock data, while
/// <see cref="ExpenseApiOptions.Mode"/> is <c>Stub</c>.
///
/// It mirrors the shape the live resolver will need rather than short-circuiting it: a channel identity
/// is looked up in a link table, and the member it finds carries the organization. Live, WhatsApp will
/// resolve by phone number and Telegram by exactly this kind of stored link — a Telegram update has no
/// phone number, and nothing in expense-api maps a phone or an email to a member.
/// </summary>
public sealed class StubExpenseTenantResolver(ILogger<StubExpenseTenantResolver> logger)
    : IExpenseTenantResolver
{
    public Task<Result<ExpenseTenant>> ResolveAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var directory = StubMemberDirectory.Current;
        var member = directory.Find(context.Channel, context.User.UserId);

        if (member is null)
        {
            member = directory.Default;

            if (member is null)
            {
                return Task.FromResult(Result.Failure<ExpenseTenant>(
                    ErrorCodes.NotAvailable,
                    "This conversation is not linked to an expense account yet."));
            }

            logger.LogWarning(
                "STUB tenant resolution: {Channel} user {UserId} is not linked to any member, falling " +
                "back to {FullName} ({MemberId}). Live, this would be a refusal until the user pairs.",
                context.Channel,
                context.User.UserId,
                member.FullName,
                member.Id);
        }
        else
        {
            logger.LogWarning(
                "STUB tenant resolution: {Channel} user {UserId} resolved to {FullName} ({MemberId}) in " +
                "organization {OrganizationId} from embedded mock data, not from JustLogin",
                context.Channel,
                context.User.UserId,
                member.FullName,
                member.Id,
                member.OrganizationId);
        }

        // CompanyId is the separate identifier the membership API returns and the token request sends as
        // its CompanyID form field. It comes from the membership fixture when that fixture describes
        // this company; otherwise the company GUID stands in, as it always has, and only a live token
        // request would notice the difference — IJustLoginCompanyDirectory resolves it properly there.
        var tenant = new ExpenseTenant(
            member.OrganizationId,
            StubMembershipCompany.CompanyIdFor(member.CompanyGuid) ?? member.CompanyGuid,
            member.Id);

        return Task.FromResult(Result.Success(tenant));
    }
}
