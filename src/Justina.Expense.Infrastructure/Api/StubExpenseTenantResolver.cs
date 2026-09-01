using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Resolves every conversation to one configured company while
/// <see cref="ExpenseApiOptions.Mode"/> is <c>Stub</c>.
///
/// The live resolver will look a member up by WhatsApp phone number, and for Telegram by a stored link
/// between the numeric Telegram user id and a JustLogin member — a Telegram update carries no phone
/// number. Neither contract exists yet, so this stands in for both.
/// </summary>
public sealed class StubExpenseTenantResolver(
    IOptions<ExpenseApiOptions> options,
    ILogger<StubExpenseTenantResolver> logger)
    : IExpenseTenantResolver
{
    private readonly ExpenseApiOptions _options = options.Value;

    public Task<Result<ExpenseTenant>> ResolveAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        logger.LogWarning(
            "Expense tenant resolution is running in STUB mode: {Channel} user resolved to the configured " +
            "organization {OrganizationId}, not to a real JustLogin member",
            context.Channel,
            _options.StubOrganizationId);

        var tenant = new ExpenseTenant(
            _options.StubOrganizationId,
            _options.StubCompanyId,
            _options.StubMemberId);

        return Task.FromResult(Result.Success(tenant));
    }
}
