using System.Text.Json.Serialization;

namespace Justina.Api.Tools;

/// <summary>
/// Every tool call carries the same envelope: who is speaking, on which channel, in which conversation.
/// The agent supplies identity claims, but they are only ever *claims* — C# resolves them to a principal
/// and decides what that principal may do (§34).
/// </summary>
public sealed record ToolEnvelope(
    string Channel,
    string UserId,
    string ConversationId,
    string? MessageId = null,
    string? CorrelationId = null);

/// <summary>
/// Where the file is. <c>StagedPath</c> is the normal case — the AI gateway downloads inbound
/// attachments before the agent runs, so there is no channel media id left to fetch by.
/// </summary>
public sealed record MediaDto(
    string? MediaId = null,
    string? MimeType = null,
    string? FileName = null,
    long SizeBytes = 0,
    string? StagedPath = null);

public sealed record SessionContextRequest(ToolEnvelope Envelope);

public sealed record ReceiveMediaRequest(ToolEnvelope Envelope, MediaDto Media);

public sealed record GetReceiptRequest(ToolEnvelope Envelope, Guid? ReceiptId = null);

public sealed record EditDto(string Field, string Value);

public sealed record EditReceiptRequest(ToolEnvelope Envelope, IReadOnlyList<EditDto> Edits, Guid? ReceiptId = null);

public sealed record ConfirmReceiptRequest(ToolEnvelope Envelope, Guid? ReceiptId = null);

public sealed record CancelReceiptRequest(ToolEnvelope Envelope, Guid? ReceiptId = null);

public sealed record RetrySubmissionRequest(ToolEnvelope Envelope, Guid? ReceiptId = null);

public sealed record SearchCandidatesRequest(
    ToolEnvelope Envelope,
    string? Role = null,
    IReadOnlyList<string>? Skills = null,
    string? Seniority = null,
    string? Location = null);

public sealed record ToolError(string Code, string Message);

/// <summary>
/// A uniform tool response. A refusal is a successful HTTP call with <c>ok: false</c>, so the agent can
/// relay the reason to the user instead of treating it as a transport failure.
/// </summary>
public sealed record ToolResponse<T>(
    bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] T? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolError? Error)
{
    public static ToolResponse<T> Success(T data) => new(true, data, null);

    public static ToolResponse<T> Failure(string code, string message) => new(false, default, new ToolError(code, message));
}
