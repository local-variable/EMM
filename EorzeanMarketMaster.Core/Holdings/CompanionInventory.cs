namespace EorzeanMarketMaster.Core.Holdings;

/// <summary>
/// What decoding one companion reading produced, including what it could not place.
/// </summary>
/// <param name="Reading">
/// The reading, or null where nothing could be placed. Null is a refusal rather than an empty
/// result - see <see cref="CompanionInventory.Decode"/>.
/// </param>
/// <param name="Placed">Rows EMM recognised and kept.</param>
/// <param name="UnreadContainers">
/// The containers EMM did not read, and how many rows each held.
///
/// <b>Broken down by container rather than totalled, because the total is not a signal.</b> A
/// companion reporting a Character's whole inventory always includes the armoury and equipped
/// gear, and a Retainer's always includes the gear it is wearing - so "some rows were dropped" is
/// true on every single refresh and says nothing. Which container they were in is the thing that
/// separates "EMM is correctly ignoring the armoury" from "the numbering moved and EMM is now
/// dropping the market board", and only the second is worth waking anyone up for.
/// </param>
public sealed record CompanionReading(
    HoldingsReading? Reading,
    int Placed,
    IReadOnlyDictionary<ulong, int> UnreadContainers)
{
    /// <summary>Rows dropped in total, across every container EMM does not read.</summary>
    public int Unplaced => UnreadContainers.Values.Sum();
}

/// <summary>
/// Reads another plugin's inventory rows into EMM's own shape.
///
/// <b>The container ids are supplied by the caller, not known here.</b> They are the game's own
/// inventory enum, and the plugin side has that enum as symbols the compiler checks - so the
/// numbers live there and the arithmetic lives here, which is what lets this be exercised with no
/// client running.
///
/// <b>A row EMM cannot place is dropped, and a reading in which nothing could be placed is
/// refused outright.</b> That refusal is the important half. A reading is the complete contents of
/// its place, so one built out of rows EMM failed to recognise would not be a thin reading - it
/// would be a Retainer confidently reported as empty, which is the single most damaging thing this
/// surface could say. Dropping the reading leaves the Retainer marked unseen, which is true.
///
/// The same refusal covers a place the companion returned nothing at all for, and for the same
/// reason: this interface does not distinguish "that Retainer is empty" from "I have never cached
/// that Retainer", so the companion is only ever allowed to add coverage and is never allowed to
/// assert an absence.
/// </summary>
public static class CompanionInventory
{
    /// <summary>
    /// The positions this decoder reads out of one row, as the companion lays them out.
    ///
    /// Named rather than inlined because they are magic numbers into somebody else's array and the
    /// consequence of transposing two of them is a wrong quantity rather than a crash.
    /// </summary>
    private const int Container = 0;
    private const int ItemId = 2;
    private const int Quantity = 3;
    private const int Flags = 6;
    private const int MarketPrice = 24;

    /// <summary>The shortest row this can read. Anything shorter is not the layout EMM expects.</summary>
    private const int MinimumWidth = MarketPrice + 1;

    /// <summary>The companion's High Quality flag, which is the game's own.</summary>
    private const ulong HighQuality = 1;

    /// <summary>
    /// Decodes one place's rows.
    /// </summary>
    /// <param name="character">The owning Character.</param>
    /// <param name="retainer">The Retainer these rows belong to, or null for the Character's bags.</param>
    /// <param name="rows">The companion's rows, in its own positional layout.</param>
    /// <param name="containers">
    /// Which inventory containers count as which place. A container absent from this is one EMM
    /// does not read - equipped gear, gil, the armoury - and its rows are dropped rather than
    /// guessed at.
    /// </param>
    /// <param name="askedAt">When EMM asked. Not when the companion last looked, which it does not say.</param>
    /// <returns>The reading and the counts behind it.</returns>
    public static CompanionReading Decode(
        string character,
        RetainerId? retainer,
        IEnumerable<IReadOnlyList<ulong>> rows,
        IReadOnlyDictionary<ulong, HoldingPlace> containers,
        DateTimeOffset askedAt)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(containers);

        var held = new List<HeldWare>();
        var unplaced = new Dictionary<ulong, int>();

        void Drop(ulong container) =>
            unplaced[container] = unplaced.GetValueOrDefault(container) + 1;

        foreach (var row in rows)
        {
            if (row is null || row.Count < MinimumWidth || row[ItemId] == 0 || row[Quantity] == 0)
            {
                continue;
            }

            if (!containers.TryGetValue(row[Container], out var place))
            {
                Drop(row[Container]);
                continue;
            }

            // A Listing with no price is not a Listing. The companion holds the price array
            // alongside the items and can report a zero for a slot it has not seen priced, and a
            // row like that describes a Listing EMM cannot state the ask for.
            if (place == HoldingPlace.Listed && row[MarketPrice] == 0)
            {
                Drop(row[Container]);
                continue;
            }

            // A Retainer's own containers cannot hold a Character's bag line, and the reading
            // would refuse the whole set for it. Counting it as unplaced keeps the refusal rule
            // meaningful rather than turning a mapping mistake into an exception.
            if ((place == HoldingPlace.Bag) != (retainer is null))
            {
                Drop(row[Container]);
                continue;
            }

            held.Add(new HeldWare(
                new WareId(
                    (uint)row[ItemId],
                    (row[Flags] & HighQuality) != 0 ? Quality.High : Quality.Normal),
                place,
                (int)row[Quantity],
                place == HoldingPlace.Listed ? new UnitPrice((long)row[MarketPrice]) : null));
        }

        // Nothing placed: EMM did not read this place, whatever the reason. A companion that
        // returned rows EMM could not recognise has a mapping that has drifted; one that returned
        // nothing at all is either describing an empty Retainer or has never cached it, and this
        // interface does not say which. Both are refused, so the companion can only ever ADD
        // coverage and can never assert an absence - the difference between a Retainer marked
        // unseen and one confidently reported sold out.
        if (held.Count == 0)
        {
            return new CompanionReading(null, 0, unplaced);
        }

        return new CompanionReading(
            new HoldingsReading(character, retainer, askedAt, null, Source.ImportedStore, held),
            held.Count,
            unplaced);
    }

    /// <summary>
    /// Whether a set of dropped containers is worth telling anyone about.
    ///
    /// A container EMM knows about and deliberately does not read - the armoury, equipped gear, a
    /// saddlebag - is not news, and a warning that fires on every refresh is one nobody reads by
    /// the third time. Only a container EMM has never heard of is evidence that the numbering has
    /// moved underneath it.
    /// </summary>
    /// <param name="unread">What was dropped, by container.</param>
    /// <param name="ignored">Containers EMM knows about and chooses not to read.</param>
    /// <returns>The containers that are neither read nor knowingly ignored.</returns>
    public static IReadOnlyList<ulong> Unexpected(
        IReadOnlyDictionary<ulong, int> unread, IReadOnlySet<ulong> ignored)
    {
        ArgumentNullException.ThrowIfNull(unread);
        ArgumentNullException.ThrowIfNull(ignored);

        return [.. unread.Keys.Where(container => !ignored.Contains(container)).Order()];
    }
}
