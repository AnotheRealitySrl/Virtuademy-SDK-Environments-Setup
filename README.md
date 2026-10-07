# Virtuademy-SDK-Environments-Setup

The Virtuademy SDK installer. A creator adds **this** package by git URL, by hand, and everything
else arrives through it — so it is the one package whose own URL has to be typed.

- Package id `com.anotherealitysrl.virtuademy-sdk-environments-setup`, assembly and namespace
  `Virtuademy.SDK.Environments.Setup.Editor`, editor-only.
- Entry point: `CreatorKitSetupWindow`, menu **Virtuademy ▸ Setup ▸ Setup project**.
- **User documentation** (install, every check, every button): [Documentation~/index.md](Documentation~/index.md).

This README is for whoever maintains the installer or publishes the files it reads.

## What it does

1. Downloads `PackageRegistry.json` and `BreakingChangesSolverIndex.json` (below). If the registry
   cannot be downloaded or parsed, or holds no released entry, the window hides both sections and
   shows the reason with a **Retry** button (`ShowLoadError`); the index is optional — failing to
   get it is a Console warning and an empty index.
2. Checks the project: git on the editor's `PATH`, editor version equal to the installed entry's
   `requiredUnityVersion`, Android/iOS/WebGL/Windows modules, URP + .NET Framework 4.8 (Standalone) +
   max texture size override 1024, HybridCLR installed and the hot-update assembly ready.
3. Lists the packages of the selected registry entry and installs, uninstalls or moves them by
   writing `"{url}#{version}"` lines into `Packages/manifest.json`, then calling `Client.Resolve`.

The window opens on its own at the first domain reload of an editor session — the project
opening, or this package arriving — through `SetupWindowStartup` (`[InitializeOnLoad]`, guarded by
a `SessionState` flag, skipped in batch mode) — **only if the project needs it**:
`CreatorKitSetupWindow.FindStartupIssue` returns a reason when `virtuademy-sdk-environments` is not
registered, git cannot be run, an editor module is missing, or the URP / API compatibility / max
texture size settings are off. It reuses the window's own checks. Deliberately left out: the Unity
version (it needs the registry, hence the network) and the interpreter (HybridCLR). The reason is
logged when the window opens. The toggle at the foot of the window turns the startup check off; the
choice is an `EditorPrefs` key per `PlayerSettings.productGUID` (default on), so it is per project
and per machine and nothing is committed. The header logo is `Icons/virtuademy-logo-{light,dark}.png`, picked by
`EditorGUIUtility.isProSkin`, rasterized from the Landing's `themes/virtuademy/full-*.svg`; the
`.meta` files are committed because a git package is immutable and Unity cannot generate them.

State lives in a `PackageManagerConfiguration` asset, created on first open at
`Assets/Virtuademy/Editor/Settings/SetupConfiguration.asset` and found afterwards by type
(`FindAssets("t:PackageManagerConfiguration")`), never by path.

"Our" packages are recognised by prefix — `com.anotherealitysrl.virtuademy`, `…reflectis`,
`…spacs` (`package_prefixes` in the window). A new prefix must be added there, or the window
neither sees those packages as installed nor unpins them on re-resolve.

### Interpreter setup

The hot-update configuration is owned by `HotUpdateSetupper` in `Virtuademy-SDK-Environments`, which
only compiles once HybridCLR is installed, so the window reaches it **by reflection**
(`GetSetupIssue`, `Setup`). When HybridCLR is missing, the window installs it from
`https://github.com/focus-creative-games/hybridclr_unity.git` and raises the session flag
`PENDING_HYBRIDCLR_SETUP`; the setupper picks the job up on the domain reload after the import. The
key is duplicated on purpose (`HotUpdateSetupper.PENDING_SETUP_KEY`) — keep the two equal.

## What it reads

Both files sit in the public blob container `spacspublic`, folder `sdkpackagesregistry`
(`https://spacsglobal.dfs.core.windows.net/spacspublic/sdkpackagesregistry/`), on the `spacsglobal` storage account. The URLs are hardcoded
in `CreatorKitSetupWindow`. `spacspublic` is the public container for generic, publicly served
tooling; the registry has its own folder in it. The container is named for neither a brand nor a
tenant, so neither a rename nor a tenant decommission moves it.

Installers up to 1.x read the same files from `reflectis2023-public/PackageManager/`. That folder
receives registry updates **up to and including 2026.6** — the version whose package set carries the
`v2026.5 -> v2026.6` update routine, which moves a project to this installer — and is frozen after
it. From 2026.7 on, only `spacspublic/sdkpackagesregistry/` is updated.

**The filenames are load-bearing**: the installer asks for exactly those names, so
`PackageRegistry (1).json` is the same as not publishing.

The versioned source of `PackageRegistry.json` is
[`contracts/PackageRegistry.json`](https://github.com/AnotheRealitySrl/Virtuademy-Platform/blob/develop/contracts/PackageRegistry.json)
in the meta-repo; the blob is a publish target, uploaded by hand, with no history. Treat every
upload as a production change: reconcile against the blob first, review the diff, upload. Full
procedure in the meta-repo's `docs/deploy.md`.

### `PackageRegistry.json`

```jsonc
[
  {
    "reflectisVersion": "2026.5.0",          // platform version, or a free label for a test entry
    "requiredUnityVersion": "6000.3.21f1",   // must equal the editor's version string exactly
    "prerelease": false,                     // true = hidden unless "Show pre-releases" is on
    "packages": [
      {
        "name": "com.anotherealitysrl.virtuademy-sdk-environments",
        "displayName": "Virtuademy SDK Environments",
        "description": "…",
        "visibility": "Visible",             // Visible = offered in the UI, Hidden = pulled as a dependency
        "url": "https://github.com/AnotheRealitySrl/Virtuademy-SDK-Environments.git",
        "version": "v10.0.0"                 // ANY git ref — see below
      },
      {
        "name": "com.anotherealitysrl.virtuademy-sdk-core",
        "visibility": "Hidden",
        "url": "https://github.com/AnotheRealitySrl/Virtuademy-SDK-Core.git",
        "version": "v17.0.0"
      },
      {
        "name": "com.anotherealitysrl.spacs-utility",
        "visibility": "Hidden",
        "url": "https://github.com/AnotheRealitySrl/SPACS-Utility.git",
        "version": "v1.0.0"
      }
    ],
    "dependencies": {
      "com.anotherealitysrl.virtuademy-sdk-environments": [ "com.anotherealitysrl.virtuademy-sdk-core" ],
      "com.anotherealitysrl.virtuademy-sdk-core":         [ "com.anotherealitysrl.spacs-utility" ]
    }
  }
]
```

(Version numbers above are illustrative; the real entries are in `contracts/PackageRegistry.json`.)

#### Entry order is load-bearing

When the project has no installed version yet, the window records the **last** entry of the visible
list (`AvailableVersions[^1]`), not the highest version number, and selects the same entry for
display when the installed one is not visible. Append new releases at the end, and keep prerelease
entries anywhere: they are filtered out before the pick unless the toggle is on.

#### Removing an entry strands no project

A project keeps the version it recorded even when the registry stops listing it (a test entry
deleted after its branch merged, typically). The window shows it as *no longer available*, logs a
warning, and enables **Update packages to selected version**, which works from what is installed and
what the target lists — it does not need the old entry. Until 2026-10 the window instead overwrote
the recorded version with the newest entry: it then claimed a version the project did not have and
kept the update button disabled, which is how `Virtuademy-Env-Test` got stuck on the removed
`spacs-utility-split` set.

#### `version` is a git ref, not only a tag

The installer writes `"{url}#{version}"` into `Packages/manifest.json`, and the `#` fragment of a
git URL is any ref git understands — **a tag, a branch, or a commit SHA**. So an entry can point a
tester at work in flight:

```jsonc
{ "version": "feature/creatorkit-restructure" }   // a branch
{ "version": "9564ed8" }                          // a commit
{ "version": "v8.1.0" }                           // a tag, the normal case for a release
```

Release entries should always pin **tags** — a branch moves, and a creator on a released version
must get the same bytes tomorrow. A branch entry also stays at the commit it first resolved to
until the creator presses *Re-resolve packages from git*, because `packages-lock.json` pins it.

#### `prerelease` keeps a test entry away from creators

An entry marked `"prerelease": true` is left out of the version list unless the window's *Show
pre-releases* toggle is on. That is how a branch-pointing entry coexists with the release entries
without appearing to creators.

For backward compatibility an entry named exactly `develop` is treated as a prerelease even without
the flag — that behaviour used to be hardcoded in the window.

#### `dependencies` declares direct edges only

Resolution is recursive and depth-first, so a package does not repeat its grandchildren: if
`virtuademy-sdk-environments` needs `virtuademy-sdk-core` and `virtuademy-sdk-core` needs
`spacs-utility`, declaring the first two edges is enough. Fewer entries in a hand-edited file means
less to get wrong.

A package with no dependencies needs no key. A cycle is expanded once and does not recurse. A
dependency naming a package that is not in the entry's `packages` is logged as an error and skipped
— the install goes ahead without it, so watch the Console after editing the file.

#### `installationSource` — leave it out

`PackageDefinition` also has an `installationSource` (`Git`, the default, or `Submodule`). No
registry entry sets it, and it does not change how a package is installed: the window always writes
a git URL. It is the window that marks a package `Submodule` when UPM reports it from any source
other than git (an embedded or local package), which turns its button into a disabled **Embedded**.

### `BreakingChangesSolverIndex.json`

Maps a pair of **minor** versions (patch stripped) to the URL of a C# script:

```json
{
  "(\"2025.3\", \"2025.4\")": "https://spacsglobal.dfs.core.windows.net/spacspublic/sdkpackagesregistry/BreakingChangesSolver2025_4.cs"
}
```

When *Resolve breaking changes automatically* is on, an update from version A to version B looks up
`(minor(A), minor(B))`, downloads the script into `Assets/Virtuademy/Editor/Scripts/` and imports
it. **Nothing runs it**: the published script only declares a `[MenuItem]` (for `2025.4`,
*Reflectis ▸ Creator Kit update routines ▸ v2025.3 -> v2025.4*) that the creator has to click.
`CreatorKitSetupWindow.ResolveBreakingChangesCallback`, which would invoke a solver's
`SolveBreakingChanges` by type name, has no caller. Only the `2025.3 → 2025.4` step is published
today, and its script uses the pre-rename `Reflectis.*` namespaces.
Later version steps ship as menu entries under **Virtuademy ▸ Update routines** in
`Virtuademy-SDK-Environments` instead (e.g. `v2026.5 -> v2026.6`), not through this index.

A step with no entry — every step after `2025.4` — logs a warning and downloads nothing. The index
is downloaded on every open; if that fails the window still loads, and automatic resolution has
nothing to look up until the next refresh.

## Developing the installer

The installer is not mounted in `Virtuademy-Unity` — there the packages are embedded submodules and
the window has nothing to do. It is exercised in `Virtuademy-Env-Test`, an empty Unity project that
mounts this repository under `Packages/` and runs the install and publish flow from outside.

To test a registry change without touching the blob, point an entry at a branch with
`"prerelease": true`, publish, and select it with *Show pre-releases* on.

## Known issues

- **Every clone starts dirty.** Some committed `.asset` files under `Editor/Setup/ProjectSettings/`
  are stored with line endings that git normalizes on checkout, so `git status` reports changes
  nobody made. See the meta-repo's `docs/line-endings.md` for the platform policy; this repo has
  not had that pass.
- **Renamed on 2026-09-23, and the old id does not resolve the new package.** This package was
  `com.anotherealitysrl.reflectis-creatorkit-worlds-setup` (namespace
  `Reflectis.CreatorKit.Worlds.Setup.Editor`); it is now
  `com.anotherealitysrl.virtuademy-sdk-environments-setup` (`Virtuademy.SDK.Environments.Setup.Editor`).
  A project that re-resolves this package under the old manifest key is expected to stop resolving,
  because the key no longer matches the name in `package.json` (not yet observed on a real
  project). Such a project must run `Virtuademy ▸ Update routines ▸ v2026.5 -> v2026.6` (shipped in
  `Virtuademy-SDK-Environments`) first: it rewrites the key and the URL and unpins this project's
  own git packages in `packages-lock.json`, so UPM resolves them again. The old id stays in the
  self-exclusion list of `CreatorKitSetupWindow` for projects that have not migrated yet.
- **2.0.0 is on `develop`, not released.** `main` — the branch a creator gets from the bare git
  URL — is still the 1.x installer under the old id, and the latest tag is `v1.0.1`. The release
  waits for **the 2026.6 platform version in the registry**: moving `main` to the new id leaves a
  1.x project that re-resolves the installer unresolvable until it runs
  `Virtuademy ▸ Update routines ▸ v2026.5 -> v2026.6`, which arrives with the 2026.6 package set.
  Then merge `develop` into `main`, tag `v2.0.0` on `main`.
