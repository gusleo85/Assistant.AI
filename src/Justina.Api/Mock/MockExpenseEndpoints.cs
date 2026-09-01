using System.Text.Json.Nodes;
using Justina.Expense.Infrastructure.MockData;

namespace Justina.Api.Mock;

/// <summary>
/// A stand-in for the Expense and Membership endpoints Justina needs but which do not exist yet.
///
/// It is here so the whole path can be exercised for real — payloads are built, serialized,
/// authenticated, sent and parsed, and identifiers come back — without touching the Expense system.
/// Everything upstream of it (validation, the state machine, idempotency, the confirmation gate) is the
/// production code, not a test double. Only the far end is fake.
///
/// Catalogue and identity are served from the same embedded fixtures the stub seams deserialize, so Mock
/// and Stub cannot disagree about which categories, taxes, currencies or members exist.
///
/// Mounted only when the submission seam is in Mock, and startup refuses Mock in Production: a mock that
/// quietly accepted real submissions would tell people their expenses had been filed when nothing had.
/// </summary>
public static class MockExpenseEndpoints
{
    private const string SystemTokenHeader = "Authorization";

    public static IEndpointRouteBuilder MapMockExpenseApi(this IEndpointRouteBuilder app)
    {
        var expense = app.MapGroup("/mock/expense/v1");

        expense.MapPost("/Receipt/chat/scan", ChatScan);

        // The catalogue the model's answers are matched against. The organizationId is in the route
        // because these lists are per-company; the mock checks it is present but serves one company.
        expense.MapGet("/Categories/list/{organizationId}", (string organizationId, HttpContext http) =>
            Catalogue(http, organizationId, MockDataResources.Categories));

        expense.MapGet("/Taxes/list/{organizationId}", (string organizationId, HttpContext http) =>
            Catalogue(http, organizationId, MockDataResources.Taxes));

        expense.MapGet("/Currencies/list/{organizationId}", (string organizationId, HttpContext http) =>
            Catalogue(http, organizationId, MockDataResources.Currencies));

        var membership = app.MapGroup("/mock/membership/v1");

        // Who the channel user is, and which company they belong to.
        membership.MapGet("/Member/info", MemberInfo);
        membership.MapGet("/Member/list/{organizationId}", (string organizationId, HttpContext http) =>
            Catalogue(http, organizationId, MockDataResources.Members));
        membership.MapGet("/Organization/{organizationId}", (string organizationId, HttpContext http) =>
            Catalogue(http, organizationId, MockDataResources.Organization));

        return app;
    }

    private static IResult ChatScan(JsonObject payload, HttpContext http, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MockExpenseApi");

        var unauthorized = RequireSystemToken(http, logger, "Receipt/chat/scan");

        if (unauthorized is not null)
        {
            return unauthorized;
        }

        var organizationId = payload["organizationId"]?.GetValue<string>();
        var memberId = payload["memberId"]?.GetValue<string>();

        // Both identify who the expense is filed for. Without them the real system could not place it,
        // so the mock refuses rather than inventing a default.
        if (string.IsNullOrWhiteSpace(organizationId) || string.IsNullOrWhiteSpace(memberId))
        {
            logger.LogWarning("Mock Receipt/chat/scan called without an organizationId or memberId");

            return Results.Json(
                new { message = "organizationId and memberId are required." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var receiptId = Guid.NewGuid().ToString();

        // Logged in full: this is a mock, and seeing the exact payload is the entire point of it.
        logger.LogInformation(
            "MOCK Receipt/chat/scan accepted a receipt for organization {OrganizationId} member {MemberId} "
            + "as {ReceiptId}. NOTHING WAS SENT TO THE EXPENSE SYSTEM. Payload: {Payload}",
            organizationId,
            memberId,
            receiptId,
            payload.ToJsonString());

        return Results.Ok(new
        {
            receiptId,
            status = "Scanned",
            organizationId,
            memberId,
            mock = true,
        });
    }

    /// <summary>
    /// Serves one embedded fixture verbatim. Returning the raw JSON rather than reserializing keeps the
    /// bytes identical to what the stub seams read — including field order and decimal scale, which
    /// matters because a tax label like "GST (9.00%)" is the key a model's answer is matched against.
    /// </summary>
    private static IResult Catalogue(HttpContext http, string organizationId, string fileName)
    {
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MockExpenseApi");

        var unauthorized = RequireSystemToken(http, logger, fileName);

        if (unauthorized is not null)
        {
            return unauthorized;
        }

        if (string.IsNullOrWhiteSpace(organizationId))
        {
            return Results.Json(
                new { message = "organizationId is required." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var json = MockDataResources.Read(fileName);

        if (json is null)
        {
            logger.LogError("Mock fixture {FileName} is not embedded", fileName);

            return Results.Json(
                new { message = $"No mock data for {fileName}." },
                statusCode: StatusCodes.Status404NotFound);
        }

        logger.LogInformation(
            "MOCK served {FileName} for organization {OrganizationId} — fixture data, not the Expense API",
            fileName,
            organizationId);

        return Results.Content(json, "application/json");
    }

    /// <summary>
    /// Resolves a channel identity to a member. The real membership API has no lookup by channel user id
    /// — that mapping is Justina's own — so the mock reads the same channel-link fixture the stub does.
    /// </summary>
    private static IResult MemberInfo(HttpContext http, string channel, string userId)
    {
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MockExpenseApi");

        var unauthorized = RequireSystemToken(http, logger, "Member/info");

        if (unauthorized is not null)
        {
            return unauthorized;
        }

        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(userId))
        {
            return Results.Json(
                new { message = "channel and userId are required." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var links = JsonNode.Parse(MockDataResources.Read(MockDataResources.ChannelLinks) ?? "[]")?.AsArray();
        var members = JsonNode.Parse(MockDataResources.Read(MockDataResources.Members) ?? "[]")?.AsArray();

        var link = links?.FirstOrDefault(node =>
            string.Equals(node?["channel"]?.GetValue<string>(), channel, StringComparison.OrdinalIgnoreCase)
            && string.Equals(node?["userId"]?.GetValue<string>(), userId, StringComparison.Ordinal));

        if (link is null)
        {
            // Not an error: an unlinked user is the normal state until someone links them, and the caller
            // turns this into "I do not know who you are" rather than a failure.
            logger.LogInformation(
                "MOCK Member/info found no link for {Channel} user {UserId}",
                channel,
                userId);

            return Results.Json(new { message = "No member is linked to that channel user." },
                statusCode: StatusCodes.Status404NotFound);
        }

        var memberId = link["memberId"]?.GetValue<string>();

        var member = members?.FirstOrDefault(node =>
            string.Equals(node?["id"]?.GetValue<string>(), memberId, StringComparison.OrdinalIgnoreCase));

        if (member is null)
        {
            logger.LogError("MOCK channel link points at member {MemberId}, which is not in the fixture", memberId);

            return Results.Json(new { message = "The linked member no longer exists." },
                statusCode: StatusCodes.Status404NotFound);
        }

        logger.LogInformation(
            "MOCK Member/info resolved {Channel} user {UserId} to member {MemberId} — fixture data",
            channel,
            userId,
            memberId);

        return Results.Content(member.ToJsonString(), "application/json");
    }

    /// <summary>
    /// The real endpoints authenticate with the system token, so the mock insists on one too. A missing
    /// credential should fail here, in development, rather than the first time it matters.
    /// </summary>
    private static IResult? RequireSystemToken(HttpContext http, ILogger logger, string what)
    {
        if (!string.IsNullOrWhiteSpace(http.Request.Headers[SystemTokenHeader].ToString()))
        {
            return null;
        }

        logger.LogWarning("Mock {What} called without a system token", what);

        return Results.Json(
            new { message = "Missing system token." },
            statusCode: StatusCodes.Status401Unauthorized);
    }
}
