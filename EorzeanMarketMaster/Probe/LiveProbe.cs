using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace EorzeanMarketMaster.Probe;

/// <summary>
/// The live-session observation harness for issue #18.
///
/// #18 is not a decision — it is six things that can only be settled by watching the running game.
/// This class watches, and writes what it saw to its own log file. It DECIDES NOTHING and it
/// CHANGES NOTHING about the retainer flow; the one action it can take (arming a postprocess
/// request) is off until asked for by name, because that action makes AutoRetainer block.
///
/// Design notes worth keeping:
///
///   - It POLLS in the framework update rather than subscribing to addon lifecycle events. Q6 asks
///     what AutoRetainer does between the bell list opening and the postprocess handover, which is a
///     question about ORDERING. A per-frame poll produces a frame-numbered timeline of every piece
///     of state at once; lifecycle callbacks would produce a set of separately-ordered streams and
///     lose exactly the relationship being asked about.
///   - It logs TRANSITIONS, not frames. A poll that finds nothing changed writes nothing, so the
///     log stays readable across a long session.
///   - Its own log file, not dalamud.log: dalamud.log is held open by the game and is mostly other
///     plugins' traffic. This one is opened FileShare.ReadWrite so it can be read while the game
///     runs.
/// </summary>
internal sealed unsafe class LiveProbe : IDisposable
{
    /// <summary>LogMessage rows the sale-detection research left open, dumped by `/emm probe logmsg`.</summary>
    private static readonly uint[] SaleLogMessageRows = [384, 745, 748, 4578, 6081, 6082];

    /// <summary>Addons whose visibility is worth a timeline entry while the bell is open.</summary>
    private static readonly string[] WatchedAddons =
        ["RetainerList", "SelectString", "SelectYesno", "RetainerSellList", "RetainerSell", "RetainerSellItem"];

    private readonly StreamWriter? writer;
    private readonly string logPath;

    private long frame;

    // Previous-frame state. Every one of these exists so a poll can be turned into a transition.
    private bool prevBell;
    private readonly Dictionary<string, bool> prevAddonVisible = [];
    private ulong prevActiveRetainerId;
    private string prevMarketFingerprint = string.Empty;
    private readonly Dictionary<ulong, string> prevRetainerSummary = [];

    /// <summary>Chat is logged in full for this long after a login, to catch a replayed backlog (Q1).</summary>
    private DateTime chatWindowUntil = DateTime.MinValue;

    /// <summary>Set by `/emm probe chat on` to log every message regardless of channel.</summary>
    private DateTime chatAllUntil = DateTime.MinValue;

    // Liveness counters, incremented unconditionally before any filter.
    //
    // These exist because the probe spent an hour writing nothing and there was no way to tell
    // "nothing happened" from "the handler is dead" — and Q1, the most load-bearing question on
    // #18, is answered by an ABSENCE of messages. An unexercised listener would turn that absence
    // into a false confirmation of the very hedge the research note already assumes. A non-zero
    // count is the cheapest possible proof the subscription is wired.
    private long chatSeen;
    private long logMessagesSeen;
    private DateTime lastHeartbeat = DateTime.UtcNow;

    public LiveProbe()
    {
        var dir = Plugin.PluginInterface.ConfigDirectory;
        if (!dir.Exists)
        {
            dir.Create();
        }

        logPath = Path.Combine(dir.FullName, "probe.log");

        try
        {
            var stream = new FileStream(
                logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EMM probe could not open its log file");
            writer = null;
        }

        Write("SESSION", $"probe=start log={logPath}");
        Plugin.Framework.Update += OnUpdate;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.ChatGui.LogMessage += OnLogMessage;
        Plugin.ClientState.Login += OnLogin;
        Plugin.ClientState.Logout += OnLogout;
    }

    public string LogPath => logPath;

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        Plugin.ChatGui.LogMessage -= OnLogMessage;
        Plugin.ClientState.Login -= OnLogin;
        Plugin.ClientState.Logout -= OnLogout;

        Write("SESSION", "probe=stop");
        writer?.Dispose();
    }

    /// <summary>
    /// One line per observation: an ISO timestamp, the frame it was seen on, a tag, then fields.
    /// Timestamps are UTC and to the millisecond because Q6 is a question about ordering.
    /// </summary>
    private void Write(string tag, string fields)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} f={frame} {tag} {fields}");
        writer?.WriteLine(line);
        Plugin.Log.Information("[probe] {Line}", line);
    }

    public void Note(string text) => Write("NOTE", text);

    /// <summary>Lets the AutoRetainer half write into the same single ordered timeline.</summary>
    public void WriteEntry(string tag, string fields) => Write(tag, fields);

    private void OnLogin()
    {
        // Q1 hangs on this window: if channel 71 replays an offline backlog, it arrives here.
        // Ten minutes rather than three, because the overnight test costs a whole day to repeat and
        // a window that is merely probably long enough is a bad trade against that.
        chatWindowUntil = DateTime.UtcNow.AddSeconds(600);
        Write("LOGIN", $"character={Describe(Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.CharacterName : "?")} chatWindow=600s");
        DumpRetainers("login");
    }

    private void OnLogout(int type, int code)
    {
        Write("LOGOUT", $"type={type} code={code}");
        DumpRetainers("logout");
    }

    /// <summary>
    /// Q1's evidence. Channel 71 is logged in full always; everything else is logged, thinly, only
    /// inside the post-login window — a replayed backlog that arrived on some OTHER channel would
    /// otherwise be invisible, and "there is no such message" is a claim this ticket has to test
    /// rather than assume.
    /// </summary>
    private void OnChatMessage(IHandleableChatMessage message)
    {
        chatSeen++;

        var type = message.LogKind;
        var isSale = (int)type == 71;
        var now = DateTime.UtcNow;
        if (!isSale && now > chatWindowUntil && now > chatAllUntil)
        {
            return;
        }

        var text = message.Message.TextValue.Replace('\n', ' ');

        if (!isSale)
        {
            var clipped = text.Length > 90 ? text[..90] + "..." : text;
            Write("CHAT-BG", $"type={(int)type}({type}) ts={message.Timestamp} text=\"{clipped}\"");
            return;
        }

        // Item link payloads carry the one attribution the message text does not spell out.
        var items = message.Message.Payloads.OfType<ItemPayload>()
            .Select(p => $"item={p.ItemId}/hq={p.IsHQ}")
            .ToList();

        Write("CHAT-SALE",
            $"type={(int)type} ts={message.Timestamp} tsUtc={FromUnix(message.Timestamp)} " +
            $"sender=\"{message.Sender.TextValue}\" items=[{string.Join(",", items)}] " +
            $"payloads=[{string.Join(",", message.Message.Payloads.Select(p => p.Type.ToString()))}] " +
            $"raw={Convert.ToHexString(message.Message.Encode())} text=\"{text}\"");
    }

    /// <summary>
    /// Q5's answer, and it is a better one than the ticket expected. This Dalamud build exposes the
    /// LogMessage sheet row behind a chat line as ILogMessage.LogMessageId, along with the
    /// parameters the sheet text renders blank. #16 could only say that rows 745 and 748 read
    /// identically and that a live observation was needed; this reports which row actually fired and
    /// what was substituted into it, so the distinction is observed rather than inferred.
    /// </summary>
    private void OnLogMessage(ILogMessage message)
    {
        logMessagesSeen++;

        // Cheap filter: the sale-related rows plus anything at all during the post-login window,
        // since Q1 is precisely the question of whether something unexpected arrives at login.
        var interesting = SaleLogMessageRows.Contains(message.LogMessageId);
        var now = DateTime.UtcNow;
        if (!interesting && now > chatWindowUntil && now > chatAllUntil)
        {
            return;
        }

        // Wrapped because a throw in here would be swallowed by the event dispatcher and present as
        // silence — and on this ticket silence is the thing being measured. Q1 concludes "no
        // backlog replayed" from an absence of lines, so an absence caused by a defect must be
        // impossible to mistake for an absence caused by the game.
        try
        {
            var parameters = new List<string>();
            for (var i = 0; i < message.ParameterCount; i++)
            {
                if (message.TryGetIntParameter(i, out var intValue))
                {
                    parameters.Add($"{i}:int={intValue}");
                }
                else if (message.TryGetStringParameter(i, out var stringValue))
                {
                    parameters.Add($"{i}:str=\"{stringValue.ExtractText()}\"");
                }
                else
                {
                    parameters.Add($"{i}:?");
                }
            }

            Write(interesting ? "LOGMSG-FIRED" : "LOGMSG-BG",
                $"row={message.LogMessageId} paramCount={message.ParameterCount} " +
                $"params=[{string.Join(" ", parameters)}] " +
                $"source=\"{message.SourceEntity?.Name}\" target=\"{message.TargetEntity?.Name}\" " +
                $"debug=\"{message.FormatLogMessageForDebugging()}\"");
        }
        catch (Exception ex)
        {
            // Deliberately minimal: report the row and the failure without touching anything else
            // on the message, since one of those members is what threw.
            Write("LOGMSG-ERROR",
                $"row={message.LogMessageId} {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        frame++;

        try
        {
            PollHeartbeat();
            PollBell();
            PollAddons();
            PollActiveRetainer();
            PollMarketContainer();
            PollRetainerSummaries();
        }
        catch (Exception ex)
        {
            // A probe that throws every frame would flood the log and blind the session it exists
            // to observe, so it reports once and then goes quiet.
            Plugin.Log.Error(ex, "EMM probe poll failed; detaching");
            Write("ERROR", $"poll failed: {ex.GetType().Name}: {ex.Message}");
            Plugin.Framework.Update -= OnUpdate;
        }
    }

    /// <summary>
    /// A periodic line so a quiet log is distinguishable from a dead one. The probe writes
    /// transitions only, which is what keeps it readable — but it also means an hour of silence
    /// reads identically whether nothing happened or the poll stopped running. It carries the
    /// listener counters so the answer to "is the chat subscription alive?" is always in the log
    /// rather than something that has to be asked for.
    /// </summary>
    private void PollHeartbeat()
    {
        var now = DateTime.UtcNow;
        if (now - lastHeartbeat < TimeSpan.FromMinutes(5))
        {
            return;
        }

        lastHeartbeat = now;
        Write("HEARTBEAT", $"{LivenessSummary()} loggedIn={Plugin.ClientState.IsLoggedIn} bell={prevBell}");
    }

    public string LivenessSummary()
        => $"frames={frame} chatSeen={chatSeen} logMessagesSeen={logMessagesSeen}";

    /// <summary>
    /// Q1 is answered by an absence, so the listener has to be shown working BEFORE the absence is
    /// recorded. This logs every chat line and every LogMessage row for a bounded window, which any
    /// ordinary game activity will fill within seconds.
    /// </summary>
    public void SetChatCapture(bool on, int minutes)
    {
        chatAllUntil = on ? DateTime.UtcNow.AddMinutes(minutes) : DateTime.MinValue;
        Write("CHAT-CAPTURE", $"on={on} minutes={(on ? minutes : 0)} {LivenessSummary()}");
    }

    private void PollBell()
    {
        var bell = Plugin.Condition[ConditionFlag.OccupiedSummoningBell];
        if (bell != prevBell)
        {
            prevBell = bell;
            Write("BELL", bell ? "state=open" : "state=closed");
            DumpRetainers(bell ? "bell-open" : "bell-close");
        }
    }

    private void PollAddons()
    {
        foreach (var name in WatchedAddons)
        {
            var visible = IsAddonVisible(name);
            var known = prevAddonVisible.TryGetValue(name, out var was);
            if (known && was == visible)
            {
                continue;
            }

            prevAddonVisible[name] = visible;

            // A first sighting of an addon that is already hidden is the resting state, not an
            // event, so it seeds the baseline silently.
            if (!known && !visible)
            {
                continue;
            }

            Write("ADDON", $"name={name} visible={visible}");
        }
    }

    private static bool IsAddonVisible(string name)
    {
        var addon = Plugin.GameGui.GetAddonByName(name, 1);
        return !addon.IsNull && addon.IsVisible;
    }

    private void PollActiveRetainer()
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
        {
            return;
        }

        var active = manager->GetActiveRetainer();
        var id = active == null ? 0UL : active->RetainerId;
        if (id == prevActiveRetainerId)
        {
            return;
        }

        prevActiveRetainerId = id;
        Write("ACTIVE-RETAINER", active == null
            ? "id=0 name=none"
            : $"id={id} name=\"{active->NameString}\" marketItems={active->MarketItemCount} " +
              $"gil={active->Gil} town={active->Town} expire={FromUnix((int)active->MarketExpire)}");
    }

    /// <summary>
    /// Q3's evidence. The question is whether the RetainerMarket container and the parallel price
    /// array hold real data at the moment the retainer's SelectString menu is up, or only once the
    /// sell list has been opened. Fingerprinting the whole container turns that into a transition
    /// the timeline can be read against the ADDON lines above.
    /// </summary>
    private void PollMarketContainer()
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            return;
        }

        var container = inventory->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null)
        {
            SetMarketFingerprint("container=null");
            return;
        }

        var loaded = container->IsLoaded;
        var occupied = 0;
        var priced = 0;
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot != null && slot->ItemId != 0)
            {
                occupied++;
            }

            if (inventory->GetRetainerMarketPrice((short)i) != 0)
            {
                priced++;
            }
        }

        SetMarketFingerprint($"loaded={loaded} size={container->Size} occupied={occupied} priced={priced}");
    }

    private void SetMarketFingerprint(string fingerprint)
    {
        if (fingerprint == prevMarketFingerprint)
        {
            return;
        }

        prevMarketFingerprint = fingerprint;
        Write("MARKET-CONTAINER", fingerprint);
    }

    /// <summary>
    /// Q2 and Q4's evidence. MarketItemCount and MarketExpire are readable for every retainer at the
    /// bell without opening any of them, so a transition log across a whole session shows both what
    /// moves the expiry clock and whether the count tracks occupied slots.
    /// </summary>
    private void PollRetainerSummaries()
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
        {
            return;
        }

        for (uint i = 0; i < manager->GetRetainerCount(); i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null)
            {
                continue;
            }

            var summary =
                $"marketItems={retainer->MarketItemCount} items={retainer->ItemCount} " +
                $"gil={retainer->Gil} expire={retainer->MarketExpire}";

            if (prevRetainerSummary.TryGetValue(retainer->RetainerId, out var was) && was == summary)
            {
                continue;
            }

            prevRetainerSummary[retainer->RetainerId] = summary;
            Write("RETAINER-DELTA",
                $"name=\"{retainer->NameString}\" id={retainer->RetainerId} {summary} " +
                $"expireUtc={FromUnix((int)retainer->MarketExpire)} was=\"{was ?? "-"}\"");
        }
    }

    /// <summary>Full state of every retainer, on demand and at the session's natural boundaries.</summary>
    public void DumpRetainers(string reason)
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
        {
            Write("RETAINERS", $"reason={reason} manager=null");
            return;
        }

        var count = manager->GetRetainerCount();
        Write("RETAINERS", $"reason={reason} count={count}");

        for (uint i = 0; i < count; i++)
        {
            var r = manager->GetRetainerBySortedIndex(i);
            if (r == null)
            {
                continue;
            }

            Write("RETAINER",
                $"idx={i} name=\"{r->NameString}\" id={r->RetainerId} available={r->Available} " +
                $"town={r->Town} items={r->ItemCount} marketItems={r->MarketItemCount} gil={r->Gil} " +
                $"expire={r->MarketExpire} expireUtc={FromUnix((int)r->MarketExpire)} " +
                $"lapsed={IsLapsed(r->MarketExpire)} ventureId={r->VentureId}");
        }
    }

    /// <summary>
    /// Q2 needs a retainer whose market has lapsed, which is the one observation that cannot be
    /// manufactured on demand. Reporting it per retainer means the session can tell at a glance
    /// whether the observation is available at all.
    /// </summary>
    private static string IsLapsed(uint expire)
        => expire == 0
            ? "no-expiry-set"
            : (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expire ? "LAPSED" : "live");

    /// <summary>
    /// Full per-slot listing state: the exact snapshot the recommended design takes, dumped so the
    /// session can compare MarketItemCount against occupied slots by hand (Q2).
    /// </summary>
    public void DumpMarket(string reason)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Write("MARKET", $"reason={reason} inventoryManager=null");
            return;
        }

        var container = inventory->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null)
        {
            Write("MARKET", $"reason={reason} container=null");
            return;
        }

        var manager = RetainerManager.Instance();
        var active = manager == null ? null : manager->GetActiveRetainer();

        Write("MARKET",
            $"reason={reason} loaded={container->IsLoaded} size={container->Size} " +
            $"activeRetainer=\"{(active == null ? "none" : active->NameString)}\" " +
            $"marketItemCount={(active == null ? -1 : active->MarketItemCount)}");

        var occupied = 0;
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            var price = inventory->GetRetainerMarketPrice((short)i);
            if (slot == null || slot->ItemId == 0)
            {
                Write("MARKET-SLOT", $"slot={i} empty=true price={price}");
                continue;
            }

            occupied++;
            var hq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
            Write("MARKET-SLOT",
                $"slot={i} itemId={slot->ItemId} name=\"{ItemName(slot->ItemId)}\" hq={hq} " +
                $"qty={slot->Quantity} unitPrice={price}");
        }

        Write("MARKET-TOTAL",
            $"occupiedSlots={occupied} marketItemCount={(active == null ? -1 : active->MarketItemCount)} " +
            $"agree={(active != null && active->MarketItemCount == occupied)}");
    }

    /// <summary>
    /// Q5's evidence, and the one item on this ticket that needs no in-world action at all. The
    /// LogMessage sheet renders parameters blank, which is why rows 745 and 748 read identically in
    /// the research note. The raw SeString bytes carry the macro structure the rendering discards,
    /// so dumping them is what actually distinguishes the two rows.
    /// </summary>
    public void DumpLogMessages()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>();
        if (sheet == null)
        {
            Write("LOGMSG", "sheet=null");
            return;
        }

        foreach (var rowId in SaleLogMessageRows)
        {
            if (!sheet.TryGetRow(rowId, out var row))
            {
                Write("LOGMSG", $"row={rowId} missing=true");
                continue;
            }

            var raw = row.Text.Data.ToArray();
            Write("LOGMSG",
                $"row={rowId} text=\"{row.Text.ExtractText()}\" bytes={raw.Length} hex={Convert.ToHexString(raw)}");
        }
    }

    private static string ItemName(uint itemId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        return sheet != null && sheet.TryGetRow(itemId, out var row) ? row.Name.ExtractText() : "?";
    }

    private static string Describe(object? value) => value?.ToString() ?? "?";

    private static string FromUnix(int seconds)
        => seconds <= 0
            ? "-"
            : DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
