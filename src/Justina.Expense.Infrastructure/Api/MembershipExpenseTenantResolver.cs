using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using JustLogin.Identity.SDK.SystemToken.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Turns a channel identity into a JustLogin member by asking the membership API.
///
/// The person is on Telegram or WhatsApp and has no JustLogin session, so the only thing we know about
/// them is the channel's own user id. This is the call that turns that into a member and a company —
/// everything downstream, the catalogue, the company token, the expense itself, depends on its answer.
///
/// It authenticates with the system token rather than a company one: which company this person belongs
/// to is precisely what is being asked, so it cannot already be known.
///
/// The endpoint it calls is currently Justina's own stand-in, because no membership route maps a channel
/// id to a member yet (plan risk R12). The shape is the one a real route would have, so replacing it is
/// a base URL and nothing else.
/// </summary>
public sealed class MembershipExpenseTenantResolver(
    HttpClient httpClient,
    IOptions<ExpenseApiOptions> options,
    ISingletonAuthenticationClient systemTokens,
    ILogger<MembershipExpenseTenantResolver> logger)
    : IExpenseTenantResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ExpenseApiOptions _options = options.Value;

    public async Task<Result<ExpenseTenant>> ResolveAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = string.Format(
            CultureInfo.InvariantCulture,
            _options.MemberInfoPath,
            Uri.EscapeDataString(context.Channel.ToString()),
            Uri.EscapeDataString(context.User.UserId));

        try
        {
            var systemToken = await systemTokens.GetSystemToken(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(systemToken?.AccessToken))
            {
                logger.LogError("No system token, so {Channel} user cannot be resolved", context.Channel);

                return Result.Failure<ExpenseTenant>(
                    ErrorCodes.ExternalApiFailed,
                    "I could not sign in to the expense system just now.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            ExpenseApiAuthorization.Apply(request, _options, systemToken.AccessToken);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Not an error: an unlinked user is the ordinary state until someone links them, and the
                // person should be told that rather than shown a failure.
                logger.LogInformation(
                    "Membership knows no member for {Channel} user {UserId}",
                    context.Channel,
                    context.User.UserId);

                return Result.Failure<ExpenseTenant>(
                    ErrorCodes.NotAvailable,
                    "This conversation is not linked to an expense account yet.");
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Membership returned {StatusCode} resolving {Channel} user {UserId}",
                    (int)response.StatusCode,
                    context.Channel,
                    context.User.UserId);

                return Result.Failure<ExpenseTenant>(
                    ErrorCodes.ExternalApiFailed,
                    "I could not look up your expense account just now.");
            }

            var member = await response.Content
                .ReadFromJsonAsync<MemberInfoResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return Build(member, context);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not resolve {Channel} user {UserId} to a member",
                context.Channel,
                context.User.UserId);

            return Result.Failure<ExpenseTenant>(
                ErrorCodes.ExternalApiFailed,
                "I could not look up your expense account just now.");
        }
    }

    private Result<ExpenseTenant> Build(MemberInfoResponse? member, RequestContext context)
    {
        if (member is null || member.Id == Guid.Empty || member.OrganizationId == Guid.Empty)
        {
            logger.LogError(
                "Membership answered for {Channel} user {UserId} without a member or organization id",
                context.Channel,
                context.User.UserId);

            return Result.Failure<ExpenseTenant>(
                ErrorCodes.ExternalApiFailed,
                "I could not look up your expense account just now.");
        }

        // CompanyId is the identity server's name for the company and the membership API is where it
        // comes from. Falling back to the GUID keeps older responses working; the company token path
        // resolves it properly through IJustLoginCompanyDirectory either way.
        var companyId = string.IsNullOrWhiteSpace(member.CompanyId)
            ? member.OrganizationId.ToString("N").ToUpperInvariant()
            : member.CompanyId;

        logger.LogInformation(
            "{Channel} user {UserId} resolved to member {MemberId} in organization {OrganizationId}",
            context.Channel,
            context.User.UserId,
            member.Id,
            member.OrganizationId);

        return Result.Success(new ExpenseTenant(member.OrganizationId, companyId, member.Id));
    }

    /// <summary>Only the fields that decide who this expense belongs to; the rest of the record is ignored.</summary>
    private sealed record MemberInfoResponse
    {
        [JsonPropertyName("id")]
        public Guid Id { get; init; }

        [JsonPropertyName("organizationId")]
        public Guid OrganizationId { get; init; }

        [JsonPropertyName("companyId")]
        public string? CompanyId { get; init; }
    }
}
