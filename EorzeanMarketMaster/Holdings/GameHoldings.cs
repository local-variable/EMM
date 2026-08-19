using System;
using System.Collections.Generic;
using System.Linq;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Holdings;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace EorzeanMarketMaster.Holdings;

/// <summary>
/// The result of trying to read the open Retainer.
///
/// <b>The refusal is carried rather than swallowed, and that is the #18 lesson applied here.</b>
/// A null reading on its own reads identically whether no Retainer was open, the Retainer was open
/// and its containers had not arrived, or the price array was still catching up - and those want
/// three different responses from whoever is watching. An instrument that reports silence for all
/// of them is one whose silence proves nothing.
/// </summary>
/// <param name="Reading">What was read, or null where nothing could be.</param>
/// <param name="Refused">
/// Why a Retainer that IS open could not be read, or null where one was read or none was open.
/// </param>
internal readonly record struct RetainerRead(HoldingsReading? Reading, string? Refused);

/// <summary>
/// EMM's own inventory reader: the baseline, and the only one that needs no other plugin installed.
///
/// Three things it can read, and they are not equally available, which is the shape of the whole
/// Holdings surface:
///
///   - <b>Character bags</b>, whenever the Player is logged in.
///   - <b>One Retainer's stock and Listings</b>, and only while that Retainer is open. This is the
///     one that costs something: the game loads a Retainer's containers when it is opened and not
///     before, so everything EMM knows about the other twenty-nine is last-seen state.
///   - <b>Every Retainer's counts</b>, at the summoning bell, without opening any of them. Counts
///     and not contents - the bell says how many Listings a Retainer has and never which.
///
/// Nothing here writes to the game. Every call reads a container and returns.
/// </summary>
internal static unsafe class GameHoldings
{
    /// <summary>
    /// The Character's own containers EMM reads.
    ///
    /// The four bags and the crystal store, and deliberately not the armoury, equipped gear or the
    /// saddlebag. Equipment in use is not stock waiting for a slot, and the saddlebag is unreadable
    /// unless the Player has it open - a container EMM can only sometimes see would make the total
    /// change when the Player walked into a city, which is worse than not counting it.
    /// </summary>
    /// <summary>
    /// The containers that must be loaded before EMM will claim to have read a place.
    ///
    /// <b>A reading is the complete contents of its place, so a reading taken while the game has
    /// not loaded the containers is not a thin reading - it is a Player stated to own nothing.</b>
    /// That would then replace a good reading, and for a Retainer there is no way to get the good
    /// one back except by going to a bell and opening it again. So a container that is not loaded
    /// makes EMM decline to read at all rather than read an emptiness it never saw.
    /// </summary>
    private static readonly InventoryType[] RequiredBags =
        [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    /// <summary>The Retainer's seven pages, which must all be loaded before its stock is believed.</summary>
    private static readonly InventoryType[] RequiredRetainerPages =
    [
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    /// <summary>
    /// The four bags plus the crystal store.
    ///
    /// The crystal store is read but is deliberately not required. It behaves differently from the
    /// ordinary pages, and requiring it would mean declining every reading if that difference ever
    /// became a difference in <c>IsLoaded</c> - at the cost of undercounting shards on the rare
    /// occasion it is absent, which is a smaller and self-correcting error than reading nothing.
    /// </summary>
    private static readonly InventoryType[] BagContainers =
        [.. RequiredBags, InventoryType.Crystals];

    /// <summary>The Retainer's own stock: its seven pages and its crystals, on the same footing.</summary>
    private static readonly InventoryType[] RetainerStockContainers =
        [.. RequiredRetainerPages, InventoryType.RetainerCrystals];

    /// <summary>
    /// Which container is which place, for a Source that reports containers by number.
    ///
    /// Built from the game's own enum so the numbers are the compiler's problem rather than a
    /// comment's. Any container not in here is one EMM does not read, and a companion row landing
    /// in one is dropped rather than filed somewhere plausible.
    /// </summary>
    internal static IReadOnlyDictionary<ulong, HoldingPlace> Containers { get; } = Map();

    /// <summary>
    /// Containers EMM knows about and deliberately does not read.
    ///
    /// This list exists purely so a Source that reports everything can be told apart from a Source
    /// whose numbering has moved. A companion asked for a Character's whole inventory always
    /// returns the armoury and what the Character is wearing; asked for a Retainer it always
    /// returns the gear that Retainer is wearing. None of that is stock and none of it is a fault,
    /// so dropping those rows must not read as one - a warning that fires on every refresh is a
    /// warning nobody reads by the third time.
    ///
    /// Equipment is excluded on the same reasoning as the armoury: gear in use is not stock waiting
    /// for a slot. The saddlebags are excluded because the game only loads them while the Player
    /// has one open, so counting them would make the total change when the Player walked into a
    /// city.
    /// </summary>
    internal static IReadOnlySet<ulong> KnowinglyIgnored { get; } = Ignored();

    /// <summary>
    /// Whether an Item can be sold on a board at all.
    ///
    /// Cached because it is asked once per occupied slot per read and the answer never changes
    /// inside a session. The filter itself is a judgement worth stating: Holdings is defined as
    /// everything the Player owns, and EMM reads only what a Market could ever price - a bag of
    /// untradable quest items and job gear would bury the answer to "what do I own to sell" under
    /// the answer to "what is in my bags".
    /// </summary>
    private static readonly Dictionary<uint, bool> Marketable = [];

    /// <summary>
    /// Everything marketable in the Character's own bags.
    /// </summary>
    /// <param name="character">The Character.</param>
    /// <param name="now">The instant to stamp the reading with.</param>
    /// <returns>The reading, or null where the game is not in a state to be read.</returns>
    internal static HoldingsReading? Bags(string character, DateTimeOffset now)
    {
        var inventory = InventoryManager.Instance();

        if (inventory == null || string.IsNullOrWhiteSpace(character) || !Loaded(inventory, RequiredBags))
        {
            return null;
        }

        var held = new List<HeldWare>();

        foreach (var container in BagContainers)
        {
            Collect(inventory, container, HoldingPlace.Bag, held);
        }

        return new HoldingsReading(character, null, now, now, Source.OpenedBoard, held);
    }

    /// <summary>
    /// The open Retainer's stock and Listings, in one pass.
    ///
    /// <b>Refuses to read a Retainer whose price array has not caught up.</b> The item container
    /// reflects a change immediately and the parallel price array follows about fifty milliseconds
    /// later, which was measured on a live client rather than assumed. A snapshot taken in that
    /// window carries a Listing whose asking price is a leftover or a zero, and it would be stored
    /// as though it were the board.
    /// </summary>
    /// <param name="character">The Character who owns the Retainer.</param>
    /// <param name="now">The instant to stamp the reading with.</param>
    /// <returns>The reading, or null where no Retainer is open or its containers are not settled.</returns>
    internal static RetainerRead OpenRetainer(string character, DateTimeOffset now)
    {
        var inventory = InventoryManager.Instance();

        // No Retainer open is not a refusal - there is simply nothing to read, which is the
        // ordinary state. Only a Retainer that IS open and still cannot be read is worth saying
        // anything about.
        if (inventory == null || Open(character) is not { } retainer)
        {
            return default;
        }

        var market = inventory->GetInventoryContainer(InventoryType.RetainerMarket);

        if (market == null || !market->IsLoaded)
        {
            return new RetainerRead(null, "its market board has not loaded yet");
        }

        var held = new List<HeldWare>();
        var occupied = 0;
        var priced = 0;

        for (var slot = 0; slot < market->Size; slot++)
        {
            var item = market->GetInventorySlot(slot);
            var price = inventory->GetRetainerMarketPrice((short)slot);

            if (price != 0)
            {
                priced++;
            }

            if (item == null || item->ItemId == 0)
            {
                continue;
            }

            occupied++;

            // Not filtered by marketability, and that is not an oversight: it is on the board, so
            // whatever the Item sheet says about it, somebody can buy it.
            held.Add(new HeldWare(
                Ware(item),
                HoldingPlace.Listed,
                item->Quantity,
                new UnitPrice((long)price)));
        }

        if (occupied != priced)
        {
            return new RetainerRead(
                null, $"its prices have not caught up ({occupied} listed, {priced} priced)");
        }

        if (!Loaded(inventory, RequiredRetainerPages))
        {
            return new RetainerRead(null, "its inventory pages have not loaded yet");
        }

        foreach (var container in RetainerStockContainers)
        {
            Collect(inventory, container, HoldingPlace.Stock, held);
        }

        return new RetainerRead(
            new HoldingsReading(character, retainer, now, now, Source.OpenedBoard, held), null);
    }

    /// <summary>
    /// Which Retainer is open, or null where none is.
    /// </summary>
    /// <param name="character">The Character who owns it.</param>
    /// <returns>The Retainer, or null.</returns>
    internal static RetainerId? Open(string character)
    {
        var manager = RetainerManager.Instance();

        // <b>Two signals, because neither is sufficient on its own.</b> GetActiveRetainer does NOT
        // reliably clear when the Player backs out to the Retainer list - it goes on naming the
        // last Retainer visited - so on its own it reports one open indefinitely after a run has
        // finished. The list addon being up is the game's own statement that none is. Requiring
        // both means a stale pointer reads as "none open", which is the truth, and a genuinely
        // open Retainer still reads as open because the list is not up while one is.
        if (manager == null || string.IsNullOrWhiteSpace(character) || AtTheRetainerList)
        {
            return null;
        }

        var active = manager->GetActiveRetainer();

        return active == null || active->RetainerId == 0
            ? null
            : new RetainerId(character, active->NameString);
    }

    /// <summary>
    /// Whether the Retainer list itself is on screen, which is the game saying no Retainer is open.
    ///
    /// Opening a Retainer replaces the list with its menu, so these two are never up together -
    /// which is what makes the addon a cleaner answer than the manager's own pointer.
    /// </summary>
    internal static bool AtTheRetainerList
    {
        get
        {
            var addon = Plugin.GameGui.GetAddonByName("RetainerList", 1);

            return !addon.IsNull && addon.IsVisible;
        }
    }

    /// <summary>
    /// Every Retainer's counts, which is all the summoning bell exposes.
    ///
    /// Readable for all of them at once and without opening any, which is exactly why the surface
    /// has a control for it - and why that control cannot promise contents.
    /// </summary>
    /// <param name="character">The Character.</param>
    /// <param name="now">The instant the roster was read at.</param>
    /// <returns>The roster, or null where the game has no Retainer list to read.</returns>
    internal static RetainerRoster? Roster(string character, DateTimeOffset now)
    {
        var manager = RetainerManager.Instance();

        if (manager == null || string.IsNullOrWhiteSpace(character))
        {
            return null;
        }

        var retainers = new List<RetainerSummary>();

        for (uint i = 0; i < manager->GetRetainerCount(); i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);

            if (retainer == null || retainer->RetainerId == 0)
            {
                continue;
            }

            retainers.Add(new RetainerSummary(
                new RetainerId(character, retainer->NameString),
                retainer->ItemCount,
                retainer->MarketItemCount,
                retainer->Gil,

                // Zero is "nothing listed" rather than "never opened", observed on a live client -
                // so it is not an expiry, and reading it as one would date every empty Retainer to
                // 1970 and call its market lapsed forever.
                retainer->MarketExpire == 0
                    ? null
                    : DateTimeOffset.FromUnixTimeSeconds(retainer->MarketExpire),
                retainer->Available));
        }

        return new RetainerRoster(character, now, retainers);
    }

    /// <summary>
    /// The game's own Retainer ids to the names EMM keys Retainers by.
    ///
    /// The two identifiers exist because two surfaces disagree. The game has a stable numeric id;
    /// the automation surface EMM drives reports only the name, so the name is what EMM keys on.
    /// Anything that speaks in ids - a companion plugin, most obviously - has to be translated
    /// here, at the one place that can see both.
    /// </summary>
    /// <param name="character">The Character who owns them.</param>
    /// <returns>The map, empty where there is no Retainer list to read.</returns>
    internal static IReadOnlyDictionary<ulong, RetainerId> RetainerIds(string character)
    {
        var manager = RetainerManager.Instance();
        var map = new Dictionary<ulong, RetainerId>();

        if (manager == null || string.IsNullOrWhiteSpace(character))
        {
            return map;
        }

        for (uint i = 0; i < manager->GetRetainerCount(); i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);

            if (retainer != null && retainer->RetainerId != 0)
            {
                map[retainer->RetainerId] = new RetainerId(character, retainer->NameString);
            }
        }

        return map;
    }

    /// <summary>Whether every one of these containers is present and populated.</summary>
    private static bool Loaded(InventoryManager* inventory, InventoryType[] required)
    {
        foreach (var type in required)
        {
            var container = inventory->GetInventoryContainer(type);

            if (container == null || !container->IsLoaded)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Adds one container's marketable contents to a reading under construction.
    ///
    /// Quantities of one Ware are not summed here. The reading does that itself, and doing it in
    /// two places is how the two spellings eventually disagree.
    /// </summary>
    private static void Collect(
        InventoryManager* inventory, InventoryType type, HoldingPlace place, List<HeldWare> held)
    {
        var container = inventory->GetInventoryContainer(type);

        if (container == null || !container->IsLoaded)
        {
            return;
        }

        for (var slot = 0; slot < container->Size; slot++)
        {
            var item = container->GetInventorySlot(slot);

            if (item == null || item->ItemId == 0 || item->Quantity == 0 || !CanBeSold(item->ItemId))
            {
                continue;
            }

            held.Add(new HeldWare(Ware(item), place, item->Quantity, null));
        }
    }

    private static WareId Ware(InventoryItem* item) => new(
        item->ItemId,
        item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) ? Quality.High : Quality.Normal);

    /// <summary>
    /// Whether the game lists this Item in a market board search category.
    ///
    /// That is the game's own definition of sellable, rather than EMM's guess at one. An Item with
    /// no search category cannot be put on a board at all, so it is not stock and never will be.
    /// </summary>
    private static bool CanBeSold(uint itemId)
    {
        if (Marketable.TryGetValue(itemId, out var known))
        {
            return known;
        }

        var sellable = false;

        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();

            if (sheet.TryGetRow(itemId, out var row))
            {
                sellable = !row.IsUntradable && row.ItemSearchCategory.RowId != 0;
            }
        }
        catch (Exception ex)
        {
            // A sheet that cannot be read is a reason to say nothing about this Item, not a reason
            // to take the frame down. It is cached as unsellable so the lookup is not retried per
            // slot per second, and the log carries the cause.
            Plugin.Log.Warning(ex, "EMM holdings: could not read item {ItemId} from the sheet", itemId);
        }

        Marketable[itemId] = sellable;

        return sellable;
    }

    private static HashSet<ulong> Ignored() =>
    [
        .. new[]
        {
            // What the Character and its Retainers are wearing.
            InventoryType.EquippedItems,
            InventoryType.RetainerEquippedItems,

            // The armoury, one container per equipment slot. This is the bulk of it - a Character
            // with a full armoury drops several hundred rows here on every single refresh.
            InventoryType.ArmoryMainHand,
            InventoryType.ArmoryOffHand,
            InventoryType.ArmoryHead,
            InventoryType.ArmoryBody,
            InventoryType.ArmoryHands,
            InventoryType.ArmoryWaist,
            InventoryType.ArmoryLegs,
            InventoryType.ArmoryFeets,
            InventoryType.ArmoryEar,
            InventoryType.ArmoryNeck,
            InventoryType.ArmoryWrist,
            InventoryType.ArmoryRings,
            InventoryType.ArmorySoulCrystal,

            // Gil and currencies, which are not Wares, and the key items nobody can sell.
            InventoryType.Currency,
            InventoryType.RetainerGil,
            InventoryType.KeyItems,

            // The saddlebags, readable only while the Player has one open.
            InventoryType.SaddleBag1,
            InventoryType.SaddleBag2,
            InventoryType.PremiumSaddleBag1,
            InventoryType.PremiumSaddleBag2,
        }.Select(container => (ulong)container),
    ];

    private static Dictionary<ulong, HoldingPlace> Map()
    {
        var map = new Dictionary<ulong, HoldingPlace>
        {
            [(ulong)InventoryType.RetainerMarket] = HoldingPlace.Listed,
        };

        foreach (var container in BagContainers)
        {
            map[(ulong)container] = HoldingPlace.Bag;
        }

        foreach (var container in RetainerStockContainers)
        {
            map[(ulong)container] = HoldingPlace.Stock;
        }

        return map;
    }
}
