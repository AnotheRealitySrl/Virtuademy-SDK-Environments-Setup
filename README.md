# Virtuademy-SDK-Environments-Setup

The Virtuademy SDK installer. A creator adds **this** package by git URL, by hand, and everything
else arrives through it — so it is the one package whose own URL has to be typed.

- Package id `com.anotherealitysrl.virtuademy-sdk-environments-setup`, assembly and namespace
  `Virtuademy.SDK.Environments.Setup.Editor`, editor-only.
- Entry points: `CreatorKitSetupWindow`, menu **Virtuademy ▸ Setup project**; `SetupCli.Run` for
  batch mode (see "Command line" below).
- **User documentation** (install, every check, every button): [Documentation~/index.md](Documentation~/index.md).

This README is for whoever maintains the installer or publishes the files it reads.

## What it does

1. Downloads `PackageRegistry.json` and `BreakingChangesSolverIndex.json` (below). If the registry
   cannot be downloaded or parsed, or holds no released entry, the window hides both sections and
   shows the reason with a **Retry** button (`ShowLoadError`); the index is optional — failing to
   get it is a Console warning and an empty index.
2. Checks the project: git on the editor's `PATH`, editor version equal to the installed entry's
   `requiredUnityVersion`, Android/iOS/WebGL/Windows modules, URP + .NET Framework 4.8 (Standalone) +
   max texture size override 1024, HybridCLR installed at the entry's `interpreter` version, the
   local IL2CPP at the same version, and the hot-update assembly ready.
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
(`GetSetupIssue`, `Setup`, and `GetInterpreterVersionIssue` when the SDK has it).

**Which HybridCLR is the registry's choice.** Each entry from 2026.6 on declares it in `interpreter`
(below): the tag the release's player runs. The window never installs an unpinned HybridCLR. An
unpinned one is whatever HybridCLR released last, and v9.0.0 (2026-10-08) renamed the namespaces
the SDK compiles against.
- **Install interpreter** adds the installed entry's `interpreter` (`url#version`) and raises the
  session flag `PENDING_HYBRIDCLR_SETUP`. The setupper picks the job up on the domain reload after
  the import. The key is duplicated on purpose (`HotUpdateSetupper.PENDING_SETUP_KEY`): keep the
  two equal.
- An entry with no `interpreter`, or with a malformed one, gets no HybridCLR added: the button
  logs why. A malformed declaration needs the name `com.code-philosophy.hybridclr`, a url and a
  version; it is ignored with an error rather than written into the manifest. For a project
  that already has HybridCLR, **Fix** still runs the configuration, as before.
- **The interpreter row is not ready**, and its button reads **Fix**, when either:
  - the manifest asks for a HybridCLR other than the entry's (checked first: another HybridCLR
    may not even compile against the SDK); or
  - the interpreter in the project's local IL2CPP copy (`HybridCLRData/`) is not the package's version
    (`GetInterpreterVersionIssue`, from Virtuademy-SDK-Environments; not part of the publish gate).
- **Fix** switches the manifest to the entry's HybridCLR, raises the flag and resolves. The setup
  then reinstalls the local IL2CPP for the new version.
- **Update packages to selected version** moves HybridCLR with the version
  (`AlignInterpreterWithVersion`). HybridCLR is not one of "our" packages, so the package loops do
  not see it:

| Project | Target entry | Result |
|---|---|---|
| No HybridCLR | any | Nothing: the interpreter stays optional |
| HybridCLR | same `interpreter` | Nothing |
| HybridCLR | another `interpreter` | Manifest line moved, flag raised: the setup reinstalls after the resolve |
| HybridCLR | no `interpreter` (before 2026.6) | HybridCLR removed and listed in the removed-packages dialog |
| HybridCLR | malformed `interpreter` | Left as it is, with the error logged |

### Command line (`SetupCli`)

`SetupCli.Run` does what three of the window's buttons do, in batch mode: **Configure**
(`configure`), **Install** next to Virtuademy SDK Environments at a registry version (`sdk`), and
**Install interpreter** (`interpreter`).

```
Unity -batchmode -nographics -projectPath <project> -logFile - -ignoreCompilerErrors
      -executeMethod Virtuademy.SDK.Environments.Setup.Editor.SetupCli.Run
      -vdSetupVersion develop [-vdSetupSteps configure,sdk,interpreter | check]
      [-vdSetupRegistry <url or file>]
```

- **One launch, one round.** Every package change ends in a resolve and a domain reload, which
  would cut a running method short. A launch applies what it can, stops after the first change
  that needs a restart and exits with `2`; the caller launches again until the code is `0`. A new
  project takes three launches, measured on a scratch project on 2026-10-09:
  1. Configure and the SDK packages, written to the manifest (about 20 s);
  2. the entry's HybridCLR (`interpreter`), written to the manifest, after the SDK packages
     resolve (about 90 s);
  3. the interpreter, through `HotUpdateSetupper.Setup`, which clones hybridclr and il2cpp_plus
     (about 40 s).

  A fourth launch is needed only when the interpreter setup is not ready until a recompilation.
- **The interpreter step follows the registry.** It installs the HybridCLR that the project's
  recorded version declares. When the manifest asks for another one it switches to it, which is a
  repair rather than a version update; exit `2`. When the local IL2CPP holds another libil2cpp,
  the setup reinstalls it.
- **`-ignoreCompilerErrors`** lets Unity run the method on a project that does not compile, for
  example one that pulled HybridCLR 9.0.0, so the step can repair it. Measured: two launches, from
  24 compile errors to `0`. Without the flag, Unity aborts before `-executeMethod` (exit `1`). The
  report's `scriptsCompile` says whether the scripts compile, and `check` fails when they do not.
- **`-vdSetupRegistry`** reads the registry from another URL or from a local file (for example the
  meta-repo's `contracts/PackageRegistry.json`), to try a registry change before publishing it.
  The window always reads the published one.
- **Nothing has to survive a reload.** The steps write the manifest directly, as `InstallPackages`
  does, instead of calling `Client.Add`/`Client.Resolve`; the next launch resolves at startup. The
  interpreter retries are counted in `Library/VirtuademySetupCli.interpreter-attempts`, at most
  three, as `HotUpdateSetupper` allows itself.
- **Same logic as the window.** Configure, the install with dependencies, the configuration asset
  and the interpreter checks are internal static methods of `CreatorKitSetupWindow`, called by
  both. The files have the same format: manifest lines `url#version`, and `SetupConfiguration.asset`
  with *Show pre-releases* on when the version is a prerelease, so the window lists it.
- **Which packages.** The `sdk` step installs Virtuademy-SDK-Environments and its dependencies,
  which is the window's **Install** click. On a new project the window offers that click only for
  its default version (the newest release). For another version, such as `develop`, the window
  goes through *Update packages to selected version*, which installs **every** package of the
  entry. Today's entries list nothing outside that closure, so both paths install the same set.
  An entry that adds another package would make them differ.
- **Stricter than the window on the Unity version.** The window shows a mismatch with the entry's
  `requiredUnityVersion` in red. The `sdk` step refuses it (exit `14`), because nobody is watching a
  batch run.
- **No update.** The `sdk` step refuses a project that already has Virtuademy packages of another
  version, or a manifest asking for another ref (exit `15`). Moving between versions is the
  window's *Update packages to selected version*, which removes what the new version drops. "Already
  installed" compares the Virtuademy-SDK-Environments line only, not its dependencies.
- **A refused run writes nothing.** `SetupConfiguration.asset` is created or changed only when a
  step is about to write.
- **`check`** changes nothing. It runs after the git and modules checks, so those still exit `11`
  and `12`. Then it exits `0` when the project is set up and `3` otherwise. "Set up" means:
  - the project settings are configured;
  - Virtuademy-SDK-Environments is registered (whatever its version);
  - the scripts compile;
  - the interpreter is ready, counted as the window counts it: optional, but required once
    HybridCLR is installed. That includes the HybridCLR the recorded version declares, so with
    HybridCLR installed `check` reads the registry. The report's `hybridClrVersionChecked` is
    false when it could not.
- The method exits the editor itself, so `-quit` is not needed. Every launch ends with a
  `[SetupCli] REPORT` line (one JSON object with every check) and `[SetupCli] EXIT <code>`.
- **Exit `1` without those two lines is Unity's own failure, not SetupCli's.** It happens when the
  packages do not resolve or the scripts do not compile, and Unity aborts the batch run before
  `-executeMethod`. The cause is earlier in the log.

| Exit | Meaning |
|---|---|
| `0` | Done |
| `1` | Unexpected exception (stack trace in the log), or Unity aborted before `-executeMethod` |
| `2` | The project changed: launch again |
| `3` | `check`: not set up |
| `10` | Bad arguments, a version the registry does not list, or one before 2026.6 (no Virtuademy-SDK-Environments) |
| `11` | git cannot be run from the editor process |
| `12` | Editor modules missing (Android, iOS, WebGL, Windows) |
| `13` | Registry unreadable or empty, it no longer lists the project's recorded version, or its `interpreter` is malformed |
| `14` | The editor is not the entry's `requiredUnityVersion` |
| `15` | Another Virtuademy version is installed |
| `16` | Virtuademy-SDK-Environments missing or not resolved, or the project records no version |
| `17` | Interpreter setup failed: the entry declares no `interpreter`, HybridCLR did not resolve or compile, or three configurations did not make it ready |
| `18` | Project settings could not be applied |

`Virtuademy-Env-Test` drives it with `scripts/unity.sh setup <version>`, which passes
`-ignoreCompilerErrors` and relaunches until the code is not `2`; `SETUP_REGISTRY=<file or url>`
becomes `-vdSetupRegistry`.

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

#### `interpreter` — the HybridCLR of the release

```json
"interpreter": {
  "name": "com.code-philosophy.hybridclr",
  "displayName": "HybridCLR",
  "description": "Interprets the C# scripts of an environment. …",
  "url": "https://github.com/focus-creative-games/hybridclr_unity.git",
  "version": "v8.12.0"
}
```

- **The value.** It is the HybridCLR that the release's player runs: the tag
  `Virtuademy-Unity/Packages/manifest.json` pins at the release commit. The meta-repo's
  `docs/deploy.md` has the check.
- **Where it goes.** In the entry, not in `packages`. The interpreter is optional and installed
  only by Install interpreter. *Update packages to selected version* installs every element of
  `packages`, and the uninstall path removes hidden packages nothing depends on, so a HybridCLR
  there would end up in every project, or out of it.
- **Which entries have it.** Only entries from 2026.6 on, the first platform version with
  interpreted scripts. An entry without it offers no interpreter.
- **Older installers ignore it** (1.x, and 2.x before this field): the registry is read with
  Newtonsoft.Json, which skips fields the class does not declare.
- **Changing it on an existing entry** (in practice `develop`) moves the projects on that entry the
  next time their owner presses **Fix**, or runs `SetupCli`.

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

- **The max texture size override is not committed.** Configure sets
  `EditorUserBuildSettings.overrideMaxTextureSize`, which Unity stores in
  `Library/EditorUserBuildSettings.asset`, and `Library/` is never committed. So on every fresh
  clone the check fails again and the window opens until someone presses Configure, even though the
  rest of the configuration arrived with the clone. `SetupCli` with `-vdSetupSteps configure` is a
  one-line fix after a clone; the check itself treats a per-machine setting as a project setting.
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
