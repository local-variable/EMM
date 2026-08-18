# EMM — Eorzean Market Master

[![Support EMM on Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/localvariable)

Strategy-driven pricing and automatic intelligent relisting across every retainer.

Configure a pricing strategy once — undercut behaviour, a price floor, a minimum margin over
what the item cost you — assign it to a ware or a group of wares, and EMM applies it across
every retainer on every character you play. It lists, reprices and relists on its own, so upkeep
is limited to enrolling new wares rather than revisiting old ones.

Prices are drawn from the live marketboard and from Universalis, and every figure carries its age
and the number of sales behind it. A price resting on three sales from a fortnight ago is
labelled as such rather than quoted as a price. EMM also records what you paid, so it can report
profit and not merely revenue.

> **Status: v0.1 is in development, and nothing is published yet.** The repository link below is
> the address EMM will be served from, and it will not resolve until the first release — adding
> it today just fails. It is written down now so that setup is a single paste when v0.1 lands.
> Building from source works today; the current build loads and draws its window shell, with no
> pricing engine and no write path behind it.

| EMM is written from original code, or reuses open source code as its licence permits. It contains no code taken or reverse engineered from the game. You will need XIVLauncher, or compatible software, to run Dalamud plugins at all. |
| --- |

## Installation

1. Install [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher) and enable Dalamud in
   its settings. The game has to be launched through XIVLauncher for any plugin to load.
2. Open Dalamud's settings by typing `/xlsettings` in the game's chat.
3. Go to the **Experimental** tab.
4. Find the **Custom Plugin Repositories** section. The first time you add one, Dalamud shows a
   warning whose button is disabled for a few seconds — that is deliberate, not a hang. Read it,
   wait for the countdown, then press **Ok, I have read and understood this warning**.
5. Paste this link into the empty text field at the bottom of the repository list:

   ```
   https://raw.githubusercontent.com/local-variable/EMM/main/repo.json
   ```

6. Press the **+** button to the right of the field. Check that the new row's **Enabled** box is
   ticked, then press **Save and close**.

Open the plugin installer with `/xlplugins`, search for **Eorzean Market Master**, and install it.
Type `/emm` to open the window.

Updates arrive through the same installer once the repository is added; there is nothing further
to paste.

Until the first release exists, step 5's link returns a 404 and Dalamud will report the repository
as failed. That is expected, and the only fix is the first release.

## What EMM needs

**Required — and this is the whole list:**

- **XIVLauncher** with **Dalamud** enabled.

That is deliberate. EMM never hard-depends on another plugin: needing three plugins from two
repositories before anything works is a worse product than one that does less on its own.

**Optional, and what each one buys you:**

- **[AutoRetainer](https://github.com/PunishXIV/AutoRetainer)** — lets EMM work your retainers
  while you are not watching. Without it EMM is a full analysis and pricing tool that acts when
  you open a retainer yourself; with it, the sell side runs unattended inside the window
  AutoRetainer already holds open. This build is compiled against AutoRetainer `4.6.1.27`.

EMM detects what is present when it starts and says plainly which of these it found, so you are
never guessing why something is greyed out.

On safety, one sentence, because the honest version is short: **EMM adds no risk you have not
already accepted, and it will not pretend to subtract any.**

## Releases

Every version is a [GitHub release](https://github.com/local-variable/EMM/releases) carrying its
`latest.zip`, and the repository manifest above points at the release for the version it
advertises. Release notes for a version are shown in the plugin installer when you expand its
entry.

Maintainer-facing release steps live in [`docs/releasing.md`](docs/releasing.md).

## Building from source

Requires the .NET SDK `10.0.101` or newer and a Dalamud installation for the SDK to resolve
references against.

```
git clone --recurse-submodules https://github.com/local-variable/EMM.git
```

```
dotnet build EorzeanMarketMaster/EorzeanMarketMaster.csproj -c Release -p:Platform=x64
```

`-p:Platform=x64` matters more than it looks. Without it the build still succeeds, but `Platform`
falls back to AnyCPU and the packaged output lands in `bin/Release/` instead of `bin/x64/Release/`.

`--recurse-submodules` matters: `AutoRetainerAPI` is not published to NuGet and is vendored as a
submodule pinned to the commit AutoRetainer itself ships.

## Licence

[GPL-3.0](LICENSE).
