# Dalamud API 15 — scaffolding, manifest, images, third-party repo, dev loading

Research for issue #5. Investigated 2026-08-17 (local clock 2026-08-16T20:44-05:00 = 2026-08-17T01:44Z).

## Sources and pins

Every claim below is read from an official `goatcorp` source at a pinned commit, or from the
current `dalamud.dev` documentation. Where the docs and the code disagree, the code wins and the
disagreement is called out.

| Artifact | Pin | Date |
| --- | --- | --- |
| `goatcorp/Dalamud` `master` | `83042016d0` | 2026-08-14T16:25:22Z |
| `goatcorp/Dalamud.NET.Sdk` `master` | `18377d5609` | 2026-04-29T19:16:49Z |
| `goatcorp/DalamudPackager` `master` | `827d88dca9` | 2026-04-29T19:15:41Z |
| `goatcorp/SamplePlugin` `master` | `b8477daaa6` | 2026-08-15T16:34:17Z |
| `goatcorp/dalamud-docs` `main` (source of dalamud.dev) | `0bf7a92438` | 2026-07-29T18:27:59Z |
| `goatcorp/DalamudPluginsD17` `main` | `1eec8da93b` | 2026-08-16T18:55:05Z |
| `Dalamud.NET.Sdk` 15.0.0 on NuGet | published 2026-04-29 | <https://www.nuget.org/packages/Dalamud.NET.Sdk/15.0.0> |
| Local Dalamud install | `%APPDATA%\XIVLauncher\addon\Hooks\dev\Dalamud.dll` | `15.0.3.2` |

**The read source is the running build.** The installed `Dalamud.dll` reports
`ProductVersion = 15.0.3.2+83042016d0e9996dc44c9f7fd96a8d33a5e586f2`. That informational suffix is
the exact commit pinned in the table above, so the Dalamud source cited here is byte-for-byte the
Dalamud this machine loads — not a newer `master`.

**Correction to the brief.** The ticket's verified-facts block says the .NET 10 SDK is missing.
It is not, as of this session: `dotnet --list-sdks` reports `10.0.400` alongside `9.0.317`. This
already superseded in `.agent/CONTINUITY.md` [DISCOVERIES] 2026-08-17T02:15Z. Builds are unblocked.

---

## 1. API 15 in one line

API level is not a separate number — it is Dalamud's own major version.

```csharp
// Dalamud/Plugin/Internal/PluginManager.cs:83
DalamudApiLevel = typeof(PluginManager).Assembly.GetName().Version!.Major;
```

<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Plugin/Internal/PluginManager.cs#L83>

Installed Dalamud `15.0.3.2` → API level **15**. Confirmed independently by the release notes:
API Level 15, .NET 10.0.0, released 29/04/2026, `Dalamud.NET.Sdk v15.0.0`.
<https://dalamud.dev/versions/v15>

---

## 2. Project setup

### 2.1 Toolchain

| Thing | Value | Source |
| --- | --- | --- |
| Target framework moniker | `net10.0-windows` | SDK `Sdk.props` (below) |
| Language version | `14.0` | SDK `Sdk.props` |
| Platform | `x64` (both `Platforms` and `PlatformTarget`) | SDK `Sdk.props` |
| SDK attribute | `<Project Sdk="Dalamud.NET.Sdk/15.0.0">` | <https://dalamud.dev/versions/v15> |
| .NET SDK required | **10.0.101** or newer. 10.0.100 is explicitly *not* recommended | <https://dalamud.dev/versions/v14> |
| IDE | Visual Studio 2026 or Rider 2025.3 | <https://dalamud.dev/versions/v14> |

The .NET SDK / IDE requirement was introduced with Dalamud v14 (which was already on .NET 10.0.0,
released 2025-08-06) and carries forward unchanged into v15; the v15 page does not restate it.
The locally installed `10.0.400` satisfies it.

**You do not set the TFM yourself.** `Dalamud.NET.Sdk/15.0.0` sets it:

```xml
<!-- Dalamud.NET.Sdk/Sdk/Sdk.props -->
<TargetFramework>net10.0-windows</TargetFramework>
<LangVersion>14.0</LangVersion>
<Platforms>x64</Platforms>
<PlatformTarget>x64</PlatformTarget>
<Nullable>enable</Nullable>
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
<ProduceReferenceAssembly>false</ProduceReferenceAssembly>
<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
```

<https://github.com/goatcorp/Dalamud.NET.Sdk/blob/18377d5609/Dalamud.NET.Sdk/Sdk/Sdk.props#L34-L51>

Note `RestorePackagesWithLockFile` is on by default, so a `packages.lock.json` will appear next to
the `.csproj` and should be committed.

### 2.2 What the SDK references for you

The SDK resolves the Dalamud install itself and adds every Dalamud-shipped reference. Nothing needs
a `HintPath`:

```xml
<!-- Sdk.props: install resolution -->
<DalamudLibPath Condition="$([MSBuild]::IsOSPlatform('Windows'))">$(appdata)\XIVLauncher\addon\Hooks\dev\</DalamudLibPath>
<DalamudLibPath Condition="$(DALAMUD_HOME) != ''">$(DALAMUD_HOME)/</DalamudLibPath>
```

Auto-added references (all `Private="false"`, i.e. not copied to output because Dalamud provides
them at runtime): `Dalamud`, `Dalamud.Bindings.ImGui`, `Dalamud.Bindings.ImPlot`,
`Dalamud.Bindings.ImGuizmo`, `FFXIVClientStructs`, `InteropGenerator.Runtime`, `Newtonsoft.Json`,
`Lumina`, `Lumina.Excel`, `Serilog`, `Microsoft.Extensions.ObjectPool`. Each is behind a
`Use_Dalamud_*` property defaulting to `true`, so any of them can be opted out and replaced.

<https://github.com/goatcorp/Dalamud.NET.Sdk/blob/18377d5609/Dalamud.NET.Sdk/Sdk/Sdk.props#L53-L88>

Package references added for you: `DalamudPackager` (version pinned by the SDK) and
`DotNet.ReproducibleBuilds 1.2.39`. The pin lives in `SdkPackageVersions.props`:

```xml
<PackageVersion_Dalamud_NET_Sdk>15.0.0</PackageVersion_Dalamud_NET_Sdk>
<PackageVersion_DalamudPackager>15.0.0</PackageVersion_DalamudPackager>
```

<https://github.com/goatcorp/Dalamud.NET.Sdk/blob/18377d5609/Dalamud.NET.Sdk/Sdk/SdkPackageVersions.props>

The SDK also hard-fails the build if Dalamud is not installed:

```xml
<Error Text="Dalamud.NET.Sdk: Dalamud installation not found at $(DalamudLibPath)"
       Condition="!Exists('$(DalamudLibPath)')" />
```

<https://github.com/goatcorp/Dalamud.NET.Sdk/blob/18377d5609/Dalamud.NET.Sdk/Sdk/Sdk.targets#L5-L7>

### 2.3 Is the old manual `.csproj` pattern still valid? — Deprecated, and superseded.

The docs state it plainly: referencing `DalamudPackager` yourself, and the
`Dalamud.Plugin.Bootstrap.targets` import, are *"currently deprecated and set to be removed as an
option soon"*. The migration is: replace `<Project Sdk="Microsoft.NET.Sdk">` with
`<Project Sdk="Dalamud.NET.Sdk/15.0.0">`, delete every `<Reference>`+`<HintPath>` to a Dalamud
library, delete the `<DalamudLibPath>` property group, and delete the
`<Import Project="Dalamud.Plugin.Bootstrap.targets"/>` line plus the file.

<https://dalamud.dev/plugin-development/how-tos/v12-SDK-migration>

Both v14 and v15 release notes repeat the recommendation as a warning banner.
<https://dalamud.dev/versions/v15>

Everything except `<Version>` can be dropped from the project's `PropertyGroup` — anything you do
specify overrides the SDK.

### 2.4 How `DalamudPackager` produces the release zip

`DalamudPackager` is an MSBuild task that runs `AfterTargets="Build"`. The package ships two default
targets, differing only in `MakeZip`:

- `DefaultDalamudPackagerDebug` — `Configuration == Debug`, `MakeZip="false"`
- `DefaultDalamudPackagerRelease` — `Configuration == Release`, `MakeZip="true"`

Both are skipped if a `DalamudPackager.targets` file exists in the project directory, which is the
escape hatch for a custom invocation.

<https://github.com/goatcorp/DalamudPackager/blob/827d88dca9/DalamudPackager/build/DalamudPackager.props>

`Execute()` does four things, in order (`DalamudPackager.cs:134-155`):

1. Load the manifest template.
2. Verify required fields; fail the build if any are missing.
3. Read `AssemblyName`/`Version` off the just-built DLL and inject `InternalName` +
   `AssemblyVersion` (`SetProperties`, `DalamudPackager.cs:552-555`).
4. Write `<OutputPath>/<AssemblyName>.json`, and if `MakeZip`, build the release layout.

So **a Debug build already emits the manifest next to the DLL** — which is exactly what dev-loading
consumes. Only Release produces the zip.

Release layout produced by `CreateZip()` (`DalamudPackager.cs:157-246`):

```
bin/x64/Release/
 |- <AssemblyName>.dll            <- build output
 |- <AssemblyName>.json           <- generated manifest
 |- images/icon.png               <- if present; excluded from latest.zip, copied alongside
 |- <AssemblyName>/               <- the release folder
     |- latest.zip                <- everything in OutputPath except handled images
     |- <AssemblyName>.json
     |- images/
         |- icon.png, image1..5.png
```

`latest.zip` is the artifact a third-party repo's `DownloadLinkInstall` should point at. Note the
manifest is inside `latest.zip` *and* copied beside it.

<https://github.com/goatcorp/DalamudPackager/blob/827d88dca9/DalamudPackager/DalamudPackager.cs#L157-L246>

Useful task knobs: `ManifestType` (`auto` | `json` | `yaml` | `csproj`, default `auto`),
`VersionComponents` (default `4`), `Exclude` / `Include` (semicolon-separated; mutually exclusive),
`HandleImages` (default `true`), `ImagesPath` (default `images`).

### 2.5 Minimal working `.csproj` for API 15

Since SDK v14 the manifest can live entirely in the `.csproj`; no JSON file is required. This is
what the current `SamplePlugin` does, and it is the recommended shape for a new plugin.

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project Sdk="Dalamud.NET.Sdk/15.0.0">
  <PropertyGroup>
    <!-- The only field the SDK does not supply. Becomes AssemblyVersion in the manifest. -->
    <Version>0.0.0.1</Version>

    <!-- Manifest fields. AssemblyName (= file name) becomes InternalName and cannot change later. -->
    <Author>the maintainer</Author>
    <Name>Eorzean Market Master</Name>
    <Punchline>A short one-liner that shows up in /xlplugins.</Punchline>
    <Description>A longer description, shown when the entry is expanded in /xlplugins.</Description>
    <RepoUrl>https://github.com/local-variable/EMM</RepoUrl>
    <Tags>market;marketboard;retainer</Tags>
    <CategoryTags>utility</CategoryTags>
    <IconUrl>https://example.invalid/emm/icon.png</IconUrl>
  </PropertyGroup>
</Project>
```

Modelled on `SamplePlugin/SamplePlugin.csproj`:
<https://github.com/goatcorp/SamplePlugin/blob/b8477daaa6/SamplePlugin/SamplePlugin.csproj>

`SamplePlugin` no longer ships a `SamplePlugin.json` at all — the repo tree at `b8477daaa6` contains
only `.csproj`, `.cs` files and `packages.lock.json`. The `dalamud.dev` project-layout page and the
`SamplePlugin` README both still describe a JSON manifest file; that guidance is **stale relative to
the code**, though the JSON path still works (`ManifestType` `auto` tries json → yaml → csproj).

`AssemblyName` defaults to the `.csproj` file name and becomes `InternalName`, which is permanent —
it is the config directory name, the log prefix, and the DLL name.
<https://dalamud.dev/plugin-development/project-layout>

Equivalent JSON template, if a file is preferred over csproj properties (must be named
`<InternalName>.json`, next to the `.csproj`):

```json
{
  "Name": "Eorzean Market Master",
  "Author": "the maintainer",
  "Punchline": "A short one-liner that shows up in /xlplugins.",
  "Description": "A longer description, shown when the entry is expanded in /xlplugins.",
  "RepoUrl": "https://github.com/local-variable/EMM",
  "Tags": ["market", "marketboard", "retainer"],
  "CategoryTags": ["utility"],
  "IconUrl": "https://example.invalid/emm/icon.png"
}
```

Do **not** hand-write `InternalName`, `AssemblyVersion`, or `DalamudApiLevel` — DalamudPackager
fills all three. <https://dalamud.dev/plugin-development/plugin-metadata>

### 2.6 Entrypoint

Dalamud scans the DLL for exactly one class implementing `IDalamudPlugin`, injects services declared
in its constructor, and requires a working `Dispose()`.
<https://dalamud.dev/plugin-development/project-layout>

New in API 15: `IAsyncDalamudPlugin`, with `Task LoadAsync(CancellationToken)` and `IAsyncDisposable`
instead of `IDisposable`. Load and unload run off the main thread; the load task is cancelled after a
60-second timeout; main-thread work must be marshalled via `IFramework.Run()`. Marked experimental,
but the interface is stated to be stable. <https://dalamud.dev/versions/v15>

---

## 3. The manifest

### 3.1 Required vs optional

**Required** (build fails without them — `Manifest.LogMissing`,
`DalamudPackager.cs:530-550`):

- `Name`
- `Author`
- `Description`
- `Punchline`

The docs list the same four. <https://dalamud.dev/plugin-development/plugin-metadata>

**Auto-filled — do not set:** `InternalName`, `AssemblyVersion`, `DalamudApiLevel`.

### 3.2 Fields you may set

Authoritative list = the properties DalamudPackager accepts, since anything else is dropped at
package time. From `DalamudPackager.cs:75-115` and the task invocation in
`DalamudPackager.props`:

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `Name` | string | — | **required**; display name |
| `Author` | string | — | **required** |
| `Punchline` | string | — | **required**; one-liner in `/xlplugins` |
| `Description` | string | — | **required**; long description |
| `MinimumDalamudVersion` | string | null | e.g. `15.0.0` |
| `ApplicableVersion` | string | `"any"` | game version |
| `RepoUrl` | string | null | source/website |
| `Tags` | list | null | `;`-separated in csproj |
| `CategoryTags` | list | null | see valid values below |
| `LoadRequiredState` | int | `0` | 0 = during `Framework.Tick` with drawing available; 1 = during `Framework.Tick`; 2 = no requirement. Takes precedence over `LoadPriority` |
| `LoadSync` | bool | `false` | load not concurrently with other plugins/the game |
| `CanUnloadAsync` | bool | `false` | unload off the Framework thread |
| `LoadPriority` | int | `0` | higher loads earlier |
| `ImageUrls` | list | null | max 5 |
| `IconUrl` | string | null | see §4 |
| `Changelog` | string | null | shown to existing users |
| `AcceptsFeedback` | bool | `true` | |
| `FeedbackMessage` | string | null | shown when sending feedback |
| `DalamudApiLevel` | int | `15` | auto-filled; the packager's own default is now 15 |

<https://github.com/goatcorp/DalamudPackager/blob/827d88dca9/DalamudPackager/DalamudPackager.cs#L419-L556>

The v14 release notes carry the complete csproj-property list verbatim, which is the easiest
copy-paste reference. <https://dalamud.dev/versions/v14>

Valid `CategoryTags` (tag-driven installer categories, `PluginCategoryManager.cs:40-47`):
`other`, `jobs`, `ui`, `minigames`, `inventory`, `sound`, `social`, `utility`.
<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Interface/Internal/PluginCategoryManager.cs#L40-L47>

### 3.3 Fields Dalamud reads that you must NOT author

`Dalamud.Plugin.Internal.Types.PluginManifest` is the full deserialisation target. Beyond the table
above it carries fields set by the distribution plumbing or by a repository, not by a plugin author:

- `InternalName`, `AssemblyVersion` — packager
- `_Dip17Channel` (C# `Dip17Channel`) — set by the mainline D17 pipeline; also written into the
  local manifest at install time. Serialised under the literal JSON name `_Dip17Channel`
- `IsHide`, `DownloadCount`, `LastUpdate` — repository-side
- `DownloadLinkInstall`, `DownloadLinkUpdate`, `DownloadLinkTesting` — repository-side
- `TestingAssemblyVersion`, `TestingDalamudApiLevel`, `IsTestingExclusive` — repository-side
- `SupportsProfiles` — defaults `true`
- `IsAvailableForTesting` — computed, `[JsonIgnore]`

<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Plugin/Internal/Types/PluginManifest.cs>

This explains the AutoRetainer 4.6.1.27 sample in the ticket: `SupportsProfiles`,
`IsTestingExclusive` and `_Dip17Channel` appear in the *installed* manifest because Dalamud writes
them on install, not because the author wrote them.

The docs say the same in prose: *"some fields mentioned there, like `Dip17Channel`, are set
automatically by the various plumbing … you should not include them in your manifest explicitly."*
<https://dalamud.dev/plugin-development/plugin-metadata>

### 3.4 API-15 change: the distributed manifest must now be accurate

Until v15, Dalamud overwrote the `InternalName.json` inside the plugin zip with the repository's
manifest. It no longer does. The zip must contain a manifest, and it must match.
<https://dalamud.dev/versions/v15>

The enforcement is hard — install throws on mismatch:

```csharp
var tempManifest = LocalPluginManifest.Load(tempManifestFile) ?? throw new Exception("Plugin had no valid manifest");
if (tempManifest.InternalName != repoManifest.InternalName)
    throw new Exception($"Distributed internal name does not match repo internal name, ...");
if (tempManifest.AssemblyVersion != version)
    throw new Exception($"Distributed plugin version does not match repo version, ...");
```

<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Plugin/Internal/PluginManager.cs>
(`InstallPluginInternalAsync`)

**Consequence for a third-party repo:** the `InternalName` and `AssemblyVersion` in the repo JSON
must be kept in lockstep with the manifest inside `latest.zip`. Generating the repo JSON from the
packager's emitted `<InternalName>.json` is the safe pattern; hand-editing it invites a hard install
failure.

Manifest filename convention, used for both install and dev load: `<dll basename>.json` beside the
DLL. <https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Plugin/Internal/Types/Manifest/LocalPluginManifest.cs#L78-L85>

---

## 4. Icon and image requirements

### 4.1 Exact numbers

Dalamud enforces these at load time (`PluginImageCache.cs:27-46`):

```csharp
public const int PluginImageWidth = 730;
public const int PluginImageHeight = 380;
public const int PluginIconWidth = 512;
public const int PluginIconHeight = 512;
```

<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Interface/Internal/Windows/PluginImageCache.cs#L27-L48>

| | Icon | Store images |
| --- | --- | --- |
| Max resolution | **512 × 512** | **730 × 380** |
| Must be square | **yes** | no |
| Count | 1 | up to 5 (`image1.png` … `image5.png`) |
| Format | PNG (`icon.png` / `imageN.png` by filename; decoding is format-agnostic for URL-served images) | PNG |
| Min resolution | **64 × 64** — D17 mainline rule only, not enforced by Dalamud | not specified |
| File-size limit | **UNCONFIRMED** — no byte limit found in Dalamud, DalamudPackager, or the D17 README | UNCONFIRMED |

Enforcement is a maximum plus a squareness test, not an exact match. Oversize or non-square images
are rejected and logged, then simply not displayed:

```csharp
if (image.Width > maxWidth || image.Height > maxHeight) { /* log error, dispose, return null */ }
if (requireSquare && image.Height != image.Width) { /* log error, dispose, return null */ }
```

`requireSquare` is `true` for icons and `false` for store images.
<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Interface/Internal/Windows/PluginImageCache.cs#L271-L314>

More than 5 `ImageUrls` are truncated to 5 with a warning, not an error.

The mainline-submission rule adds a lower bound: *"Your plugin **must have** an `icon.png` that is
no larger than 512x512 and no smaller than 64x64, located in `images/`."*
<https://github.com/goatcorp/DalamudPluginsD17/blob/1eec8da93b/README.md> — restated on
<https://dalamud.dev/plugin-publishing/submission> as 1:1 aspect ratio, between 64x64 and 512x512.

Practical recommendation: ship exactly **512 × 512** PNG, square, and **730 × 380** PNGs for store
images. Those are the maxima and are what the installer lays out for.

### 4.2 How `IconUrl` resolves — three distinct paths

`PluginImageCache.DownloadPluginIconAsync` / `GetPluginIconUrl`
(<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Interface/Internal/Windows/PluginImageCache.cs#L445-L666>):

1. **Dev plugin** (`plugin.IsDev`) — tries the local filesystem *first*:
   `<directory of the plugin DLL>/images/icon.png`, and `images/image1..5.png` for store images.
   If the local file loads, no network request happens. If it does not exist or fails validation,
   the plugin is then treated as third-party and the URL path below is used.
2. **Third-party repo** — `return manifest.IconUrl;`. Plain HTTP(S) `GET` via the shared
   `HttpClient`. A `404` yields no icon; any other non-success throws. So yes: for a third-party
   repo the icon is **served over HTTP from wherever `IconUrl` points, not read from the package.**
3. **Mainline (D17)** — `IconUrl` is *ignored*. The URL is composed from the D17 channel:
   `https://raw.githubusercontent.com/goatcorp/PluginDistD17/main/{Dip17Channel}/{InternalName}/images/{icon.png|imageN.png}`.
   If `Dip17Channel` is empty, no icon.

**For EMM (third-party distribution), that means:** the icon needs stable public HTTP hosting and
`IconUrl` must be an absolute URL. Packaging `images/icon.png` in the zip is still worth doing —
it costs nothing, it is what a dev-loaded build reads, and it is the layout D17 expects should the
plugin ever go mainline — but it is *not* what installed third-party users see. A raw
`raw.githubusercontent.com` URL pinned to a tag is the low-friction option and mirrors what the
mainline pipeline itself does.

This confirms and sharpens the CONTINUITY note "IconUrl is an HTTP URL, not a packaged file": true
for installed third-party plugins, false for dev-loaded ones, and irrelevant for mainline.

---

## 5. Third-party repository JSON

### 5.1 Shape

A repository is a single URL returning a **JSON array** of store entries, reachable by unauthenticated
HTTP `GET`. Query parameters are allowed; authentication is not supported.
<https://dalamud.dev/plugin-publishing/custom-repositories>

Dalamud requests it with `Accept: application/json` and `Cache-Control: no-cache`, with a request
timeout, and deserialises straight into `List<RemotePluginManifest>`:

```csharp
var pluginMaster = JsonConvert.DeserializeObject<List<RemotePluginManifest>>(data)
    ?? throw new Exception("Deserialized PluginMaster was null.");
```

<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Plugin/Internal/Types/PluginRepository.cs#L108-L223>

Because the target type is `RemotePluginManifest : PluginManifest`, **every** manifest field from
§3 is a legal repository key, plus the repository-only ones. Unknown keys are ignored.

### 5.2 Minimal valid entry

```json
[
  {
    "Name": "Eorzean Market Master",
    "InternalName": "EorzeanMarketMaster",
    "Author": "the maintainer",
    "Punchline": "One-liner shown in the installer list.",
    "Description": "Longer description shown when the entry is expanded.",
    "AssemblyVersion": "0.1.0.0",
    "DalamudApiLevel": 15,
    "ApplicableVersion": "any",
    "RepoUrl": "https://github.com/local-variable/EMM",
    "IconUrl": "https://example.invalid/emm/icon.png",
    "Tags": ["market", "marketboard", "retainer"],
    "CategoryTags": ["utility"],
    "DownloadLinkInstall": "https://example.invalid/emm/latest.zip",
    "DownloadLinkUpdate": "https://example.invalid/emm/latest.zip",
    "DownloadLinkTesting": "https://example.invalid/emm/testing.zip",
    "LastUpdate": 1755388800,
    "IsHide": false,
    "IsTestingExclusive": false,
    "TestingAssemblyVersion": null,
    "TestingDalamudApiLevel": null
  }
]
```

Cross-checked against a live third-party repository — `https://love.puni.sh/ment.json`
(301 → `https://puni.sh/api/plugins`), 15 entries, first entry keys: `Author`, `Name`, `Punchline`,
`Description`, `Tags`, `InternalName`, `RepoUrl`, `DownloadCount`, `LastUpdate`,
`DownloadLinkInstall`, `DownloadLinkUpdate`, `AssemblyVersion`, `ApplicableVersion`,
`DalamudApiLevel`, `Changelog`, `IconUrl`. Fetched 2026-08-17. Notably it ships neither
`DownloadLinkTesting` nor any testing keys, and serialises `DalamudApiLevel` as a JSON *string* —
Newtonsoft coerces it, so both forms work in practice, but an integer is the correct type.

### 5.3 Repository-only keys

<https://dalamud.dev/plugin-publishing/custom-repositories>, cross-checked against
`PluginManifest.cs`:

- `DownloadLinkInstall` — URL of the artifact zip. **Required in practice.**
- `DownloadLinkUpdate` — documented as an update-specific URL, e.g. for download-count tracking.
- `DownloadLinkTesting` — URL of the testing artifact zip.
- `IsHide` — hide from clients without removing the entry. Forced to `false` for the official repo
  only; honoured for third-party.
- `DownloadCount`, `LastUpdate` — display metadata. `LastUpdate` is a Unix timestamp (seconds).
- `ImageUrls`, `IconUrl` — see §4.

**Which link is actually fetched.** The single download path is:

```csharp
var downloadUrl = useTesting ? repoManifest.DownloadLinkTesting : repoManifest.DownloadLinkInstall;
```

<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Plugin/Internal/PluginManager.cs> —
`DownloadPluginAsync`. Updates call the same method with the update manifest, so they too read
`DownloadLinkInstall`. No consumer of `DownloadLinkUpdate` was found anywhere in
`PluginManager.cs`; whether any other code path reads it is **UNCONFIRMED** (GitHub code search
returned zero results for all three keys, so it could not be used as a cross-check). Set
`DownloadLinkUpdate` to the same URL as `DownloadLinkInstall` — that is what the docs sample and the
live Puni.sh repo both do.

### 5.4 Testing channel

Author-side keys (all optional; only needed if you publish a beta):

- `IsTestingExclusive` — entry visible only to users opted into testing.
- `TestingAssemblyVersion` — used **only if greater than** `AssemblyVersion`.
- `TestingDalamudApiLevel` — API level of the testing build. Required whenever
  `TestingAssemblyVersion > AssemblyVersion`; Dalamud logs a warning if it is missing.
- `TestingChangelog` — changelog shown for the test version (lives on `RemotePluginManifest`).
- `DownloadLinkTesting` — the testing zip.

Eligibility is computed, not declared:

```csharp
public bool IsAvailableForTesting
    => this.TestingAssemblyVersion != null &&
       this.TestingAssemblyVersion > this.AssemblyVersion &&
       this.TestingDalamudApiLevel == PluginManager.DalamudApiLevel;
```

<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Plugin/Internal/Types/PluginManifest.cs#L178-L181>

User side: Settings → Experimental → **"Get plugin testing builds"** (`DoPluginTest`), then
right-click the plugin in the installer's *Installed Plugins* tab and choose **"Receive plugin
testing versions"** (a per-plugin opt-in list, `PluginTestingOptIns`).
<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Interface/Internal/Windows/Settings/Tabs/SettingsTabExperimental.cs#L38-L53>

### 5.5 Validation and gotchas

`PluginRepository.IsValidManifest` drops any entry with a blank `InternalName`, a blank `Name`, or a
null `AssemblyVersion`. A repository-wide deserialisation failure marks the whole repo failed.

**A third-party repo may not shadow a mainline plugin.** If an `InternalName` (case-insensitive)
already exists in the official repository, the entry is filtered out with a logged warning —
*"this is no longer allowed for security reasons"*. If the official repo has not loaded, the
third-party repo is failed outright rather than trusted.
<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Plugin/Internal/Types/PluginRepository.cs#L135-L160>

Pick `InternalName` accordingly — a collision with any mainline plugin makes EMM uninstallable.

Users add a repo at Settings → Experimental → **"Custom Plugin Repositories"**, behind a timed
"read this warning" speedbump.
<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Interface/Internal/Windows/Settings/Widgets/ThirdRepoSettingsEntry.cs>

### 5.6 If EMM ever goes mainline instead

D17 submission is a PR against `goatcorp/DalamudPluginsD17` adding
`testing/live/<PluginName>/manifest.toml` (new plugins **must** start in testing) with an
`images/icon.png` beside it:

```toml
[plugin]
repository = "https://github.com/local-variable/EMM.git"
commit = "<full commit sha>"
owners = ["<github-username>"]
maintainers = ["<github-username>"]
project_path = "<csproj directory>"
changelog = "..."
```

Also required there: no timestamp- or build-counter-based versions (the same commit must always
produce the same version), and the Dalamud Windowing API for anything window-shaped.
<https://dalamud.dev/plugin-publishing/submission> and
<https://github.com/goatcorp/DalamudPluginsD17/blob/1eec8da93b/README.md>

---

## 6. Dev loading at API 15

### 6.1 `devPlugins` is dead

Support for loading from `%APPDATA%\XIVLauncher\devPlugins` was removed in May 2023: *"we are
removing support in Dalamud for loading plugins from the legacy `devPlugins` directory … To load
plugins for development, you'll need to add a dev plugin path in your Dalamud settings."*
<https://dalamud.dev/news/2023/05/26/removing-legacy-devplugins> (published 2023-05-26)

Matches the local machine: that folder contains only `DONT_USE_THIS_FOLDER.txt`.

### 6.2 The current mechanism — Dev Plugin Locations

A **Dev Plugin Location is the full path to the plugin DLL itself, not a folder.** The settings UI
validates it:

```csharp
this.Name = LazyLoc.Localize("DalamudSettingsDevPluginLocation", "Dev Plugin Locations");
// hint: "Add dev plugin load locations.\nThis must be a path to the plugin DLL."
private static bool IsValidPluginPath(string path)
    => Path.IsPathRooted(path) && Path.GetExtension(path) == ".dll";
```

<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Interface/Internal/Windows/Settings/Widgets/DevPluginsSettingsEntry.cs>

**The setting is hidden until Developer Mode is on.** This is not in the SamplePlugin README and is
the most likely stumbling block:

```csharp
this.devModeEntry = new SettingsEntry<bool>(
    LazyLoc.Localize("DalamudSettingEnableDeveloperMode", "Enable Developer Mode"), ...);
...
new DevPluginsSettingsEntry(visibility: () => this.devModeEntry.Value),
```

<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Interface/Internal/Windows/Settings/Tabs/SettingsTabExperimental.cs#L30-L72>

Likewise the installer's *Dev Tools* group only appears when dev plugins exist
(`canShowGroup = groupInfo.GroupKind != GroupKind.DevTools || this.hasDevPlugins`).

### 6.3 Exact click path

1. Build the plugin (Debug is fine). DalamudPackager writes `<InternalName>.json` next to
   `<InternalName>.dll` in `bin/x64/Debug/` on every build — both files must sit together.
2. In game, run **`/xlsettings`** in chat (or `xlsettings` in the Dalamud console).
3. Open the **Experimental** tab.
4. Tick **Enable Developer Mode**. *("Unlocks developer-specific settings." Without this, step 5 is
   not rendered.)*
5. Find **Dev Plugin Locations**. Either paste the absolute path to `<InternalName>.dll` into the
   text field and press the add button, or use **Select Dev Plugin DLL** to browse. An optional
   nickname can be set, shown next to the plugin name in the list.
   - The path must be rooted and end in `.dll`; a folder path is rejected with *"The entered value
     is not a valid path to a potential Dev Plugin."*
6. **Save** the settings window.
7. Run **`/xlplugins`** (or `xlplugins`).
8. Go to **Dev Tools → Installed Dev Plugins**. The plugin appears under its `InternalName`. Enable
   it.

Group and category labels verified against
<https://github.com/goatcorp/Dalamud/blob/83042016d0/Dalamud/Interface/Internal/PluginCategoryManager.cs#L54-L57>
(`"Dev Tools"`) and `#L575` (`"Installed Dev Plugins"`). The same flow, minus the Developer Mode
step, is documented in the SamplePlugin README:
<https://github.com/goatcorp/SamplePlugin/blob/b8477daaa6/README.md>

Step 5 is one-time and persists. Afterwards, rebuild and use the installer's reload control; the
location does not need re-adding.

Also worth enabling while developing, same tab: **Enable ImGui asserts** (and its at-startup
counterpart) — the docs explicitly recommend it for plugin development. The non-startup toggle does
not persist across game restarts.

### 6.4 Dev-plugin icon

Drop a 512×512 square `icon.png` into `<output dir>/images/icon.png` — beside the DLL — and it shows
up in the installer without any hosting. See §4.2 path 1.

---

## 7. Open items

- **Icon/image byte-size limit — UNCONFIRMED.** Only resolution and squareness are enforced by
  Dalamud, and only resolution bounds appear in the D17 rules. No byte cap found in Dalamud,
  DalamudPackager, or the D17 README. Not searched: the Plogon CI implementation.
- **`DownloadLinkUpdate` consumer — UNCONFIRMED.** Defined in the schema and documented, but no
  reader found in `PluginManager.cs`. Mirror `DownloadLinkInstall` and move on.
- **Doc drift.** `dalamud.dev/plugin-development/project-layout` and the SamplePlugin README both
  still describe a required JSON manifest file, and the README still says ".NET Core 8 SDK" and
  `SamplePlugin.sln` (the repo now ships `SamplePlugin.slnx`). Trust the code and the v14/v15
  release notes over those two pages.
- **`dalamud.dev` sitemap does not list the `/plugin-development` pages**, though they return 200.
  Search-engine discovery of these pages may be unreliable; go via the docs repo
  (`goatcorp/dalamud-docs`, `docs/**`) when a page seems missing.
