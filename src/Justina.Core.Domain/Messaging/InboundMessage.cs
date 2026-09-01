namespace Justina.Core.Domain.Messaging;

public enum ChannelKind
{
    Unknown = 0,
    Telegram = 1,
    WhatsApp = 2,
}

public enum InboundMessageKind
{
    Text = 0,
    Image = 1,
    Document = 2,
    Unsupported = 3,
}

/// <summary>
/// A reference to media held by the channel. Justina stores the reference, never the channel's raw payload
/// shape, so domain code never depends on WhatsApp- or Telegram-specific structures (§35).
/// </summary>
public sealed record MediaReference(
    string MediaId,
    string MimeType,
    string? FileName,
    long SizeBytes);

/// <summary>
/// The normalized inbound envelope. Every channel adapter produces this and nothing else;
/// business logic is written against it alone.
/// </summary>
public sealed record InboundMessage(
    ChannelKind Channel,
    string UserId,
    string ConversationId,
    string MessageId,
    InboundMessageKind Kind,
    string? Text,
    MediaReference? Media,
    DateTimeOffset ReceivedAtUtc)
{
    public bool HasMedia => Media is not null;
}
