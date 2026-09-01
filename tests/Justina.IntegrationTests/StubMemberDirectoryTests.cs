using Justina.Core.Domain.Messaging;
using Justina.Expense.Infrastructure.Api;
using Shouldly;

namespace Justina.IntegrationTests;

/// <summary>
/// Pins the mock data the Stub mode runs on. If someone edits the embedded JSON, these say what broke.
/// </summary>
public class StubMemberDirectoryTests
{
    private static readonly Guid KhinLeoId = Guid.Parse("4b07c8bf-dda4-40b7-8042-ceaea8ed3342");
    private static readonly Guid OrganizationId = Guid.Parse("1ba47eac-7ae7-4270-a3b8-a935f30c53ee");

    [Fact]
    public void The_paired_telegram_account_resolves_to_its_member()
    {
        var member = StubMemberDirectory.Current.Find(ChannelKind.Telegram, "646882196");

        member.ShouldNotBeNull();
        member.Id.ShouldBe(KhinLeoId);
        member.OrganizationId.ShouldBe(OrganizationId);
        member.FullName.ShouldBe("khinleo");
        member.Email.ShouldBe("khinwah@justlogin.com");
    }

    [Fact]
    public void The_company_guid_is_the_32_character_uppercase_form_the_membership_api_requires()
    {
        var member = StubMemberDirectory.Current.Find(ChannelKind.Telegram, "646882196");

        member.ShouldNotBeNull();
        member.CompanyGuid.ShouldBe("1BA47EAC7AE74270A3B8A935F30C53EE");
        member.CompanyGuid.Length.ShouldBe(32);
    }

    [Fact]
    public void An_unpaired_telegram_id_is_not_linked()
    {
        StubMemberDirectory.Current.Find(ChannelKind.Telegram, "999999999").ShouldBeNull();
    }

    [Fact]
    public void The_same_id_on_another_channel_is_not_linked()
    {
        // A Telegram user id and a WhatsApp phone number live in different namespaces; matching on the
        // number alone would hand one person's expenses to another.
        StubMemberDirectory.Current.Find(ChannelKind.WhatsApp, "646882196").ShouldBeNull();
    }

    [Fact]
    public void There_is_a_default_member_so_an_unpaired_tester_can_still_use_stub_mode()
    {
        StubMemberDirectory.Current.Default.ShouldNotBeNull().Id.ShouldBe(KhinLeoId);
    }
}
