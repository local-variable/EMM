# AutoRetainer IPC surface — can it be EMM's "hands"?

Research for issue #2. Investigated 2026-08-17.

## Sources and pins

Everything below is read from source at the exact commits that produced the installed build,
cross-checked against the shipped binaries.

| Artifact | Pin | Date |
| --- | --- | --- |
| Installed plugin `AutoRetainer` 4.6.1.27 | `%APPDATA%\XIVLauncher\installedPlugins\AutoRetainer\4.6.1.27\` | DLL mtime 2026-08-09T20:43 local |
| `PunishXIV/AutoRetainer` master | `c281a92` | 2026-08-08T15:56:20+03:00 |
| `PunishXIV/AutoRetainerAPI` (submodule pin **and** repo HEAD) | `7ccf0f6b4c7923821a43ed1e92456c9d5d7132f2` | 2026-08-08T04:02:29+03:00 |
| `NightmareXIV/ECommons.IPC` (submodule pin) | `90986f272fbc45b2d7db0771a9b884d5e69ae654` | 2026-08-08T15:56:09+03:00 |
| `NightmareXIV/ECommons` (submodule pin) | `e6be8f0fd7786a9e1781db2e71cb5b9146f04980` | — |

Submodule pins read from `git ls-tree c281a92 AutoRetainerAPI ECommons.IPC ECommons`.

**Binary verification.** A UTF-16 scan of the shipped `AutoRetainerAPI.dll` (both byte alignments)
yields exactly 25 `AutoRetainer.*` IPC tag strings plus the `AutoRetainer.GetConfig.*` family. The
set matches `ApiConsts.cs` + `AutoRetainerApi.cs` at `7ccf0f6` with no additions and no omissions,
so **the source below is the shipped 4.6.1.27 surface**, not a newer master.

Licence: BSD 3-Clause (`AutoRetainer/LICENCE.md`). The README adds a non-binding usage guideline
that AutoRetainer is for non-commercial use only and that products produced with its help may not
be sold — relevant only if EMM were ever monetised.

---

## 1. Can an external plugin SUPPLY prices? — **No.**

There is no price parameter anywhere in the IPC surface. The complete set of write/injection
endpoints AutoRetainer exposes is:

```
AutoRetainer.SetVenture               (uint ventureId)          -> void
AutoRetainer.SetSuppressed            (bool)                    -> void
AutoRetainer.SetMultiModeEnabled      (bool)                    -> void
AutoRetainer.WriteOfflineCharacterData(OfflineCharacterData)    -> void
AutoRetainer.WriteAdditionalRetainerData(ulong cid, string name, AdditionalRetainerData) -> void
```

Source: `AutoRetainer/Modules/IPC.cs:21-36` (provider registration) —
<https://github.com/PunishXIV/AutoRetainer/blob/c281a92/AutoRetainer/Modules/IPC.cs#L21-L36>

`SetVenture` is the only "override AutoRetainer's own decision with my computed value" hook that
exists, and it applies to venture IDs, not gil:

```csharp
// AutoRetainerAPI/AutoRetainerApi.cs:161-164
public void SetVenture(uint ventureId)
    => Svc.PluginInterface.GetIpcSubscriber<uint, object>("AutoRetainer.SetVenture").InvokeAction(ventureId);
```

Neither `OfflineRetainerData` nor `AdditionalRetainerData` carries any price, listing, or
marketboard field. `OfflineRetainerData` (`AutoRetainerAPI/Configuration/OfflineRetainerData.cs:14-23`)
has `Name, VentureEndsAt, HasVenture, Level, VentureBeginsAt, Job, VentureID, Gil, RetainerID,
MBItems` — `MBItems` is a plain **count** of market listings, populated from
`ret.MarkerItemCount` at `AutoRetainer/Modules/OfflineDataManager.cs:142`. No item IDs, no prices.

---

## 2. Does AutoRetainer already undercut / adjust prices? — **No. There is nothing to override, feed, or bypass.**

A case-insensitive search across the whole plugin for `undercut|askingprice|adjustprice|
compareprice|marketboard` returns only three touchpoints, none of which set a price:

1. **Market cooldown overlay.** `AutoRetainer/Internal/Memory.cs:27,78-81` hooks
   `PacketDispatcher.Delegates.HandleMarketBoardItemRequestStartPacket` solely to set
   `P.MarketCooldownOverlay.UnlockAt = Environment.TickCount64 + 2000` — a 2-second visual timer.
   Disabled unless `C.MarketCooldownOverlay` is on (`Memory.cs:48`).
2. **`QuickSellItems`.** A hotkey (`C.SellMarketKey`) that auto-clicks the game's "Put up For Sale"
   inventory context-menu entry (`AutoRetainer/Modules/QuickSellItems.cs:108-111`, entry click at
   `:127-134`). It opens the game's own price dialog and stops there.
3. **`HaveRetainerSellItem`.** `AutoRetainer/Internal/InventoryManagement/InventorySpaceManager.cs:59`
   with `RetainerItemCommand.HaveRetainerSellItem = 5` — this is the **NPC vendor** sell path for
   junk clearing, not a marketboard listing.

So EMM does not override, feed, or bypass an existing pricing engine. **There is no pricing engine
in AutoRetainer at all.** This is a hole EMM fills, not a system EMM contends with.

---

## 3. Can an external plugin TRIGGER or ENQUEUE a sell/re-list cycle, and observe the result?

**Trigger: yes, coarsely. Enqueue a specific sale: no. Observe the result: no.**

### 3a. The postprocess handshake — the load-bearing mechanism

This is the only way an external plugin gets control of an open retainer. Full API surface
(`AutoRetainerAPI/AutoRetainerApi.cs`):

```csharp
// events, AutoRetainerApi.cs:16-56
public event OnSendRetainerToVentureDelegate      OnSendRetainerToVenture;      // (string retainerName)
public event OnRetainerPostprocessTaskDelegate    OnRetainerPostprocessStep;    // (string retainerName)
public event OnRetainerReadyToPostprocessDelegate OnRetainerReadyToPostprocess; // (string retainerName)
public event OnRetainerSettingsDrawDelegate       OnRetainerSettingsDraw;       // (ulong CID, string retainerName)
public event OnRetainerPostVentureTaskDrawDelegate OnRetainerPostVentureTaskDraw;// (ulong CID, string retainerName)
public event OnRetainerListTaskButtonsDrawDelegate OnRetainerListTaskButtonsDraw;// ()
public event OnCharacterPostprocessTaskDelegate   OnCharacterPostprocessStep;   // ()
public event OnCharacterReadyToPostprocessDelegate OnCharacterReadyToPostProcess;// ()
public event OnMainControlsDrawDelegate           OnMainControlsDraw;           // ()

// control methods, AutoRetainerApi.cs:77-105
public void ProcessIPCTaskFromOverlay();     // only valid inside OnRetainerListTaskButtonsDraw
public void RequestRetainerPostprocess();    // only valid inside OnRetainerPostprocessStep
public void FinishRetainerPostProcess();     // only valid inside OnRetainerReadyToPostprocess
public void RequestCharacterPostprocess();   // only valid inside OnCharacterPostprocessStep
public void FinishCharacterPostProcess();    // only valid inside OnCharacterReadyToPostProcess
```

Delegate signatures verbatim from `AutoRetainerAPI/Delegates.cs:5-14`.

The two-phase handshake, from `AutoRetainer/Scheduler/Tasks/TaskPostprocessRetainerIPC.cs`:

```
AR: clears the opt-in list, fires OnRetainerAdditionalTask(retainerName)      (:9-11)
EMM: (inside OnRetainerPostprocessStep) calls RequestRetainerPostprocess()
     -> AutoRetainer.RequestPostprocess(PluginName), appends to SchedulerMain.RetainerPostprocess
AR: for each opted-in plugin, sets RetainerPostProcessLocked = true and fires
    OnRetainerReadyForPostprocess(pluginName, retainerName)                    (:20-25)
AR: enqueues a blocking wait `() => !RetainerPostProcessLocked` with
    `new(timeLimitMS: int.MaxValue)`                                           (:26)
EMM: drives the game UI, then calls FinishRetainerPostProcess()
     -> AutoRetainer.FinishPostprocessRequest, sets RetainerPostProcessLocked = false
```

`PluginName` is `Svc.PluginInterface.InternalName` plus an optional constructor suffix
(`AutoRetainerApi.cs:60-62`). AutoRetainer broadcasts the ready event to everyone; each subscriber
self-filters on its own name (`AutoRetainerApiInternal.cs:39-48`). Multiple plugins are therefore
supported and are serialised one at a time.

### 3b. What UI state EMM inherits

The retainer postprocess call site is `AutoRetainer/Scheduler/SchedulerMain.cs:193`, sequenced:

```
… venture reassign / entrust / withdraw gil / auto-vendor …
TaskPostprocessRetainerIPC.Enqueue(retainer);       // :193  <- EMM's window
if (C.RetainerMenuDelay > 0) TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
P.TaskManager.Enqueue(RetainerHandlers.SelectQuit);  // :199
P.TaskManager.Enqueue(RetainerHandlers.ConfirmCantBuyback);
```

EMM is handed the retainer **already selected, with its SelectString menu open**, immediately
before AutoRetainer quits out. This is exactly the state from which "Sell items" / the market
listing UI is reachable. The API doc comment is explicit about the contract
(`AutoRetainerApi.cs:24`): *"You must return game UI into the state from where you picked it up."*

### 3c. Ways to start a cycle

| Mechanism | Signature | Reach | Notes |
| --- | --- | --- | --- |
| Ambient — AutoRetainer's own scheduler | n/a | every enabled retainer as ventures come due | postprocess fires automatically once EMM opts in |
| `ProcessIPCTaskFromOverlay()` | `AutoRetainerApi.cs:77` -> `AutoRetainer.OnRetainerListCustomTask(PluginName)` | all `Available` retainers of the **current** character | must be called from inside `OnRetainerListTaskButtonsDraw`; that overlay only draws when `C.UIBar` is on and the player is at a summoning bell with `RetainerList` ready (`RetainerListOverlay.cs:23-32`); loop at `:267-285` |
| `PluginState.EnableMultiMode` / `EnableSingleMultiMode(MultiModeType?)` | ECommons.IPC `AutoRetainerIPC.cs:28,41` -> `IPC_PluginState.cs:52-64` | full multi-character run | equivalent to `/autoretainer multi enable` |
| `PluginState.EnqueueHET` | provider `IPC_PluginState.cs:78-82` | "Home / Enable / Teleport" run | **see the signature-drift warning in §5** |
| `AutoRetainer.SetMultiModeEnabled(bool)` | `IPC.cs:25` | full multi-character run | plain Dalamud IPC, no wrapper needed |

**None of these can say "sell item X for Y gil".** They start AutoRetainer's own itinerary and give
EMM a window per retainer.

### 3d. Observing the result — **nothing comes back**

`FinishRetainerPostProcess()` is `Action` (void, no arguments) — EMM tells AutoRetainer it is done;
AutoRetainer never asks whether it worked and has no channel to report back. There is no
`Func<bool>`, no completion event, no error callback anywhere in the surface. EMM must observe its
own results by reading the game state directly.

---

## 4. Events published — the gaps that matter

Complete list is the nine delegates in §3a. Explicitly **absent**:

- **No "retainer sold" event.** No sale notification of any kind. The nearest data is
  `OfflineRetainerData.Gil` and `MBItems` (a listing count), both refreshed by
  `OfflineDataManager` only while the retainer is open.
- **No "venture complete" event.** `OnSendRetainerToVenture(string retainerName)` fires *before*
  assignment (it exists so a plugin can call `SetVenture`), never on return.
- **No "listing changed" event.** No listings are modelled at all.

`OnRetainerSettingsDraw(ulong, string)`, `OnRetainerPostVentureTaskDraw(ulong, string)`,
`OnRetainerListTaskButtonsDraw()` and `OnMainControlsDraw()` are ImGui draw callbacks that let EMM
inject widgets into AutoRetainer's own windows — useful for per-retainer configuration UI, not for
state notification.

---

## 5. Per-character or global? Multi-character behaviour

**The IPC endpoints are process-global** — one AutoRetainer instance per Dalamud, one provider per
tag. Character scoping is carried in the payloads, and inconsistently:

- Data accessors are keyed by content ID:
  ```csharp
  public List<ulong>            GetRegisteredCharacters();                          // AutoRetainerApi.cs:213
  public OfflineCharacterData   GetOfflineCharacterData(ulong cid);                 // :171
  public void                   WriteOfflineCharacterData(OfflineCharacterData);    // :180
  public AdditionalRetainerData GetAdditionalRetainerData(ulong cid, string name);  // :192
  public void WriteAdditionalRetainerData(ulong cid, string name, AdditionalRetainerData); // :203
  ```
  `GetRegisteredCharacters` excludes blacklisted and uninitialised characters
  (`IPC.cs:98-101`).
- **Retainer events carry only the retainer's name string** — no CID. `OnRetainerPostprocessStep`
  and `OnRetainerReadyToPostprocess` are `(string retainerName)`. EMM must read
  `Svc.ClientState.LocalContentId` itself to know which character it is acting for.
- **Character events carry nothing at all.** `OnCharacterPostprocessStep()` and
  `OnCharacterReadyToPostProcess()` are parameterless.
- Retainer identity within a character is therefore the **name**, not `RetainerID` — even though
  `OfflineRetainerData.RetainerID` exists. `AdditionalRetainerData` is keyed
  `(cid, retainerName)` throughout.

Multi-character flow: MultiMode relogs between characters and fires the character postprocess
before logout, at `AutoRetainer/Modules/Multi/MultiMode.cs:600-602`, gated on
`reason == RelogReason.MultiMode || C.AllowManualPostprocess`. So a full MultiMode sweep yields, per
character: N retainer-postprocess windows (one per processed retainer, at the bell) then one
character-postprocess window (just before logout, not at a bell).

**Mutation contract.** Both write methods throw unless read-modify-write happens inside a single
framework tick:

```csharp
if (data.CreationFrame != Svc.PluginInterface.UiBuilder.FrameCount)
    throw new Exception("You must read the data, make changes and immediately write it back within
                         single framework update, storing OfflineCharacterData is prohibited.");
```
`AutoRetainerApi.cs:182` and `:205`. On the provider side the write is a reflective field-by-field
copy (`IPC.cs:108-146`), so unknown/renamed fields are silently skipped rather than erroring.

---

## 6. Versioning and stability — what breaks on AutoRetainer updates

**There is no version handshake.** The only capability probe is presence-based:

```csharp
// AutoRetainerApi.cs:110-124
public bool Ready {
  get { try { Svc.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.Init").InvokeAction();
              return true; }
        catch (Exception) { return false; } }
}
```

`AutoRetainer.Init` is a no-op registered purely as a liveness beacon (`IPC.cs:21`). It answers
"is AutoRetainer loaded", not "which API version".

Distribution and pinning:

- **`AutoRetainerAPI` is not on NuGet** — `https://api.nuget.org/v3-flatcontainer/autoretainerapi/index.json`
  returns 404 (checked 2026-08-17). It must be vendored as a git submodule / `ProjectReference`, so
  EMM pins a source commit and rebuilds to move. `AssemblyVersion` is `1.0.0.4`
  (`AutoRetainerAPI/AutoRetainerAPI.csproj`), and that number has not tracked the surface.
- **`ECommons.IPC` *is* on NuGet** — versions `1.0.0` … `1.0.0.24`, latest published 2026-08-13.
  AutoRetainer 4.6.1.27 ships the `1.0.0.23` line (`BaseVersion` in `ECommons.IPC.csproj` at the
  pinned commit). EMM would reference its own copy; the shipped DLL is irrelevant to EMM's build.
- Both projects target `net10.0-windows7.0` and depend on `ECommons` NuGet `3.2.0.11`.

**Observed churn is low.** `git log` on the three surface files
(`ApiConsts.cs`, `AutoRetainerApi.cs`, `Delegates.cs`) in the AutoRetainerAPI repo:

```
e686cc9  2026-01-17  Update api          <- added `public readonly AutoRetainerConfig Config`
6c16d52  2025-08-21  Update API
069cf98  2024-11-14  7.1
1c8745e  2024-10-05  Change api name
9db3160  2024-10-04  Update API
278bae0  2023-12-13  Add retainer ID
```

Six edits in ~2.5 years, and the most recent was purely additive. The postprocess handshake has not
changed shape in that window.

### Concrete drift already present in the shipped tree

`ECommons.IPC`'s AutoRetainer subscriber declares:

```csharp
[EzIPC("PluginState.EnqueueHET")] public Action<bool, bool> EnqueueHET { get; private set; }
```
(`ECommons.IPC/Subscribers/AutoRetainer/AutoRetainerIPC.cs:31`, identical at the pinned commit
`90986f2` and at main HEAD `43bbc79`.)

AutoRetainer's provider is:

```csharp
[EzIPC] public void EnqueueHET(Action onFailure) => TaskNeoHET.Enqueue(onFailure);
```
(`AutoRetainer/Modules/EzIPCManagers/IPC_PluginState.cs:78-82`, unchanged since `baa934d`
2026-03-10.)

`EzIPC.Init` builds the provider generic args from the method's parameter types plus a trailing
`object` (`ECommons/EzIpcManager/EzIPC.cs`, provider branch), so AutoRetainer registers
`GetIpcProvider<Action, object>("AutoRetainer.PluginState.EnqueueHET")` while the subscriber
resolves `GetIpcSubscriber<bool, bool, object>` — a Dalamud type mismatch that throws on invoke.
**Treat the ECommons.IPC convenience wrapper as advisory, not authoritative.** Verify each endpoint
against `IPC_PluginState.cs` before relying on it.

### Failure modes EMM must design for

1. **Indefinite block.** The postprocess wait uses `timeLimitMS: int.MaxValue`
   (`TaskPostprocessRetainerIPC.cs:26`). If EMM throws or never calls `FinishRetainerPostProcess()`,
   AutoRetainer hangs at the retainer.
2. **Bailout is suppressed while EMM holds the lock.** `AutoRetainer/Modules/BailoutManager.cs:44`
   only arms when `!SchedulerMain.CharacterPostProcessLocked && !SchedulerMain.RetainerPostProcessLocked`.
   AutoRetainer's own stuck-detection safety net is off during EMM's window. EMM owns its own
   watchdog, and `FinishRetainerPostProcess()` must be in a `finally`.
3. **Duplicate opt-in throws.** `RequestRetainerPostprocess()` throws server-side if the same plugin
   name is already in the list (`IPC.cs:80-83`).
4. **Suppression.** `Suppressed` (`AutoRetainerApi.cs:129-139`, `AutoRetainer.GetSuppressed` /
   `AutoRetainer.SetSuppressed`) stops AutoRetainer performing any action regardless of config —
   useful for EMM to hold AutoRetainer off, and a state EMM must not leave set.
5. **Config reads are one IPC call per property.** `AutoRetainerConfig` exposes ~190 properties, each
   a separate `AutoRetainer.GetConfig.<Name>` round trip (`AutoRetainerAPI/AutoRetainerConfig.cs`).
   Cheap individually, not something to poll in a draw loop.

---

## Verdict

**The brain/hands split is feasible, but "hands" is the wrong word for what AutoRetainer provides,
and the plan should be restated before #9 and #10 are scoped.**

AutoRetainer will not list, re-list, or re-price anything, at any price, supplied or self-computed.
It has no marketboard pricing code whatsoever. What it *does* provide is genuinely valuable and is
the hard, boring part of the problem:

- it schedules and sequences work across every registered character and retainer;
- it relogs, teleports, walks to the bell, and opens each retainer;
- it then **hands EMM a fully-opened retainer, on the SelectString menu, and blocks indefinitely**
  until EMM says it is finished.

So the accurate division is: **AutoRetainer = legs and scheduler; EMM = brain *and* hands.** EMM
computes the price *and* drives the `RetainerSellList` / listing UI itself — via ECommons
`AddonMaster` / `Callback` or equivalent — inside the postprocess window. That is additional scope
versus the assumption in the map, but it is not a fallback: no game-UI automation outside
AutoRetainer's window is needed, no separate navigation or login automation is needed, and the
advisory-only v0.1 retreat is not required.

Two things also fall out of this that were not asked but change the shape of the work:

- **EMM must own its own sale/result telemetry.** There is no sold event, no venture-complete event,
  and no listing-changed event. Any "what sold, for how much" feature is EMM's own bookkeeping.
- **The convenience wrapper cannot be trusted blind.** `ECommons.IPC`'s AutoRetainer subscriber
  already carries at least one signature that does not match the live provider. Bind against
  `AutoRetainerAPI` (vendored at a pinned commit) plus hand-verified `PluginState.*` tags, and gate
  every call on `AutoRetainerApi.Ready`.
