using System.Globalization;

namespace EorzeanMarketMaster.Core.Store;

/// <summary>
/// One week of raw Snapshots, and the name of the table holding them.
///
/// Weekly is not an arbitrary grain. Raw Snapshots are the bulk of the store - a Ring 1 of 4,000
/// Wares snapshotted hourly is roughly seven times the entire catalogue's annual Sale history -
/// and they are also the most disposable thing in it. Removing a week of them was measured at
/// 0.03 s as a DROP against 8-17 s as the equivalent DELETE, and the DELETE takes a write lock
/// inside a running game. So eviction drops whole tables, and the partition is the unit that can
/// be dropped.
///
/// ISO-8601 weeks, which is why the year here comes from <see cref="ISOWeek"/> and not from
/// <see cref="DateTimeOffset.Year"/>. The two disagree at the turn of the year - 2027-01-01 falls
/// in ISO week 53 of 2026 - and taking the calendar year would put those days in a table that a
/// later, real week 53 would collide with.
/// </summary>
/// <param name="Year">The ISO week-numbering year.</param>
/// <param name="Week">The ISO week number, 1 to 53.</param>
public readonly record struct StoreWeek(int Year, int Week)
{
    /// <summary>The week an instant falls in, measured in UTC.</summary>
    /// <param name="instant">Any moment.</param>
    /// <returns>Its ISO week.</returns>
    public static StoreWeek Of(DateTimeOffset instant)
    {
        // UTC throughout. The store is read on whatever machine the Player is on and compared
        // against Sale times from a Source in another time zone, so a local-time partition
        // boundary would put the same observation in different weeks on different machines.
        var utc = instant.UtcDateTime;

        return new StoreWeek(ISOWeek.GetYear(utc), ISOWeek.GetWeekOfYear(utc));
    }

    /// <summary>
    /// The table this week's raw Snapshots live in.
    ///
    /// Zero-padded so the names sort in chronological order, which is what lets the oldest
    /// partition be found by sorting a list of table names rather than by parsing them.
    /// </summary>
    public string TableName => $"snapshot_{Year:D4}w{Week:D2}";

    /// <summary>The first instant in the week, inclusive.</summary>
    public DateTimeOffset Start => new(ISOWeek.ToDateTime(Year, Week, DayOfWeek.Monday), TimeSpan.Zero);

    /// <summary>The first instant after the week. Exclusive, so ranges join without overlapping.</summary>
    public DateTimeOffset EndExclusive => Start.AddDays(7);

    /// <summary>
    /// Reads a week back out of a partition table name.
    /// </summary>
    /// <param name="tableName">A name as <see cref="TableName"/> renders it.</param>
    /// <param name="week">The week it names.</param>
    /// <returns>Whether the name was a partition name at all.</returns>
    public static bool TryParse(string tableName, out StoreWeek week)
    {
        week = default;

        if (tableName is null || !tableName.StartsWith("snapshot_", StringComparison.Ordinal))
        {
            return false;
        }

        var stamp = tableName["snapshot_".Length..];
        var split = stamp.IndexOf('w');

        if (split != 4 || stamp.Length != 7)
        {
            return false;
        }

        if (!int.TryParse(stamp[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(stamp[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        if (number is < 1 or > 53)
        {
            return false;
        }

        week = new StoreWeek(year, number);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Year:D4}-W{Week:D2}";
}
