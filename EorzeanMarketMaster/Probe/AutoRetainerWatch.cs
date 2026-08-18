using System;
using AutoRetainerAPI;

namespace EorzeanMarketMaster.Probe;

/// <summary>
/// The AutoRetainer half of the #18 observation harness, kept in its own file because it is the only
/// part of the probe that can affect the player's retainer run.
///
/// The safety split, which is the whole reason this class is shaped the way it is:
///
///   OBSERVING is free. Constructing AutoRetainerApi only subscribes to IPC gates
///   (AutoRetainerApi.cs:60-72), and OnRetainerReadyToPostprocess is filtered by plugin name
///   (AutoRetainerApiInternal.cs:39-48) — AutoRetainer will not name EMM as the postprocessor
///   unless EMM asks. So attaching and listening cannot interrupt anything, which is what makes
///   the Q6 timeline observable at zero risk.
///
///   ASKING is not free. RequestRetainerPostprocess() makes AutoRetainer block on
///   FinishRetainerPostProcess() at timeLimitMS: int.MaxValue with its own bailout watchdog
///   disarmed for the duration (#2 §6). A handler that fails to finish hangs the retainer run
///   outright. That is why arming is a separate, explicitly-named command, why the handler does
///   nothing but read, why Finish is in a finally, and why there is a frame deadline underneath
///   the finally as well.
///
/// #14 declined to construct this at all, which was right for a scaffold. #18 is the ticket that
/// has to look inside the window, so it constructs it — and still keeps the risky half switched off
/// by default.
/// </summary>
internal sealed class AutoRetainerWatch : IDisposable
{
    /// <summary>Frames the postprocess window may stay open before the failsafe closes it.</summary>
    private const long PostprocessDeadlineFrames = 600;

    private readonly LiveProbe probe;
    private readonly Action<string, string> write;

    private AutoRetainerApi? api;

    /// <summary>Off until asked for by name. While false, EMM never requests a postprocess turn.</summary>
    public bool Armed { get; private set; }

    private bool inPostprocess;
    private long postprocessDeadline;
    private long frame;

    public AutoRetainerWatch(LiveProbe probe, Action<string, string> write)
    {
        this.probe = probe;
        this.write = write;
    }

    public bool Attached => api != null;

    /// <summary>Subscribes to AutoRetainer's IPC gates. Read-only: see the class comment.</summary>
    public void Attach()
    {
        if (api != null)
        {
            write("AR", "attach=already-attached");
            return;
        }

        try
        {
            api = new AutoRetainerApi();
            api.OnRetainerPostprocessStep += OnPostprocessStep;
            api.OnRetainerReadyToPostprocess += OnReadyToPostprocess;
            api.OnRetainerListTaskButtonsDraw += OnListTaskButtonsDraw;
            api.OnCharacterPostprocessStep += OnCharacterPostprocessStep;

            Plugin.Framework.Update += OnUpdate;

            write("AR", $"attach=ok ready={SafeReady()} suppressed={SafeSuppressed()}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "EMM probe could not attach to AutoRetainer");
            write("AR", $"attach=FAILED {ex.GetType().Name}: {ex.Message}");
            api = null;
        }
    }

    public void Dispose()
    {
        if (api == null)
        {
            return;
        }

        // Leaving a blocked AutoRetainer behind on unload would be the worst possible failure, so
        // the window is closed before the subscription goes away.
        if (inPostprocess)
        {
            write("AR", "dispose=while-in-postprocess finishing=true");
            SafeFinish("dispose");
        }

        Plugin.Framework.Update -= OnUpdate;

        api.OnRetainerPostprocessStep -= OnPostprocessStep;
        api.OnRetainerReadyToPostprocess -= OnReadyToPostprocess;
        api.OnRetainerListTaskButtonsDraw -= OnListTaskButtonsDraw;
        api.OnCharacterPostprocessStep -= OnCharacterPostprocessStep;
        api.Dispose();
        api = null;

        write("AR", "detach=ok");
    }

    public void Arm()
    {
        Armed = true;
        write("AR", "armed=true note=EMM will request a postprocess turn on the next retainer");
    }

    public void Disarm()
    {
        Armed = false;
        write("AR", "armed=false");
    }

    public bool SetSuppressed(bool value)
    {
        if (api == null)
        {
            return false;
        }

        try
        {
            api.Suppressed = value;
            write("AR", $"suppressed=set:{value} readback={SafeSuppressed()}");
            return true;
        }
        catch (Exception ex)
        {
            write("AR", $"suppressed=FAILED {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public string Status()
        => api == null
            ? "AutoRetainer: not attached"
            : $"AutoRetainer: attached, ready={SafeReady()}, suppressed={SafeSuppressed()}, armed={Armed}";

    private string SafeReady()
    {
        try
        {
            return api!.Ready.ToString();
        }
        catch (Exception ex)
        {
            return $"error:{ex.GetType().Name}";
        }
    }

    private string SafeSuppressed()
    {
        try
        {
            return api!.Suppressed.ToString();
        }
        catch (Exception ex)
        {
            return $"error:{ex.GetType().Name}";
        }
    }

    /// <summary>
    /// AutoRetainer offering a turn on this retainer. Q6's key timestamp: everything AutoRetainer
    /// did at the bell happened before this line.
    /// </summary>
    private void OnPostprocessStep(string retainerName)
    {
        write("AR-STEP", $"retainer=\"{retainerName}\" armed={Armed}");
        probe.DumpMarket("ar-postprocess-step");

        if (!Armed)
        {
            return;
        }

        try
        {
            api!.RequestRetainerPostprocess();
            write("AR-STEP", $"retainer=\"{retainerName}\" requested=true");
        }
        catch (Exception ex)
        {
            write("AR-STEP", $"retainer=\"{retainerName}\" request=FAILED {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Q3's answer, measured at the exact instant the ticket asks about. AutoRetainer is now blocked
    /// and stays blocked until Finish is called, so this reads and gets out — nothing else belongs
    /// in here.
    /// </summary>
    private void OnReadyToPostprocess(string retainerName)
    {
        inPostprocess = true;
        postprocessDeadline = frame + PostprocessDeadlineFrames;

        try
        {
            write("AR-READY", $"retainer=\"{retainerName}\" window=open");
            probe.DumpMarket("ar-ready-to-postprocess");
        }
        catch (Exception ex)
        {
            write("AR-READY", $"retainer=\"{retainerName}\" snapshot=FAILED {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SafeFinish("handler");
        }
    }

    /// <summary>
    /// Draws EMM's button into AutoRetainer's bell overlay.
    ///
    /// This exists because Q6 cannot be observed otherwise: AutoRetainer's own loop only acts when
    /// a Retainer has something due, so with every Retainer already out on a venture it does
    /// nothing at the bell and there is no sequence to watch. ProcessIPCTaskFromOverlay() makes it
    /// walk EVERY Retainer in the list and run the calling plugin's task regardless of venture
    /// state, which is exactly the forced run this ticket needs — and it is also the overlay path
    /// #9's aggregate bell view is specified against, so it is worth having working either way.
    ///
    /// The API is explicit that it may only be called from inside this event, which is why the
    /// click is handled here rather than routed out to a command.
    /// </summary>
    private void OnListTaskButtonsDraw()
    {
        var firstFrame = prevListDrawFrame != frame - 1;
        prevListDrawFrame = frame;

        if (firstFrame)
        {
            write("AR-OVERLAY", "onRetainerListTaskButtonsDraw=first-frame");
        }

        if (Dalamud.Bindings.ImGui.ImGui.Button(Armed
                ? "EMM: snapshot every retainer (armed)"
                : "EMM: walk every retainer (observe only)"))
        {
            write("AR-OVERLAY", $"processIPCTaskFromOverlay=requested armed={Armed}");

            try
            {
                api!.ProcessIPCTaskFromOverlay();
            }
            catch (Exception ex)
            {
                write("AR-OVERLAY", $"processIPCTaskFromOverlay=FAILED {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private long prevListDrawFrame = -10;

    private void OnCharacterPostprocessStep() => write("AR-CHAR-STEP", "characterPostprocessStep=offered");

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        frame++;

        if (!inPostprocess || frame < postprocessDeadline)
        {
            return;
        }

        // Reaching here means the finally did not run or Finish did not take. Say so loudly: a
        // silent recovery would hide exactly the defect this failsafe exists to catch.
        write("AR-READY", $"window=DEADLINE-EXCEEDED frames={PostprocessDeadlineFrames} forcing=finish");
        SafeFinish("deadline");
    }

    private void SafeFinish(string source)
    {
        inPostprocess = false;

        try
        {
            api!.FinishRetainerPostProcess();
            write("AR-READY", $"window=closed by={source}");
        }
        catch (Exception ex)
        {
            write("AR-READY", $"window=FINISH-FAILED by={source} {ex.GetType().Name}: {ex.Message}");
        }
    }
}
