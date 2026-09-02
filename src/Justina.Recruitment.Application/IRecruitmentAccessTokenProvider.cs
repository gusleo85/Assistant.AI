using Justina.Core.Domain.Results;

namespace Justina.Recruitment.Application;

/// <summary>
/// Supplies the credential Recruitment-API expects.
///
/// A company system token, minted the same way the expense integration mints one. It carries no acting
/// user on purpose: Recruitment-API resolves a system token to its own configured service account, and
/// a caller that could name the acting user could act as any employee.
/// </summary>
public interface IRecruitmentAccessTokenProvider
{
    Task<Result<string>> GetAsync(CancellationToken cancellationToken);
}
