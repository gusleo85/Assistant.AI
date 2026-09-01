using System.ComponentModel;
using System.Text.Json;
using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Application.Session;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Commands;
using Justina.Expense.Application.Queries;
using Justina.Expense.Application.Receipts;
using Justina.Recruitment.Application;
using ModelContextProtocol.Server;

namespace Justina.Api.Tools;

/// <summary>
/// The same tool surface as <see cref="ToolEndpoints"/>, exposed over MCP.
///
/// OpenClaw has no configuration for calling a plain HTTP JSON API — its only supported route to an
/// external tool is an MCP server. So this is the transport the agent layer actually uses; the REST
/// endpoints remain for testing and for any future client that is not MCP-based.
///
/// Both transports funnel into the identical commands and queries, so authorization, validation, state
/// and idempotency cannot diverge between them.
///
/// <para>
/// <b>Identity caveat.</b> MCP carries only static headers, so the agent supplies the channel identity as
/// tool arguments. Justina still resolves capabilities from the database rather than trusting any claim of
/// permission — but a compromised or misled agent could name a different user id. The gateway's own
/// channel allowlist is the first gate; this is defence in depth, not a substitute for it. Recorded as a
/// known limitation for the Product Owner.
/// </para>
/// </summary>
[McpServerToolType]
public static class JustinaMcpTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "justina_session_context", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Read who the user is, what they are allowed to do, and whether a workflow already owns this "
        + "conversation. Call this first on every turn before deciding which specialist should answer.")]
    public static Task<string> SessionContextAsync(
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        [Description("Channel the message arrived on: telegram or whatsapp.")] string channel,
        [Description("The channel's own numeric user id for the person speaking.")] string userId,
        [Description("The channel's own chat/conversation id.")] string conversationId,
        CancellationToken cancellationToken) =>
        RunAsync<SessionContext>(
            contexts,
            Envelope(channel, userId, conversationId),
            cancellationToken,
            context => dispatcher.QueryAsync(new GetSessionContextQuery(context), cancellationToken));

    [McpServerTool(Name = "justina_expense_receive_media", Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Register an image or PDF the user sent as a receipt and read it. Returns one or more extracted "
        + "receipts awaiting the user's confirmation. Never submits anything. If receiptCount is greater "
        + "than 1, ask the user before processing.")]
    public static async Task<string> ReceiveMediaAsync(
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        IInboundMessageDeduplicator deduplicator,
        [Description("Channel the message arrived on: telegram or whatsapp.")] string channel,
        [Description("The channel's own numeric user id for the person speaking.")] string userId,
        [Description("The channel's own chat/conversation id.")] string conversationId,
        [Description("The channel's identifier for the file the user sent.")] string mediaId,
        [Description("The file's declared MIME type, for example image/jpeg or application/pdf.")] string mimeType,
        CancellationToken cancellationToken,
        [Description("The file name, if the channel supplied one.")] string? fileName = null,
        [Description("The channel's id for this message, used to ignore retries.")] string? messageId = null)
    {
        var envelope = Envelope(channel, userId, conversationId, messageId);
        var context = await contexts.CreateAsync(envelope, cancellationToken).ConfigureAwait(false);

        if (context.IsFailure)
        {
            return Serialize(Result.Failure<ReceiptExtractionOutcome>(context.Error));
        }

        var dedupeKey = string.IsNullOrWhiteSpace(messageId) ? mediaId : messageId;

        var isNew = await deduplicator
            .TryRegisterAsync(context.Value.Channel, dedupeKey, cancellationToken)
            .ConfigureAwait(false);

        if (!isNew)
        {
            // Already handled: show what exists rather than reading the same document twice.
            var existing = await dispatcher
                .QueryAsync(new GetActiveExtractionQuery(context.Value), cancellationToken)
                .ConfigureAwait(false);

            return Serialize(existing);
        }

        var media = new MediaReference(mediaId, mimeType, fileName, 0);

        var received = await dispatcher
            .SendAsync(new ReceiveReceiptCommand(context.Value, media, dedupeKey), cancellationToken)
            .ConfigureAwait(false);

        if (received.IsFailure)
        {
            return Serialize(Result.Failure<ReceiptExtractionOutcome>(received.Error));
        }

        var extracted = await dispatcher
            .SendAsync(new ExtractReceiptCommand(context.Value, received.Value.ReceiptId), cancellationToken)
            .ConfigureAwait(false);

        return Serialize(extracted);
    }

    [McpServerTool(Name = "justina_expense_get_receipt", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Read the current state of a receipt so it can be shown to the user. Omit receiptId to read the "
        + "receipt this conversation is working on.")]
    public static Task<string> GetReceiptAsync(
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        [Description("Channel the message arrived on: telegram or whatsapp.")] string channel,
        [Description("The channel's own numeric user id for the person speaking.")] string userId,
        [Description("The channel's own chat/conversation id.")] string conversationId,
        CancellationToken cancellationToken,
        [Description("Optional receipt id. Omit for the receipt in progress.")] string? receiptId = null) =>
        RunAsync<ReceiptSnapshot>(
            contexts,
            Envelope(channel, userId, conversationId),
            cancellationToken,
            context => dispatcher.QueryAsync(
                new GetReceiptQuery(context, ParseId(receiptId)),
                cancellationToken));

    [McpServerTool(Name = "justina_expense_edit_receipt", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Change one or more fields the user corrected. Supply ONLY the fields they actually asked to "
        + "change. Field must be one of: merchant, date, currency, amount, category, receiptNumber, tax. "
        + "The full updated receipt is returned and must be shown again for confirmation.")]
    public static Task<string> EditReceiptAsync(
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        [Description("Channel the message arrived on: telegram or whatsapp.")] string channel,
        [Description("The channel's own numeric user id for the person speaking.")] string userId,
        [Description("The channel's own chat/conversation id.")] string conversationId,
        [Description("The field to change: merchant, date, currency, amount, category, receiptNumber or tax.")] string field,
        [Description("The new value, exactly as the user gave it. Justina parses and validates it.")] string value,
        CancellationToken cancellationToken,
        [Description("Optional receipt id. Omit for the receipt in progress.")] string? receiptId = null) =>
        RunAsync<ReceiptSnapshot>(
            contexts,
            Envelope(channel, userId, conversationId),
            cancellationToken,
            context => dispatcher.SendAsync(
                new UpdateReceiptCommand(context, ParseId(receiptId), [new ReceiptEditRequest(field, value)]),
                cancellationToken));

    [McpServerTool(Name = "justina_expense_confirm_receipt", Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Submit the receipt as an expense. Call this ONLY after the user has seen the extracted data and "
        + "explicitly confirmed it. Calling it twice is safe and will not create a second expense.")]
    public static Task<string> ConfirmReceiptAsync(
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        [Description("Channel the message arrived on: telegram or whatsapp.")] string channel,
        [Description("The channel's own numeric user id for the person speaking.")] string userId,
        [Description("The channel's own chat/conversation id.")] string conversationId,
        CancellationToken cancellationToken,
        [Description("Optional receipt id. Omit for the receipt in progress.")] string? receiptId = null) =>
        RunAsync<ReceiptSnapshot>(
            contexts,
            Envelope(channel, userId, conversationId),
            cancellationToken,
            context => dispatcher.SendAsync(
                new ConfirmReceiptCommand(context, ParseId(receiptId)),
                cancellationToken));

    [McpServerTool(Name = "justina_expense_cancel_receipt", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Discard the receipt in progress. Nothing is submitted.")]
    public static Task<string> CancelReceiptAsync(
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        [Description("Channel the message arrived on: telegram or whatsapp.")] string channel,
        [Description("The channel's own numeric user id for the person speaking.")] string userId,
        [Description("The channel's own chat/conversation id.")] string conversationId,
        CancellationToken cancellationToken,
        [Description("Optional receipt id. Omit for the receipt in progress.")] string? receiptId = null) =>
        RunAsync<ReceiptSnapshot>(
            contexts,
            Envelope(channel, userId, conversationId),
            cancellationToken,
            context => dispatcher.SendAsync(
                new CancelReceiptCommand(context, ParseId(receiptId)),
                cancellationToken));

    [McpServerTool(Name = "justina_expense_retry_submission", Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description(
        "Retry a submission that failed after the user already confirmed. Do NOT ask the user to confirm "
        + "again. Safe to call more than once; it cannot create a second expense.")]
    public static Task<string> RetrySubmissionAsync(
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        [Description("Channel the message arrived on: telegram or whatsapp.")] string channel,
        [Description("The channel's own numeric user id for the person speaking.")] string userId,
        [Description("The channel's own chat/conversation id.")] string conversationId,
        CancellationToken cancellationToken,
        [Description("Optional receipt id. Omit for the receipt in progress.")] string? receiptId = null) =>
        RunAsync<ReceiptSnapshot>(
            contexts,
            Envelope(channel, userId, conversationId),
            cancellationToken,
            context => dispatcher.SendAsync(
                new SubmitExpenseCommand(context, ParseId(receiptId)),
                cancellationToken));

    [McpServerTool(Name = "justina_recruitment_search_candidates", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description(
        "Search for candidates. Recruitment execution is not connected yet, so this currently reports that "
        + "the capability is unavailable. Never invent candidates.")]
    public static Task<string> SearchCandidatesAsync(
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        [Description("Channel the message arrived on: telegram or whatsapp.")] string channel,
        [Description("The channel's own numeric user id for the person speaking.")] string userId,
        [Description("The channel's own chat/conversation id.")] string conversationId,
        CancellationToken cancellationToken,
        [Description("The role being recruited for.")] string? role = null,
        [Description("Comma-separated skills.")] string? skills = null,
        [Description("Seniority, for example Senior.")] string? seniority = null,
        [Description("Location.")] string? location = null) =>
        RunAsync<CandidateSearchResult>(
            contexts,
            Envelope(channel, userId, conversationId),
            cancellationToken,
            context => dispatcher.QueryAsync(
                new SearchCandidatesQuery(
                    context,
                    role,
                    skills?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    seniority,
                    location),
                cancellationToken));

    private static ToolEnvelope Envelope(string channel, string userId, string conversationId, string? messageId = null) =>
        new(channel, userId, conversationId, messageId);

    private static Guid? ParseId(string? receiptId) =>
        Guid.TryParse(receiptId, out var parsed) ? parsed : null;

    private static async Task<string> RunAsync<T>(
        RequestContextFactory contexts,
        ToolEnvelope envelope,
        CancellationToken cancellationToken,
        Func<RequestContext, Task<Result<T>>> execute)
    {
        var context = await contexts.CreateAsync(envelope, cancellationToken).ConfigureAwait(false);

        if (context.IsFailure)
        {
            return Serialize(Result.Failure<T>(context.Error));
        }

        return Serialize(await execute(context.Value).ConfigureAwait(false));
    }

    /// <summary>
    /// The same <c>{ ok, data, error }</c> envelope the REST surface returns, so an agent reading either
    /// transport sees one contract — and a refusal always arrives as data it can relay, never as a fault.
    /// </summary>
    private static string Serialize<T>(Result<T> result) =>
        JsonSerializer.Serialize(
            result.IsSuccess
                ? ToolResponse<T>.Success(result.Value)
                : ToolResponse<T>.Failure(result.Error.Code, result.Error.Message),
            SerializerOptions);
}
