using Microsoft.AspNetCore.Mvc;
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

        // The two calls a chat submission is made of: the image creates the receipt, the confirmed
        // values are written onto it. Both mirror the real endpoints, including that chat/scan takes
        // multipart form data and update takes JSON.
        expense.MapPost("/Receipt/chat/scan", ChatScan).DisableAntiforgery();
        expense.MapPut("/Receipt/update", ReceiptUpdate);

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

        // Stands in for the real membership API's own route, which is v2 and takes the 32-character
        // company GUID. It answers the one question the identity server's token request needs: which
        // CompanyID this company GUID is. Kept at the real path and version so pointing at the live
        // service is a base-URL change and nothing else.
        app.MapGet("/mock/membership/v2/companies/{companyGuid}", MembershipCompany);

        return app;
    }

    /// <summary>
    /// Stands in for <c>POST v1/Receipt/chat/scan</c>: the image arrives, a receipt id comes back.
    /// Multipart, like the real endpoint, because a mock that accepted JSON would let a client that
    /// cannot talk to the real thing look like it works.
    /// </summary>
    private static async Task<IResult> ChatScan(
        HttpRequest request,
        [FromQuery] string? organizationId,
        [FromQuery] string? memberId,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MockExpenseApi");

        var unauthorized = RequireSystemToken(request.HttpContext, logger, "Receipt/chat/scan");

        if (unauthorized is not null)
        {
            return unauthorized;
        }

        // Both identify who the expense is filed for. Without them the real system could not place it,
        // so the mock refuses rather than inventing a default.
        if (string.IsNullOrWhiteSpace(organizationId) || string.IsNullOrWhiteSpace(memberId))
        {
            logger.LogWarning("Mock Receipt/chat/scan called without an organizationId or memberId");

            return Results.Json(
                new { message = "organizationId and memberId are required." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!request.HasFormContentType)
        {
            return Results.Json(
                new { message = "Expected multipart form data with a file." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var form = await request.ReadFormAsync().ConfigureAwait(false);
        var file = form.Files["file"];

        if (file is null || file.Length == 0)
        {
            // The real endpoint creates the receipt from the photo. No photo, no receipt.
            logger.LogWarning("Mock Receipt/chat/scan called without a file");

            return Results.Json(
                new { message = "A file is required." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var receiptId = Guid.NewGuid().ToString();

        logger.LogInformation(
            "MOCK Receipt/chat/scan stored {Bytes} bytes of {ContentType} as receipt {ReceiptId} for "
            + "member {MemberId} in organization {OrganizationId}. NOTHING WAS SENT TO THE EXPENSE SYSTEM.",
            file.Length,
            file.ContentType,
            receiptId,
            memberId,
            organizationId);

        return Results.Ok(new
        {
            id = receiptId,
            status = "ScanInProgress",
            memberId,
            organizationId,
            mock = true,
        });
    }

    /// <summary>
    /// Stands in for <c>PUT v1/Receipt/update</c>: the confirmed values are written onto the receipt the
    /// previous call created. This is where the payload worth reading is, so this is where it is logged
    /// with every identifier resolved to its name.
    /// </summary>
    private static IResult ReceiptUpdate(JsonObject payload, HttpContext http, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("MockExpenseApi");

        var unauthorized = RequireSystemToken(http, logger, "Receipt/update");

        if (unauthorized is not null)
        {
            return unauthorized;
        }

        var receiptId = payload["receiptId"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(receiptId))
        {
            logger.LogWarning("Mock Receipt/update called without a receiptId");

            return Results.Json(
                new { message = "receiptId is required." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var expenseId = Guid.NewGuid().ToString();

        // Every id in the payload, resolved back to the name it stands for. Reading a log full of GUIDs
        // tells you nothing about whether the right category was chosen; reading "Meals and
        // Entertainment" tells you immediately.
        logger.LogInformation(
            "MOCK Receipt/update wrote receipt {ReceiptId} as expense {ExpenseId}. "
            + "NOTHING WAS SENT TO THE EXPENSE SYSTEM.\n{Summary}",
            receiptId,
            expenseId,
            Describe(payload));

        // The raw payload too, for anyone diffing against the real contract.
        logger.LogInformation("MOCK Receipt/update raw payload: {Payload}", payload.ToJsonString());

        return Results.Ok(new
        {
            id = receiptId,
            expenseId,
            status = "ScanComplete",
            mock = true,
        });
    }

    /// <summary>
    /// Renders the payload with every identifier resolved to the name it stands for.
    ///
    /// The point is reviewability: a reader needs to see that "Meals and Entertainment" was chosen and
    /// that it resolved to a real catalogue row — not a GUID they would have to look up by hand to know
    /// whether the mapping was right.
    /// </summary>
    private static string Describe(JsonObject payload)
    {
        var categories = Lookup(MockDataResources.Categories);
        var currencies = Lookup(MockDataResources.Currencies);
        var taxes = LookupTaxes();

        string? Text(string key) => payload[key]?.GetValue<string>();

        var lines = new List<string>
        {
            $"  receipt      : {Text("receiptId")}",
            $"  merchant     : {Text("merchantName")}",
            $"  reference    : {Text("referenceNumber")}",
            $"  date         : {Text("date")}",
            $"  amount       : {Text("amount")}",
            // Receipt/update carries no currency id — the API resolves the currency from the code — so
            // there is nothing to resolve here and saying "NO ID RESOLVED" would read like a defect.
            $"  currency     : {Text("currencyCode") ?? "(none)"}",
            $"  category     : {Describe(Text("categoryId"), categories, Text("category"))}",
            $"  location     : {Text("location") ?? "(none)"}",
            $"  tax amount   : {payload["taxAmount"]?.ToJsonString() ?? "(none)"}",
        };

        var taxIds = payload["taxIds"]?.AsArray();

        if (taxIds is null || taxIds.Count == 0)
        {
            lines.Add("  taxes        : (none matched)");
        }
        else
        {
            lines.Add("  taxes        :");
            lines.AddRange(taxIds.Select(id =>
                $"      - {Describe(id?.GetValue<string>(), taxes, null)}"));
        }

        var lineItems = payload["lineItems"]?.AsArray();

        if (lineItems is not null && lineItems.Count > 0)
        {
            lines.Add($"  line items   : {lineItems.Count}");
            lines.AddRange(lineItems.Select(item =>
                $"      - {item?["description"]?.GetValue<string>()} "
                + $"x{item?["quantity"]?.ToJsonString()} = {item?["amount"]?.ToJsonString()}"));
        }

        return string.Join('\n', lines);
    }

    /// <summary>An id beside the name it resolves to, or a clear note when it resolves to nothing.</summary>
    private static string Describe(string? id, IReadOnlyDictionary<string, string> names, string? fallbackName)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return fallbackName is null
                ? "(none)"
                : $"{fallbackName} — NO ID RESOLVED";
        }

        return names.TryGetValue(id, out var name)
            ? $"{name} [{id}]"
            : $"{fallbackName ?? "(unknown)"} [{id}] — ID NOT IN CATALOGUE";
    }

    /// <summary>Maps every catalogue id to its name, from the same fixture the catalogue endpoint serves.</summary>
    private static IReadOnlyDictionary<string, string> Lookup(string fileName)
    {
        var items = JsonNode.Parse(MockDataResources.Read(fileName) ?? "[]")?.AsArray();

        return items?
            .Where(node => node?["id"] is not null && node["name"] is not null)
            .ToDictionary(
                node => node!["id"]!.GetValue<string>(),
                node => node!["name"]!.GetValue<string>(),
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// A tax reads as "GST Yes 8 (8.00%)". The rate is part of the identity — two taxes can share a name
    /// and differ only by rate, so a name alone would not tell a reviewer which one was chosen.
    /// </summary>
    private static IReadOnlyDictionary<string, string> LookupTaxes()
    {
        var items = JsonNode.Parse(MockDataResources.Read(MockDataResources.Taxes) ?? "[]")?.AsArray();

        return items?
            .Where(node => node?["id"] is not null && node["name"] is not null)
            .ToDictionary(
                node => node!["id"]!.GetValue<string>(),
                node => $"{node!["name"]!.GetValue<string>()} ({node["attribute"]?.GetValue<string>()}%)",
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>();
    }

    private static string Organization() =>
        JsonNode.Parse(MockDataResources.Read(MockDataResources.Organization) ?? "{}")?["name"]
            ?.GetValue<string>() ?? "(unknown)";

    private static string Member(string memberId)
    {
        var members = JsonNode.Parse(MockDataResources.Read(MockDataResources.Members) ?? "[]")?.AsArray();

        var member = members?.FirstOrDefault(node =>
            string.Equals(node?["id"]?.GetValue<string>(), memberId, StringComparison.OrdinalIgnoreCase));

        return member?["fullName"]?.GetValue<string>()
            ?? member?["email"]?.GetValue<string>()
            ?? "(unknown)";
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

        // The member record carries the organization GUID; the identity server's token request needs the
        // CompanyID that goes with it. Both are answered here so one call turns a Telegram user id into
        // everything a company token needs — which is what the real membership API will do too.
        var enriched = member.DeepClone().AsObject();
        var company = MembershipCompanyRecord();

        if (company is not null
            && string.Equals(
                company["companyGuid"]?.GetValue<string>(),
                enriched["organizationId"]?.GetValue<string>()?.Replace("-", string.Empty, StringComparison.Ordinal),
                StringComparison.OrdinalIgnoreCase))
        {
            enriched["companyGuid"] = company["companyGuid"]?.GetValue<string>();
            enriched["companyId"] = company["companyId"]?.GetValue<string>();
            enriched["companyName"] = company["companyName"]?.GetValue<string>();
        }

        return Results.Content(enriched.ToJsonString(), "application/json");
    }

    /// <summary>
    /// The membership API's company record. It exists for <c>companyId</c>: the identity server's token
    /// request takes that, not the company GUID, and this mapping is the only reason the real membership
    /// call is in the token flow at all.
    /// </summary>
    private static IResult MembershipCompany(HttpContext http, string companyGuid)
    {
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MockExpenseApi");

        var unauthorized = RequireSystemToken(http, logger, "membership/v2/companies");

        if (unauthorized is not null)
        {
            return unauthorized;
        }

        var company = MembershipCompanyRecord();
        var known = company?["companyGuid"]?.GetValue<string>();

        if (company is null
            || !string.Equals(known, companyGuid, StringComparison.OrdinalIgnoreCase))
        {
            // One fixture, one company. Answering for any GUID would hand back another organization's
            // identifiers, which is how expenses end up filed against the wrong company.
            logger.LogWarning(
                "MOCK membership/v2/companies asked for {Requested}; the fixture describes {Known}",
                companyGuid,
                known ?? "nothing");

            return Results.Json(
                new { message = "No such company." },
                statusCode: StatusCodes.Status404NotFound);
        }

        if (string.IsNullOrWhiteSpace(company["companyId"]?.GetValue<string>()))
        {
            logger.LogWarning(
                "MOCK membership/v2/companies has no companyId for {CompanyGuid}; a company token cannot " +
                "be requested until membership-company.json is filled in",
                companyGuid);
        }

        return Results.Content(company.ToJsonString(), "application/json");
    }

    private static JsonObject? MembershipCompanyRecord()
    {
        var record = JsonNode.Parse(MockDataResources.Read(MockDataResources.MembershipCompany) ?? "{}")?.AsObject();

        // The fixture documents itself for whoever fills it in; the real endpoint has no such field, and
        // a mock that answers with more than the thing it imitates teaches callers the wrong shape.
        record?.Remove("_comment");

        return record;
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
