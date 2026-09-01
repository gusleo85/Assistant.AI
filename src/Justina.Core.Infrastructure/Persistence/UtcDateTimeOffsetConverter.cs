using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Justina.Core.Infrastructure.Persistence;

/// <summary>
/// Stores <see cref="DateTimeOffset"/> as <c>datetime2</c> in UTC.
///
/// SQL Server can store the offset in a <c>datetimeoffset</c> column, but Justina has no use for a local
/// offset: every timestamp it records is already UTC, and keeping one column type makes comparisons and
/// indexes unambiguous (§24). The offset is therefore normalized away on write and reattached as UTC on read.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, DateTime>
{
    public UtcDateTimeOffsetConverter()
        : base(
            value => value.UtcDateTime,
            value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)))
    {
    }
}
