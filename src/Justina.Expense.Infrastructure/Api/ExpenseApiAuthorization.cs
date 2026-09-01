using Justina.Expense.Application.Abstractions;

namespace Justina.Expense.Infrastructure.Api;

/// <summary>
/// Puts the company credential on one outbound request.
///
/// Set on the request rather than on the <see cref="HttpClient"/>: a client is shared by every company
/// the process serves, and a default header would be a company's token sitting on a connection any other
/// company's request could pick up. A per-request header cannot be got wrong that way — and it also
/// takes precedence over the static <c>ExpenseApi:ApiKey</c> default, which is what lets the mock seams
/// keep working unchanged.
/// </summary>
internal static class ExpenseApiAuthorization
{
    public static void Apply(HttpRequestMessage request, ExpenseApiOptions options, string token)
    {
        request.Headers.Remove(options.ApiKeyHeader);

        request.Headers.TryAddWithoutValidation(
            options.ApiKeyHeader,
            $"{options.ApiKeyPrefix}{token}");
    }
}
