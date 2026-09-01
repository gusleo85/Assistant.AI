using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Application.Session;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Commands;
using Justina.Expense.Application.Queries;
using Justina.Expense.Application.Receipts;
using Justina.Recruitment.Application;

namespace Justina.Api.Tools;

/// <summary>
/// The tool surface OpenClaw agents call (§16). Every endpoint is a thin translation between the tool
/// contract and a command or query — no business logic, and deliberately no receipt lookup either.
///
/// Resolving "the active receipt" happens inside the handlers, which run behind the authorization
/// decorator. Doing it here would run before authorization and let an unmapped caller learn whether a
/// conversation has a receipt in progress.
/// </summary>
public static class ToolEndpoints
{
    public static IEndpointRouteBuilder MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        var tools = app.MapGroup("/tools");

        tools.MapPost("/session.context", SessionContextAsync);
        tools.MapPost("/expense.receive_media", ReceiveMediaAsync);
        tools.MapPost("/expense.get_receipt", GetReceiptAsync);
        tools.MapPost("/expense.edit_receipt", EditReceiptAsync);
        tools.MapPost("/expense.confirm_receipt", ConfirmReceiptAsync);
        tools.MapPost("/expense.cancel_receipt", CancelReceiptAsync);
        tools.MapPost("/expense.retry_submission", RetrySubmissionAsync);
        tools.MapPost("/recruitment.search_candidates", SearchCandidatesAsync);

        return app;
    }

    private static async Task<IResult> SessionContextAsync(
        SessionContextRequest request,
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var context = await contexts.CreateAsync(request.Envelope, cancellationToken).ConfigureAwait(false);

        if (context.IsFailure)
        {
            return Respond(Result.Failure<SessionContext>(context.Error));
        }

        var result = await dispatcher
            .QueryAsync(new GetSessionContextQuery(context.Value), cancellationToken)
            .ConfigureAwait(false);

        return Respond(result);
    }

    /// <summary>
    /// Registers inbound media and immediately extracts it, so one agent tool call produces something the
    /// user can review. Deduplication happens first: a retried webhook must not create a second receipt (§33).
    /// </summary>
    private static async Task<IResult> ReceiveMediaAsync(
        ReceiveMediaRequest request,
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        IInboundMessageDeduplicator deduplicator,
        CancellationToken cancellationToken)
    {
        var context = await contexts.CreateAsync(request.Envelope, cancellationToken).ConfigureAwait(false);

        if (context.IsFailure)
        {
            return Respond(Result.Failure<ReceiptExtractionOutcome>(context.Error));
        }

        if (request.Media is null
            || (string.IsNullOrWhiteSpace(request.Media.MediaId) && string.IsNullOrWhiteSpace(request.Media.StagedPath)))
        {
            return Respond(Result.Failure<ReceiptExtractionOutcome>(
                ErrorCodes.Validation,
                "No media reference was supplied."));
        }

        // The identity of the document, for deduplication: whichever the caller actually has.
        var messageId = new[] { request.Envelope.MessageId, request.Media.StagedPath, request.Media.MediaId }
            .First(value => !string.IsNullOrWhiteSpace(value))!;

        var isNew = await deduplicator
            .TryRegisterAsync(context.Value.Channel, messageId, cancellationToken)
            .ConfigureAwait(false);

        if (!isNew)
        {
            // Already handled. Return what exists rather than processing the same document twice — through
            // a capability-checked query, so a repeat from an unauthorized caller is still refused.
            var existing = await dispatcher
                .QueryAsync(new GetActiveExtractionQuery(context.Value), cancellationToken)
                .ConfigureAwait(false);

            return Respond(existing);
        }

        var media = new MediaReference(
            request.Media.MediaId ?? messageId,
            request.Media.MimeType ?? "application/octet-stream",
            request.Media.FileName,
            request.Media.SizeBytes);

        var received = await dispatcher
            .SendAsync(
                new ReceiveReceiptCommand(context.Value, media, messageId, request.Media.StagedPath),
                cancellationToken)
            .ConfigureAwait(false);

        if (received.IsFailure)
        {
            return Respond(Result.Failure<ReceiptExtractionOutcome>(received.Error));
        }

        var extracted = await dispatcher
            .SendAsync(new ExtractReceiptCommand(context.Value, received.Value.ReceiptId), cancellationToken)
            .ConfigureAwait(false);

        return Respond(extracted);
    }

    private static async Task<IResult> GetReceiptAsync(
        GetReceiptRequest request,
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var context = await contexts.CreateAsync(request.Envelope, cancellationToken).ConfigureAwait(false);

        if (context.IsFailure)
        {
            return Respond(Result.Failure<ReceiptSnapshot>(context.Error));
        }

        var result = await dispatcher
            .QueryAsync(new GetReceiptQuery(context.Value, request.ReceiptId), cancellationToken)
            .ConfigureAwait(false);

        return Respond(result);
    }

    private static async Task<IResult> EditReceiptAsync(
        EditReceiptRequest request,
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var context = await contexts.CreateAsync(request.Envelope, cancellationToken).ConfigureAwait(false);

        if (context.IsFailure)
        {
            return Respond(Result.Failure<ReceiptSnapshot>(context.Error));
        }

        var edits = (request.Edits ?? [])
            .Select(e => new ReceiptEditRequest(e.Field, e.Value))
            .ToList();

        var result = await dispatcher
            .SendAsync(new UpdateReceiptCommand(context.Value, request.ReceiptId, edits), cancellationToken)
            .ConfigureAwait(false);

        return Respond(result);
    }

    private static async Task<IResult> ConfirmReceiptAsync(
        ConfirmReceiptRequest request,
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var context = await contexts.CreateAsync(request.Envelope, cancellationToken).ConfigureAwait(false);

        if (context.IsFailure)
        {
            return Respond(Result.Failure<ReceiptSnapshot>(context.Error));
        }

        var result = await dispatcher
            .SendAsync(new ConfirmReceiptCommand(context.Value, request.ReceiptId), cancellationToken)
            .ConfigureAwait(false);

        return Respond(result);
    }

    private static async Task<IResult> CancelReceiptAsync(
        CancelReceiptRequest request,
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var context = await contexts.CreateAsync(request.Envelope, cancellationToken).ConfigureAwait(false);

        if (context.IsFailure)
        {
            return Respond(Result.Failure<ReceiptSnapshot>(context.Error));
        }

        var result = await dispatcher
            .SendAsync(new CancelReceiptCommand(context.Value, request.ReceiptId), cancellationToken)
            .ConfigureAwait(false);

        return Respond(result);
    }

    /// <summary>
    /// Retries a submission that failed after confirmation. The user is not asked to confirm again —
    /// they already did, and the idempotency key is unchanged, so this cannot create a second expense.
    /// </summary>
    private static async Task<IResult> RetrySubmissionAsync(
        RetrySubmissionRequest request,
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var context = await contexts.CreateAsync(request.Envelope, cancellationToken).ConfigureAwait(false);

        if (context.IsFailure)
        {
            return Respond(Result.Failure<ReceiptSnapshot>(context.Error));
        }

        var result = await dispatcher
            .SendAsync(new SubmitExpenseCommand(context.Value, request.ReceiptId), cancellationToken)
            .ConfigureAwait(false);

        return Respond(result);
    }

    private static async Task<IResult> SearchCandidatesAsync(
        SearchCandidatesRequest request,
        RequestContextFactory contexts,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var context = await contexts.CreateAsync(request.Envelope, cancellationToken).ConfigureAwait(false);

        if (context.IsFailure)
        {
            return Respond(Result.Failure<CandidateSearchResult>(context.Error));
        }

        var result = await dispatcher
            .QueryAsync(
                new SearchCandidatesQuery(
                    context.Value,
                    request.Role,
                    request.Skills,
                    request.Seniority,
                    request.Location),
                cancellationToken)
            .ConfigureAwait(false);

        return Respond(result);
    }

    /// <summary>
    /// Refusals are returned as HTTP 200 with <c>ok: false</c> so the agent relays the reason verbatim.
    /// Only unauthorized is given a distinct status, so it is visible in access logs and metrics.
    /// </summary>
    private static IResult Respond<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Results.Ok(ToolResponse<T>.Success(result.Value));
        }

        var body = ToolResponse<T>.Failure(result.Error.Code, result.Error.Message);

        return result.Error.Code == ErrorCodes.Unauthorized
            ? Results.Json(body, statusCode: StatusCodes.Status403Forbidden)
            : Results.Ok(body);
    }
}
