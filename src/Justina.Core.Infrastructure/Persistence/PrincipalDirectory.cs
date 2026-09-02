using Justina.Core.Application.Abstractions;
using Justina.Core.Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Justina.Core.Infrastructure.Persistence;

/// <summary>
/// Answers "who is there to talk to on this channel" from the principals table.
///
/// Ordered by display name so the answer is stable: a proactive message that goes to a different person
/// after a restart, because the database happened to return rows in another order, would be very hard to
/// believe and even harder to reproduce.
/// </summary>
public sealed class PrincipalDirectory(JustinaDbContext context) : IPrincipalDirectory
{
    public async Task<PrincipalIdentity?> GetPrimaryAsync(ChannelKind channel, CancellationToken cancellationToken)
    {
        return await context.Principals
            .AsNoTracking()
            .Where(principal => principal.Channel == channel)
            .OrderBy(principal => principal.DisplayName)
            .ThenBy(principal => principal.UserId)
            .Select(principal => new PrincipalIdentity(principal.UserId, principal.DisplayName))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
