# Releasing EMM

How a new version reaches users. Manual for now, and deliberately so — the flow is short enough
to run by hand, and running it by hand a few times is how its failure modes get found before a
workflow enshrines them.

## The two URLs, and why they differ

A user pastes **one** URL into Dalamud once, and it must keep working forever. The archive that
URL points at is a different kind of thing, and gets a different kind of URL.

| | URL | Why |
| --- | --- | --- |
| Repository manifest | `https://raw.githubusercontent.com/local-variable/EMM/main/repo.json` | Tracked on the default branch |
| Package | `https://github.com/local-variable/EMM/releases/download/<tag>/latest.zip` | Release asset, pinned to the tag |

**The manifest is served from raw on `main`, not from a release asset.** Three reasons, in the
order they mattered:

1. **A release asset URL does not exist until a release does.** `/releases/latest/download/…`
   returns 404 on a repository with no releases, so the install instructions would document a
   dead link for the whole of the pre-release period — exactly when people are first pointed at
   them. A path on the default branch is live the moment the file is committed.
2. **The manifest is small text and belongs in version control.** Its history is the record of
   what was advertised, and when. Release assets have no diff.
3. **`/releases/latest/` silently follows the newest release.** Marking a build as a pre-release,
   or deleting a bad one, would move or break the URL every user has already pasted. A branch
   path moves only when someone commits.

The cost, stated plainly: `raw.githubusercontent.com` is CDN-cached for around five minutes, so a
new version is not visible instantly; and every release needs a commit that updates `repo.json`.
Both are acceptable for a plugin that ships occasionally.

**The package is pinned to its tag rather than to `latest`.** Dalamud API 15 stopped overwriting
the manifest inside the package with the repository's copy, and now refuses an install where the
zip's `InternalName` or `AssemblyVersion` disagrees with the repository entry. A tag-pinned link
means an entry advertising a version can only ever point at the archive carrying that version.
A floating `latest` link would break every older entry the moment a new release landed.

## Steps

**1. Bump the version.** `<Version>` in
[`EorzeanMarketMaster/EorzeanMarketMaster.csproj`](../EorzeanMarketMaster/EorzeanMarketMaster.csproj)
is the only place a version is written by hand. `InternalName`, `AssemblyVersion` and
`DalamudApiLevel` are derived from the assembly by DalamudPackager and must never be authored.

**2. Build Release.**

```
dotnet build EorzeanMarketMaster/EorzeanMarketMaster.csproj -c Release -p:Platform=x64
```

This writes `EorzeanMarketMaster/bin/x64/Release/EorzeanMarketMaster/` containing `latest.zip`, a
copy of the manifest, and `images/icon.png`.

**`-p:Platform=x64` is not optional, and omitting it does not fail.** The SDK declares `x64` as an
available platform, but `Platform` still defaults to AnyCPU unless the build passes it, and the
packaged output then lands in `bin/Release/EorzeanMarketMaster/` instead. The build reports
success either way, so the only symptom is the generator in step 3 not finding the package — or,
worse, finding a stale one from an earlier correct build.

**3. Generate the repository manifest.**

```
powershell -ExecutionPolicy Bypass -File tools/build-repo-json.ps1 -Changelog "What changed."
```

`repo.json` is **generated, never hand-edited.** The script reads the packaged manifest,
cross-checks it against the copy sealed inside `latest.zip`, and refuses to write anything if the
two disagree — which is the same comparison Dalamud makes at install time, run early where it is
cheap to fix. The tag defaults to `v` + the assembly version, so the download link cannot drift
from the version it advertises.

**4. Tag and publish the release.** The tag must match the one the manifest now points at.

```
git tag v0.0.0.1
```

```
git push origin v0.0.0.1
```

```
gh release create v0.0.0.1 EorzeanMarketMaster/bin/x64/Release/EorzeanMarketMaster/latest.zip --title v0.0.0.1 --notes "What changed."
```

The asset has to be named `latest.zip`, because that is the filename the download URL ends in.

**5. Commit the manifest.** Only after the release exists, so the link is never live while broken.

`repo.json` is deliberately **not committed until the first release**, by decision on
[#15](https://github.com/local-variable/EMM/issues/15). Publishing a manifest that advertises a
version with no archive behind it turns a missing repository into a failing install, which is the
worse of the two. The first run of this step is therefore also the file's first commit.

```
git add repo.json && git commit -m "Advertise v0.0.0.1" && git push
```

**6. Verify from the outside.** Fetch what a user's Dalamud would fetch, and confirm the pair
agrees. Allow a few minutes for the raw CDN to catch up.

```
powershell -ExecutionPolicy Bypass -File tools/verify-release.ps1
```

Then install it in a clean profile from the repository URL and confirm the entry appears with its
icon, installs, and loads.

## Release notes

The `Changelog` field is what the plugin installer shows when a user expands the entry, so it is
user-facing copy: write it for a player, not a contributor. Keep the GitHub release notes and the
`Changelog` saying the same thing.

## What is not automated yet, and what it would take

A GitHub Actions workflow on tag push could run steps 2–5 unattended. It is deliberately not
built yet: the flow has never been run end to end by hand, so there is no confirmed-good sequence
to encode. Automate it once a release has actually been cut this way.

The one part worth automating early is verification, which is why step 6 is already a script.
