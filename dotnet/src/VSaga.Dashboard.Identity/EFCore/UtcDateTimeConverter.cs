using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VSaga.Dashboard.Identity.EFCore;

/// <summary>
/// Stores every DateTimeOffset column as a plain UTC DateTime, because EF Core's SQLite provider cannot
/// order or compare DateTimeOffset values. A copy of VSaga.Persistence.EFCore's converter, which is
/// internal there; the identity project does not reference the saga persistence projects.
/// </summary>
internal sealed class UtcDateTimeConverter() : ValueConverter<DateTimeOffset, DateTime>(
    v => v.UtcDateTime,
    v => new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)));
