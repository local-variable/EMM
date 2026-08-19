using System;
using AutoRetainerAPI;
using EorzeanMarketMaster.Core.Holdings;

namespace EorzeanMarketMaster.Holdings;

/// <summary>
/// Reads every Retainer by having the automation plugin walk the list, one Retainer at a time.
///
/// <b>This is the only way to read every Retainer's contents, and the reason is the game.</b> A
/// Retainer's stock and Listings are loaded when that Retainer is opened and at no other time, so
/// "refresh everything" is necessarily "open everything". EMM does not drive the game itself: it
/// asks AutoRetainer to walk the list and take a turn on each, and reads inside the window
/// AutoRetainer hands over. That is the Assisted tier working exactly as the capability ruling
/// intended - EMM acts inside a window another plugin holds open, and without that plugin it drops
/// back to what it can do alone rather than failing.
///
/// <b>It also fixes the thing that was going wrong.</b> A Player opening Retainers by hand to let
/// EMM read them was fighting AutoRetainer's watchdog, which sees an idle SelectString and closes
/// it - the "[Bailout] Closing stuck SelectString window" line. Inside a postprocess window that
/// watchdog is disarmed and AutoRetainer is the one driving, so nothing is stuck and nothing gets
/// closed out from under anybody.
///
/// <b>The dangerous half, stated plainly.</b> RequestRetainerPostprocess makes AutoRetainer block
/// on FinishRetainerPostProcess with <c>timeLimitMS: int.MaxValue</c> and suppress its own bailout
/// for the whole window. A handler that fails to finish hangs the Player's retainer run outright.
/// So: the window is closed in a <c>finally</c>, there is a frame deadline underneath the finally,
/// the deadline is enforced from the framework update rather than from anything that might not run,
/// and closing it on unload happens before the subscription goes away.
///
/// <b>It reads and nothing else.</b> No listing, no repricing, no writes of any kind - those are
/// the write-path ticket's, behind Mandates and guardrails this has none of.
///
/// Do not run <c>/emm probe arm</c> while this exists: the observation harness constructs its own
/// api and would request the same window, and two claimants on one handover is a fight neither wins.
/// </summary>
internal sealed class RetainerWalk : IDisposable
{
    /// <summary>
    /// Frames a single Retainer's window may stay open before EMM gives up and hands it back.
    ///
    /// Two seconds at sixty frames. It is a deadline rather than an instant read because the guard
    /// EMM reads behind needs a moment: the Retainer's item container reflects a change immediately
    /// and its parallel price array follows about fifty milliseconds later, so a read taken on the
    /// first frame of the window catches prices that belong to the previous Retainer. Spending a
    /// few frames waiting for the two to agree is the difference between a Listing stored at its
    /// asking price and one stored at somebody else's.
    /// </summary>
    private const long WindowFrames = 120;

    /// <summary>Frames without a Retainer offered before a walk is considered over.</summary>
    private const long QuietFrames = 600;

    private readonly Func<string?> character;
    private readonly Action<HoldingsReading> keep;

    private AutoRetainerApi? api;

    private bool wanted;
    private bool inWindow;
    private long frame;
    private long deadline;
    private long lastStep;
    /// <summary>The Retainer whose window is open. Never null, so a log line always names one.</summary>
    private string reading = "?";

    internal RetainerWalk(Func<string?> character, Action<HoldingsReading> keep)
    {
        this.character = character;
        this.keep = keep;
    }

    /// <summary>Whether the automation plugin is there to walk the list at all.</summary>
    internal bool Available => api is not null;

    /// <summary>Whether a walk is running, or waiting for the overlay to start one.</summary>
    internal bool Walking { get; private set; }

    /// <summary>Retainers read in full during the current or last walk.</summary>
    internal int Read { get; private set; }

    /// <summary>Retainers whose window opened and closed without a usable reading.</summary>
    internal int Missed { get; private set; }

    /// <summary>
    /// Subscribes to the handover gates.
    ///
    /// Free, and that is worth knowing rather than assuming: constructing the api only subscribes,
    /// and AutoRetainer filters the postprocess offer by plugin name - it will not name EMM as a
    /// postprocessor unless EMM asks, which happens in <see cref="OnStep"/> and only during a walk.
    /// So attaching cannot interrupt anything.
    /// </summary>
    internal void Attach()
    {
        if (api is not null)
        {
            return;
        }

        try
        {
            api = new AutoRetainerApi();
            api.OnRetainerListTaskButtonsDraw += OnOverlayDraw;
            api.OnRetainerPostprocessStep += OnStep;
            api.OnRetainerReadyToPostprocess += OnWindowOpen;

            Plugin.Framework.Update += OnUpdate;

            Plugin.Log.Information("EMM holdings: attached to AutoRetainer for the Retainer walk");
        }
        catch (Exception ex)
        {
            // Not installed, not loaded, or its interface has moved. EMM drops to what it can do
            // alone; it does not fail, and it does not pretend the control does more than it can.
            Plugin.Log.Information(
                "EMM holdings: AutoRetainer is unavailable, so refreshing reads the list only ({Reason})",
                ex.Message);

            api = null;
        }
    }

    /// <summary>
    /// Asks for a walk. It starts on the next frame AutoRetainer draws its bell overlay.
    ///
    /// Deferred rather than started here because the call that starts it may only be made from
    /// inside that overlay's draw callback - AutoRetainer's own documented constraint. So the press
    /// sets a flag and the overlay fires it, which is a frame or two later and needs the Retainer
    /// list to be up. That is the same condition the control is already gated on.
    /// </summary>
    /// <returns>Whether the request was taken.</returns>
    internal bool Request()
    {
        if (api is null || Walking)
        {
            return false;
        }

        wanted = true;
        Walking = true;
        Read = 0;
        Missed = 0;
        lastStep = frame;

        return true;
    }

    /// <summary>Stops asking for turns. Retainers still to come are handed straight back.</summary>
    internal void Stop()
    {
        wanted = false;
        Walking = false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (api is null)
        {
            return;
        }

        // Before the subscription goes away, and it is the most important line in the file:
        // leaving a blocked AutoRetainer behind on unload would hang the Player's retainer run
        // with nothing left running that could release it.
        if (inWindow)
        {
            Plugin.Log.Warning("EMM holdings: unloading while holding a Retainer window - closing it");
            Close("unload");
        }

        Plugin.Framework.Update -= OnUpdate;

        api.OnRetainerListTaskButtonsDraw -= OnOverlayDraw;
        api.OnRetainerPostprocessStep -= OnStep;
        api.OnRetainerReadyToPostprocess -= OnWindowOpen;
        api.Dispose();
        api = null;
    }

    /// <summary>
    /// AutoRetainer drawing its overlay on the Retainer list. The one place a walk may be started.
    /// </summary>
    private void OnOverlayDraw()
    {
        if (!wanted)
        {
            return;
        }

        wanted = false;

        try
        {
            api!.ProcessIPCTaskFromOverlay();
            lastStep = frame;

            Plugin.Log.Information("EMM holdings: asked AutoRetainer to walk every Retainer");
        }
        catch (Exception ex)
        {
            Walking = false;
            Plugin.Log.Error(ex, "EMM holdings: AutoRetainer refused to start the walk");
        }
    }

    /// <summary>
    /// A Retainer is open and AutoRetainer is offering a turn on it. Claiming the turn is what
    /// makes it block, so it is claimed only while a walk the Player asked for is running.
    /// </summary>
    private void OnStep(string retainerName)
    {
        lastStep = frame;

        if (!Walking)
        {
            return;
        }

        try
        {
            api!.RequestRetainerPostprocess();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EMM holdings: could not claim a turn on {Retainer}", retainerName);
        }
    }

    /// <summary>
    /// The window is open. AutoRetainer is now blocked until it is handed back.
    ///
    /// Nothing is read here. The read happens on the framework update, across as many frames as the
    /// deadline allows, because the price array needs a moment to agree with the item container and
    /// a single-frame attempt would fail on exactly the Retainers that had just changed.
    /// </summary>
    private void OnWindowOpen(string retainerName)
    {
        lastStep = frame;
        inWindow = true;
        deadline = frame + WindowFrames;
        reading = retainerName;
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        frame++;

        if (Walking && frame - lastStep > QuietFrames)
        {
            Walking = false;
            Plugin.Log.Information(
                "EMM holdings: the Retainer walk finished - {Read} read, {Missed} missed", Read, Missed);
        }

        if (!inWindow)
        {
            return;
        }

        try
        {
            if (character() is { Length: > 0 } who
                && GameHoldings.OpenRetainer(who, DateTimeOffset.UtcNow) is { Reading: { } read })
            {
                keep(read);
                Read++;
                Close("read");
                return;
            }

            // Not readable yet. Every frame up to the deadline is another chance for the price
            // array to catch up with the item container, which is the whole reason for a deadline
            // rather than one attempt.
            if (frame < deadline)
            {
                return;
            }

            Missed++;
            Plugin.Log.Warning(
                "EMM holdings: {Retainer} was not readable inside its window and was skipped", reading);
            Close("deadline");
        }
        catch (Exception ex)
        {
            // Nothing may escape this method while the window is open. AutoRetainer is blocked
            // until it is handed back, with its own bailout suppressed, so an exception that got
            // past here would leave the Player's retainer run stopped with nothing left to release
            // it. Closing costs a Retainer's reading; not closing costs the run.
            Missed++;
            Plugin.Log.Error(ex, "EMM holdings: reading {Retainer} threw inside its window", reading);
            Close("threw");
        }
    }

    private void Close(string why)
    {
        inWindow = false;

        try
        {
            api!.FinishRetainerPostProcess();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EMM holdings: handing the Retainer window back failed ({Why})", why);
        }
    }
}
