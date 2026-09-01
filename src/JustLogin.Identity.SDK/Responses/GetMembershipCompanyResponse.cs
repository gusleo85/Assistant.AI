using JustLogin.Identity.SDK.DTO.ApiResponses.v2.Membership;

namespace JustLogin.Identity.SDK.Responses;
public class GetMembershipCompanyResponse
{
    public string? CompanyGuid { get; set; }
    public string? CompanyId { get; set; }
    public static GetMembershipCompanyResponse Map(MembershipCompanyResponse source)
    {
        return new GetMembershipCompanyResponse
        {
            CompanyGuid = string.IsNullOrEmpty(source.CompanyGuid) ? null : source.CompanyGuid.Trim(),
            CompanyId = string.IsNullOrEmpty(source.CompanyId) ? null : source.CompanyId.Trim(),
        };
    }
}