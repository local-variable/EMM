using System.Text.Json;

namespace EorzeanMarketMaster.Core.Ingest;

/// <summary>
/// What one listings response turned into.
/// </summary>
/// <param name="Snapshots">The observations. One per Ware the Source had anything to say about.</param>
/// <param name="WithoutData">
/// Wares the Source holds nothing for. Deliberately not Snapshots - see
/// <see cref="UniversalisV2"/> for why an empty response is not an empty board.
/// </param>
/// <param name="Unresolved">Item ids the Source did not recognise. Reported, not silently dropped.</param>
/// <param name="DiscardedRows">
/// Rows that could not be believed - a Listing of zero units, a Sale at no moment. Counted rather
/// than skipped quietly, because a parser that drops rows without saying so turns a Source defect
/// into a thin Sample nobody can explain.
/// </param>
public sealed record ListingsReading(
    IReadOnlyList<Snapshot> Snapshots,
    IReadOnlyList<WareId> WithoutData,
    IReadOnlyList<uint> Unresolved,
    int DiscardedRows);

/// <summary>
/// What one history response turned into.
/// </summary>
/// <param name="Sales">The Market Sales observed.</param>
/// <param name="WithoutData">Wares the Source holds no Sales for.</param>
/// <param name="Unresolved">Item ids the Source did not recognise.</param>
/// <param name="DiscardedRows">Rows that could not be believed.</param>
public sealed record HistoryReading(
    IReadOnlyList<MarketSale> Sales,
    IReadOnlyList<WareId> WithoutData,
    IReadOnlyList<uint> Unresolved,
    int DiscardedRows);

/// <summary>
/// Reads the aggregator's v2 responses into EMM's own vocabulary.
///
/// Pure text in, domain objects out: no network, no clock, no store. That is what lets the awkward
/// parts of these payloads be asserted against recorded responses rather than argued about.
///
/// <b>The awkward part worth knowing before reading any of this.</b> An Item with nothing listed
/// and an Item nobody has ever uploaded come back identically: <c>hasData: false</c> with
/// <c>lastUploadTime: 0</c> and an empty <c>listings</c> array. Measured across 2,000 cached
/// records, the two fields were perfectly collinear - 1,359 with data and an upload time, 641 with
/// neither, and not one record carrying one without the other. So the Source cannot tell EMM
/// "nobody is selling this here", only "I have nothing".
///
/// Those are different facts and EMM stores only the first. An empty Snapshot means the board was
/// looked at and found bare, which is a real and useful observation - and writing one from a
/// no-data response would put that claim in the store on the Source's behalf, where Days of Supply
/// and every Undercut downstream would read it as an observed fact. So a no-data response yields
/// no Snapshot at all and is reported as a Ware the Source holds nothing for. A board the Player
/// opens in game <i>can</i> tell the two apart, and its empty Snapshots are honest.
///
/// <b>Two response shapes, not one.</b> Asking for a single Item returns the view flat, with
/// <c>itemID</c> at the root; asking for several wraps them in an <c>items</c> map. A point
/// refresh of one Ware takes the first path, so the flat shape is the ordinary case here rather
/// than an edge one.
/// </summary>
public static class UniversalisV2
{
    /// <summary>
    /// Reads a listings response.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="requested">
    /// The Wares this request was made on behalf of. Needed, and not derivable from the payload: a
    /// response says which Listings exist, not which Wares were asked about, so without this an
    /// Item whose HQ side happens to be bare is indistinguishable from one that was never asked
    /// about at that Quality.
    /// </param>
    /// <param name="observedAt">The instant EMM took the observation.</param>
    /// <returns>The Snapshots, and what the Source had nothing for.</returns>
    /// <exception cref="JsonException">The body is not the response this endpoint documents.</exception>
    public static ListingsReading ReadListings(
        string json,
        IReadOnlyList<WareId> requested,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(requested);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var snapshots = new List<Snapshot>();
        var withoutData = new List<WareId>();
        var discarded = 0;

        foreach (var item in Items(root))
        {
            var itemId = UInt32Of(item, "itemID");
            var world = new WorldId(UInt32Of(item, "worldID", root));
            var asked = requested.Where(w => w.ItemId == itemId).ToList();

            if (!HasData(item, out var uploadedAt))
            {
                withoutData.AddRange(asked);
                continue;
            }

            var rows = new Dictionary<Quality, List<Listing>>
            {
                [Quality.Normal] = [],
                [Quality.High] = [],
            };

            foreach (var listing in Array(item, "listings"))
            {
                var stack = Int32Of(listing, "quantity");

                if (stack <= 0)
                {
                    discarded++;
                    continue;
                }

                rows[QualityOf(listing)].Add(new Listing(
                    new UnitPrice(Int64Of(listing, "pricePerUnit")),
                    stack,
                    TextOf(listing, "retainerName"),
                    MomentOfSeconds(listing, "lastReviewTime")));
            }

            // Every Ware asked about, plus any the response answered for anyway - the other
            // Quality arrives in the same response and throwing it away would mean paying for it
            // twice.
            var wares = asked
                .Concat(rows.Where(pair => pair.Value.Count > 0).Select(pair => new WareId(itemId, pair.Key)))
                .Distinct()
                .OrderBy(w => w.Quality);

            snapshots.AddRange(wares.Select(ware => new Snapshot(
                ware, world, observedAt, Source.Aggregator, uploadedAt, rows[ware.Quality])));
        }

        return new ListingsReading(snapshots, withoutData, Unresolved(root), discarded);
    }

    /// <summary>
    /// Reads a history response.
    /// </summary>
    /// <param name="json">The response body.</param>
    /// <param name="requested">The Wares this request was made on behalf of.</param>
    /// <returns>The Market Sales, and what the Source had nothing for.</returns>
    /// <exception cref="JsonException">The body is not the response this endpoint documents.</exception>
    public static HistoryReading ReadHistory(string json, IReadOnlyList<WareId> requested)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(requested);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var sales = new List<MarketSale>();
        var withoutData = new List<WareId>();
        var discarded = 0;

        foreach (var item in Items(root))
        {
            var itemId = UInt32Of(item, "itemID");
            var world = new WorldId(UInt32Of(item, "worldID", root));

            if (!HasData(item, out _))
            {
                withoutData.AddRange(requested.Where(w => w.ItemId == itemId));
                continue;
            }

            foreach (var entry in Array(item, "entries"))
            {
                var stack = Int32Of(entry, "quantity");
                var soldAt = MomentOfSeconds(entry, "timestamp");

                if (stack <= 0 || soldAt is null)
                {
                    discarded++;
                    continue;
                }

                // Every Sale returned is kept, whichever Quality was asked for. Both Wares of an
                // Item arrive in one response, and a Sale is an observation of the Market whether
                // or not EMM went looking for it.
                sales.Add(new MarketSale(
                    new WareId(itemId, QualityOf(entry)),
                    world,
                    soldAt.Value,
                    new UnitPrice(Int64Of(entry, "pricePerUnit")),
                    stack,
                    Source.Aggregator));
            }
        }

        return new HistoryReading(sales, withoutData, Unresolved(root), discarded);
    }

    /// <summary>
    /// The per-Item views in a response, whichever of the two shapes it came back in.
    /// </summary>
    private static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in items.EnumerateObject())
            {
                yield return property.Value;
            }

            yield break;
        }

        // The single-Item shape. Requiring itemID keeps a response that is neither shape from
        // being read as an empty success.
        if (!root.TryGetProperty("itemID", out _))
        {
            throw new JsonException(
                "The response is neither the single-Item view nor the multi-Item envelope: it has " +
                "no items map and no itemID.");
        }

        yield return root;
    }

    /// <summary>
    /// Whether the Source holds anything for this Item, and when it last learned it.
    ///
    /// Both signals are required to agree. They were measured always agreeing, and the day they
    /// stop is the day EMM would otherwise stamp a Freshness of "1970" onto an observation.
    /// </summary>
    private static bool HasData(JsonElement item, out DateTimeOffset? uploadedAt)
    {
        uploadedAt = null;

        if (item.TryGetProperty("hasData", out var hasData) && hasData.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        var uploaded = item.TryGetProperty("lastUploadTime", out var value) ? value.GetInt64() : 0;

        if (uploaded <= 0)
        {
            return false;
        }

        uploadedAt = DateTimeOffset.FromUnixTimeMilliseconds(uploaded);

        return true;
    }

    private static Quality QualityOf(JsonElement row) =>
        row.TryGetProperty("hq", out var hq) && hq.ValueKind == JsonValueKind.True
            ? Quality.High
            : Quality.Normal;

    private static IEnumerable<JsonElement> Array(JsonElement item, string name) =>
        item.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
            : [];

    private static IReadOnlyList<uint> Unresolved(JsonElement root) =>
        root.TryGetProperty("unresolvedItems", out var array) && array.ValueKind == JsonValueKind.Array
            ? [.. array.EnumerateArray().Select(element => element.GetUInt32())]
            : [];

    /// <summary>
    /// A field that has to be there, as a <see cref="JsonException"/> rather than as a
    /// <c>KeyNotFoundException</c> where it is not.
    ///
    /// The type matters: the ingest treats a body it cannot read as a failed request and carries
    /// on with the rest of the refresh, and it recognises that by the exception. A
    /// KeyNotFoundException escaping from here would take the whole refresh down instead.
    /// </summary>
    private static uint UInt32Of(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.GetUInt32()
            : throw new JsonException($"The response has no {name}.");

    private static uint UInt32Of(JsonElement element, string name, JsonElement fallback) =>
        element.TryGetProperty(name, out var value) ? value.GetUInt32() : UInt32Of(fallback, name);

    private static int Int32Of(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetInt32() : 0;

    private static long Int64Of(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetInt64() : 0;

    private static string? TextOf(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static DateTimeOffset? MomentOfSeconds(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var seconds = value.GetInt64();

        return seconds <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
