using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Observability;
using Justina.Core.Domain.Results;
using Justina.Expense.Application.Abstractions;
using Justina.Expense.Domain;
using NSubstitute;
using Shouldly;

namespace Justina.Expense.UnitTests;

/// <summary>
/// A capability answers "may this principal submit expenses at all". It does not answer "may this
/// principal touch <em>this</em> receipt". These tests cover the second question (§34).
/// </summary>
public class ReceiptAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 31, 10, 0, 0, TimeSpan.Zero);

    private readonly IConversationStateStore _conversations = Substitute.For<IConversationStateStore>();
    private readonly IReceiptRepository _receipts = Substitute.For<IReceiptRepository>();

    private ReceiptAccess CreateAccess() => new(_conversations, _receipts);

    private static RequestContext Context(string conversationId = "chat-1") =>
        new(
            new UserContext(Guid.NewGuid(), ChannelKind.Telegram, "user-1", "Test User", [Capabilities.ExpenseSubmit]),
            ChannelKind.Telegram,
            conversationId,
            CorrelationId.New());

    private static ConversationState Conversation(Guid id, string externalId = "chat-1") =>
        new(id, ChannelKind.Telegram, externalId, "user-1", null, null, Now);

    private static Receipt ReceiptIn(Guid conversationId) =>
        Receipt.Create(conversationId, "media-1", null, Now);

    [Fact]
    public async Task A_receipt_in_the_callers_own_conversation_is_returned()
    {
        var conversationId = Guid.NewGuid();
        var receipt = ReceiptIn(conversationId);

        _conversations.GetAsync(ChannelKind.Telegram, "chat-1", Arg.Any<CancellationToken>())
            .Returns(Conversation(conversationId));
        _receipts.GetAsync(receipt.Id, Arg.Any<CancellationToken>()).Returns(receipt);

        var result = await CreateAccess().GetAsync(Context(), receipt.Id, default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBe(receipt.Id);
    }

    /// <summary>
    /// The defect this guard exists for: a caller holding the tool secret and the capability could
    /// otherwise pass someone else's receipt id and confirm, cancel, edit or read it.
    /// </summary>
    [Fact]
    public async Task A_receipt_belonging_to_another_conversation_is_refused()
    {
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var receipt = ReceiptIn(theirs);

        _conversations.GetAsync(ChannelKind.Telegram, "chat-1", Arg.Any<CancellationToken>())
            .Returns(Conversation(mine));
        _receipts.GetAsync(receipt.Id, Arg.Any<CancellationToken>()).Returns(receipt);

        var result = await CreateAccess().GetAsync(Context(), receipt.Id, default);

        result.IsFailure.ShouldBeTrue();
    }

    /// <summary>
    /// Refusing with "not found" rather than "unauthorized" means an id cannot be probed for existence.
    /// </summary>
    [Fact]
    public async Task Another_conversations_receipt_is_indistinguishable_from_one_that_does_not_exist()
    {
        var mine = Guid.NewGuid();
        var foreignReceipt = ReceiptIn(Guid.NewGuid());
        var missingId = Guid.NewGuid();

        _conversations.GetAsync(ChannelKind.Telegram, "chat-1", Arg.Any<CancellationToken>())
            .Returns(Conversation(mine));
        _receipts.GetAsync(foreignReceipt.Id, Arg.Any<CancellationToken>()).Returns(foreignReceipt);
        _receipts.GetAsync(missingId, Arg.Any<CancellationToken>()).Returns((Receipt?)null);

        var access = CreateAccess();
        var foreign = await access.GetAsync(Context(), foreignReceipt.Id, default);
        var missing = await access.GetAsync(Context(), missingId, default);

        foreign.Error.Code.ShouldBe(ErrorCodes.NotFound);
        missing.Error.Code.ShouldBe(ErrorCodes.NotFound);
        foreign.Error.Message.ShouldBe(missing.Error.Message);
    }

    [Fact]
    public async Task A_caller_with_no_conversation_yet_can_reach_nothing()
    {
        var receipt = ReceiptIn(Guid.NewGuid());

        _conversations.GetAsync(ChannelKind.Telegram, "chat-1", Arg.Any<CancellationToken>())
            .Returns((ConversationState?)null);
        _receipts.GetAsync(receipt.Id, Arg.Any<CancellationToken>()).Returns(receipt);

        var result = await CreateAccess().GetAsync(Context(), receipt.Id, default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task The_active_receipt_is_looked_up_within_the_callers_own_conversation()
    {
        var conversationId = Guid.NewGuid();
        var receipt = ReceiptIn(conversationId);

        _conversations.GetAsync(ChannelKind.Telegram, "chat-1", Arg.Any<CancellationToken>())
            .Returns(Conversation(conversationId));
        _receipts.GetActiveForConversationAsync(conversationId, Arg.Any<CancellationToken>()).Returns(receipt);

        var result = await CreateAccess().GetActiveAsync(Context(), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Id.ShouldBe(receipt.Id);

        await _receipts.Received(1).GetActiveForConversationAsync(conversationId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_active_receipt_is_reported_plainly()
    {
        var conversationId = Guid.NewGuid();

        _conversations.GetAsync(ChannelKind.Telegram, "chat-1", Arg.Any<CancellationToken>())
            .Returns(Conversation(conversationId));
        _receipts.GetActiveForConversationAsync(conversationId, Arg.Any<CancellationToken>())
            .Returns((Receipt?)null);

        var result = await CreateAccess().GetActiveAsync(Context(), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.NotFound);
        result.Error.Message.ShouldContain("no receipt in progress");
    }

    [Fact]
    public async Task A_conversation_that_does_not_exist_has_no_active_receipt()
    {
        _conversations.GetAsync(ChannelKind.Telegram, "chat-1", Arg.Any<CancellationToken>())
            .Returns((ConversationState?)null);

        var result = await CreateAccess().GetActiveAsync(Context(), default);

        result.IsFailure.ShouldBeTrue();
        await _receipts.DidNotReceiveWithAnyArgs().GetActiveForConversationAsync(default, default);
    }
}
