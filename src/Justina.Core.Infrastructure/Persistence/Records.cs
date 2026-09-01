using Justina.Core.Domain.Messaging;

namespace Justina.Core.Infrastructure.Persistence;

public sealed class ConversationRecord
{
    public Guid Id { get; set; }

    public ChannelKind Channel { get; set; }

    public string ExternalConversationId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string? ActiveWorkflow { get; set; }

    public Guid? ActiveEntityId { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>One row per inbound message. The unique index is the deduplication (§33).</summary>
public sealed class InboundMessageRecord
{
    public ChannelKind Channel { get; set; }

    public string MessageId { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAtUtc { get; set; }
}

public sealed class IdempotencyKeyRecord
{
    public string KeyValue { get; set; } = string.Empty;

    public string CommandType { get; set; } = string.Empty;

    public string ResultJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
}

/// <summary>
/// The authorization source of truth for phase 1: a channel identity mapped to capabilities (§34).
/// </summary>
public sealed class PrincipalRecord
{
    public Guid Id { get; set; }

    public ChannelKind Channel { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string CapabilitiesJson { get; set; } = "[]";
}
