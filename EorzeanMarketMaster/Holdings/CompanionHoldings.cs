using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using EorzeanMarketMaster.Core;
using EorzeanMarketMaster.Core.Holdings;

namespace EorzeanMarketMaster.Holdings;

/// <summary>
/// The optional inventory companion, read through its call gates.
///
/// <b>EMM never depends on it.</b> Every gate is resolved lazily and every call is wrapped: a
/// companion that is not installed, not loaded, or has changed its interface leaves EMM exactly
/// where it was, with its own reader as the baseline. What its presence buys is coverage - the
/// Retainers the Player has not opened while EMM was watching - and nothing else. It never
/// corrects, overrides or refreshes anything EMM read for itself, because it cannot say when it
/// last looked and an answer with no age may not displace one with an age.
///
/// <b>It is the only such surface in the ecosystem.</b> The market plugins surveyed expose no IPC
/// at all; this one exposes seventeen gates, four of which answer "what does the Player have".
/// </summary>
internal sealed class CompanionHoldings
{
    /// <summary>The companion whose gates these are. Its own internal name, as the installer lists it.</summary>
    internal const string PluginName = "InventoryTools";

    private readonly ICallGateSubscriber<bool> initialised;
    private readonly ICallGateSubscriber<ulong> currentCharacter;
    private readonly ICallGateSubscriber<bool, HashSet<ulong>> ownedByActive;
    private readonly ICallGateSubscriber<ulong, HashSet<ulong[]>> itemsOf;

    /// <summary>Containers already reported, so an unknown one is named once rather than per refresh.</summary>
    private readonly HashSet<ulong> reported = [];

    private bool complained;

    internal CompanionHoldings()
    {
        initialised = Plugin.PluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
        currentCharacter = Plugin.PluginInterface.GetIpcSubscriber<ulong>("AllaganTools.CurrentCharacter");
        ownedByActive = Plugin.PluginInterface
            .GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive");
        itemsOf = Plugin.PluginInterface
            .GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems");
    }

    /// <summary>
    /// Whether the companion is there and ready.
    ///
    /// Asked every time rather than cached, because a plugin can be installed, enabled or disabled
    /// while the game runs and a cached "no" would outlive the reason for it.
    /// </summary>
    internal bool Ready
    {
        get
        {
            try
            {
                return initialised.InvokeFunc();
            }
            catch (Exception)
            {
                // Not logged. This is asked once a refresh and the answer for a Player who does not
                // have the companion installed is "no" forever - a log line per ask would be noise
                // about a plugin they chose not to have.
                return false;
            }
        }
    }

    /// <summary>
    /// What the companion holds for the active Character and its Retainers.
    /// </summary>
    /// <param name="character">The Character, for naming the readings.</param>
    /// <param name="retainers">
    /// The game's own Retainer ids to EMM's Retainer names. The companion reports places by
    /// numeric id and EMM keys Retainers by name, so a Retainer missing from this map is one EMM
    /// cannot name and its rows are left alone rather than filed under a guess.
    /// </param>
    /// <param name="now">When EMM asked. Never used as the age of what comes back.</param>
    /// <returns>The readings, empty where the companion is absent or said nothing usable.</returns>
    internal IReadOnlyList<HoldingsReading> Read(
        string character, IReadOnlyDictionary<ulong, RetainerId> retainers, DateTimeOffset now)
    {
        if (!Ready)
        {
            return [];
        }

        var readings = new List<HoldingsReading>();

        try
        {
            var self = currentCharacter.InvokeFunc();
            var places = ownedByActive.InvokeFunc(true);

            foreach (var id in places)
            {
                // Bags where it is the Character itself, a Retainer where EMM has a name for it,
                // and skipped otherwise. The skipped case is another Character's household, which
                // this interface reports no name for - and a Holding filed under the wrong owner
                // is worse than one not held at all.
                RetainerId? retainer;

                if (id == self)
                {
                    retainer = null;
                }
                else if (retainers.TryGetValue(id, out var named))
                {
                    retainer = named;
                }
                else
                {
                    continue;
                }

                var decoded = CompanionInventory.Decode(
                    character, retainer, itemsOf.InvokeFunc(id), GameHoldings.Containers, now);

                if (decoded.Reading is { } reading)
                {
                    readings.Add(reading);
                }

                Report(decoded, retainer);
            }
        }
        catch (Exception ex)
        {
            // Once per session. A companion whose interface has moved would otherwise write a line
            // every time the Player pressed refresh, and the first one already said everything.
            if (!complained)
            {
                complained = true;
                Plugin.Log.Warning(ex, "EMM holdings: {Companion} did not answer; carrying on without it", PluginName);
            }

            return [];
        }

        return readings;
    }

    /// <summary>
    /// Says something only where there is something to say.
    ///
    /// <b>Dropping rows is the normal case, not the alarming one.</b> Asked for a Character's
    /// inventory the companion returns the armoury and the gear being worn; asked for a Retainer it
    /// returns the Retainer's gear. EMM reads none of that on purpose, so an earlier version of
    /// this warned on every refresh - hundreds of rows for the bags, a dozen per Retainer - and a
    /// warning that is always on is one that hides the day it means something.
    ///
    /// What is left after those are excluded is a container EMM has never heard of, which is the
    /// actual evidence that the numbering has moved. Reported once per container per session: the
    /// second occurrence adds nothing and the Player presses refresh a lot.
    /// </summary>
    private void Report(CompanionReading decoded, RetainerId? retainer)
    {
        var unexpected = CompanionInventory
            .Unexpected(decoded.UnreadContainers, GameHoldings.KnowinglyIgnored)
            .Where(reported.Add)
            .ToList();

        if (unexpected.Count == 0)
        {
            return;
        }

        Plugin.Log.Warning(
            "EMM holdings: {Companion} reported {Place} rows in container(s) {Containers}, which EMM " +
            "neither reads nor knowingly skips - its inventory numbering may have moved. {Placed} rows kept.",
            PluginName,
            retainer?.Retainer ?? "bags",
            string.Join(", ", unexpected),
            decoded.Placed);
    }
}
