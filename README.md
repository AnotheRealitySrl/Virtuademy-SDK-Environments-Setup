# Virtuademy-SDK-Environments-Setup

The Virtuademy SDK installer. A creator adds **this** package by git URL, by hand, and everything
else arrives through it — so it is the one package whose own URL has to be typed.

- Package id `com.anotherealitysrl.virtuademy-sdk-environments-setup`, assembly and namespace
  `Virtuademy.SDK.Environments.Setup.Editor`, editor-only.
- Entry points: `CreatorKitSetupWindow`, menu **Virtuademy ▸ Setup project**; `ReadmeWindow`, menu
  **Virtuademy ▸ Readme**; `SetupCli.Run` for batch mode (see "Command line" below); `SetupRun`, the
  engine that runs every step inside the editor (see "The setup run" below).
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

The window is a wizard of six pages over the page above, in `CreatorKitSetupWindow.Wizard*.cs` (`partial`s of the window; the
styles are `wizard.uss`, loaded from code): **Introduction**, **Connection**, **Choices**, **Summary**, **Run** and **Project status**, one at a
time, with a bar under the page (`RefreshNav`: the one place that knows the flow). The pages only start work and show it: the work
is `SetupRun`'s, the connections are the SDK's, and the checks are the window's own (`projectConfig`, `packageManagerConfig`,
the same the long page reads). **Development mode (internal testing)** at the foot is `packageManagerConfig.ShowPrereleases` (the long
page's *Show pre-releases*), saved in the setup configuration asset: it lists the development version and adds the details of the
Summary. The long page as it was (`showAllChecks`) is opened from its own menu entry, **Virtuademy ▸ Advanced ▸ All setup checks**
(`ShowAllChecksWindow`), refused while a run is going; **Back to the guided setup** at its top and **Virtuademy ▸ Setup project**
return to the wizard. The page, that
toggle and each page's choices are `[SerializeField]`s, so they survive the domain reload every package action ends in; the first
load of an editor session picks the page by itself (a `SessionState` flag): the Introduction for a project that has no
`virtuademy-sdk-environments` registered (the Connection page when the Introduction is turned off,
`SetupUserSettings.HideIntroduction`, key `hideIntroduction`), the Run page while a run is going, the status for a project that is
set up. A 500 ms poll keeps the page shown honest (a login finished in the browser, a compile ended).

- **Introduction** (`Wizard.cs`): optional; three rows, a back-up warning, "Don't show this introduction again".
- **Connection** (`WizardConnection.cs`): the Back Office the file is downloaded from (`bo.virtuademy.io`, or another one saved as
  `backOfficeUrl` in `setup.json`), one row per connection of the computer (tenant, environment, platform version, account or "not
  logged in", a warning when the app's `platform` is not `CreatorKit`) with Log in / Log out, and "Add an organization" (choose the
  file, import it, log in to the first connection it brought). It is optional at every point: **Connect later** is always there.
  The connections are the SDK's, kept in one store per user, and this package cannot reference the SDK, so `SetupConnections` calls
  `Virtuademy.SDK.TenantConfiguration.Editor.SetupBridge` (SDK-Core, 2026.7.0 on) by name, through reflection, with strings in and out
  (`SetupBridge.Version` must be 1; another number is "not available" until this code is updated). Without it the rows come from
  `%USERPROFILE%\.virtuademy\sdk\status.json`, which has no secret, and the page says what is not available. When the project has
  no SDK, **Choose the file...** and **Log in** first ask to install it (a dialog, "Install and continue"), then write just SDK-Core of
  the newest registry entry that has it for this editor (2026.7.0 or later; the development version in development mode, which the
  page points to while no release has it) and what it needs into the manifest, and keep the action in `setup.json`
  (`pendingConnection`: `choose-file`, or `login` with the connection's key). The window goes on with it after the domain reload,
  once no setup run is going: it opens the file picker (so the file is chosen once, after the install) or the login, and drops the
  action after 30 minutes. The action is saved only after the manifest was written; when SDK-Core is still not registered after a minute
  and the Package Manager is idle (a `Client.List` request answers), the action is dropped with a message. The packages it wrote are
  listed in `preparedPackages`, which `SetupSteps.InstallSdk` does not take for "a project at another version" and which the run clears
  once it has installed the SDK. A project whose SDK predates 2026.7.0 is told to change the version.
- **Choices** (`WizardChoices.cs`): the Virtuademy version and Visual Scripting only or with C#; **Next** opens the Summary. Only the entries that have
  `virtuademy-sdk-environments` are offered (2026.6 on: the engine cannot install an older one). The default is the platform
  version the computer's connections agree on, when the registry lists it ("<version> · <tenant>'s version (recommended)"), else the
  latest release; a project that has the SDK opens on its own version. The development version is behind the existing
  `ShowPrereleases` setting. A version for another Unity, a downgrade without the tick (`AllowDowngrade`), C# on an entry without an
  `interpreter` block **Next**, with the reason in the bar.
- **Summary** (`WizardChoices.cs`, `WizardPage.Summary`, the last value of the enum because `wizardPage` is serialized): the
  confirmation of a run. The choices (the version, with "updating from", "going back from" or "already installed"; the scripting),
  the titles of the steps `SetupRun.Plan(choices)` will run (the reason of a step it refuses), `SetupRun.DestructiveActions` under
  "Cannot be undone from here", and this computer (git, the editor modules, **Check again**) only when one is missing. A red check, a
  busy Unity, a refused step or a block of the choices disables **Start setup**. Development mode adds each step's detail, the skipped
  steps, the passing checks and the Unity the version needs. Back goes to Choices, or to the status page when **Run setup again** opened
  it (`summaryFromStatus`, with the installed version and scripting); the page is rebuilt only when what it shows changed.
- **Run** (`WizardRun.cs`): "Step n of m · NN%", a bar from `SetupRun.Progress`, the steps with their state and detail, "Stop after
  this step" (`StopAfterCurrentStep`), and a warning before the two steps that block the editor (`interpreter`, `vs-nodes`). It follows
  `SetupRun.Changed`. A run that ends well moves on to the status; one that fails stays, with Back and Try again (the same choices).
- **Project status** (`WizardStatus.cs`): "Your project is ready", or how many things need attention, and **Restart now**
  (`SetupRun.RestartEditorNow`) when `RestartPending`. A card of name-value lines (`InfoLine`, laid out like the Summary's choices): the version (Change version: the Choices page for a project that has
  the SDK), the packages (Reload packages: `PackageAutoUpdate.CheckNowAsync`, the same check as at editor start, run now, forgetting
  what `git ls-remote` answered before; commit SHAs, never versions), scripting (Add C# scripts or Remove C# scripts: the installed version with the other
  scripting, through the Summary, which warns and asks for the tick when C# scripts would stop running), the connections (Manage: the
  Connection page, which then returns here). Checks, each with a **Fix**, shown only when they fail ("Needs attention"), unless development mode shows them all ("Checks"): this computer, project settings, Addressables, TextMesh Pro
  essentials, the interpreter when installed, and a compile failure. A Fix is `SetupRun.Start(choices, onlySteps)` for the one step
  that makes the thing (project settings also takes `restart`), so it is shown on the Run page like any run; **Run setup again** is the
  whole run at the installed version, through the Summary (a Fix asks first, as above). **Restart now** shows while `RestartPending`, the session flag, is set. Show
all checks is disabled while a run is going. The status is read when the page shows and every two seconds.

`SetupConnections` and the pages never call the SDK before it is installed and compiled, and never block the editor: the bridge's
calls are awaited, and the long work is the engine's.

`ReadmeWindow` (`readme.uss`) is the Readme: a window rather than an asset, because a package is
read-only. Its "do not show" flag is `SetupUserSettings.HideReadmeOnStartup`, the key
`hideReadmeOnStartup` in `UserSettings/Virtuademy/setup.json`: per user and per project, not
versioned, and not an `EditorPrefs` key (machine-wide) or a project setting (committed). Its links
go to the documentation root and to the SDK section of the newest version whose pages were checked
(`SdkWindowLinks.documentation_sdk_url`: the site has no address for "the latest", so raise it when
a newer version is published).

The setup window or the Readme opens on its own at the first domain reload of an editor session — the project
opening, or this package arriving — through `SetupWindowStartup` (`[InitializeOnLoad]`, guarded by
a `SessionState` flag, skipped in batch mode). The setup window opens **only if the project needs it**:
`CreatorKitSetupWindow.FindStartupIssue` returns a reason when `virtuademy-sdk-environments` is not
registered, git cannot be run, an editor module is missing, the URP / API compatibility / Active
Input Handling / max texture size settings are off, the SDK's Addressables configuration is not applied
(`SetupSteps.ReadAddressablesConfigured() == false`), or TextMesh Pro's Essential Resources are missing. It reuses the
window's own checks, the ones the status page shows. Deliberately left out: the Unity
version (it needs the registry, hence the network) and the interpreter (HybridCLR). The reason is
logged when the window opens, and the window picks its page as above: a project without the SDK opens the Introduction,
one that is set up opens its status, where the rows that need attention say what. The toggle at the foot of the window turns the startup check off; the
choice is an `EditorPrefs` key per `PlayerSettings.productGUID` (default on), so it is per project
and per machine and nothing is committed. When the project needs nothing, the Readme opens instead,
unless `SetupUserSettings.HideReadmeOnStartup` is set; when the project needs setup but the footer
toggle is off, neither opens.

`PackageAutoUpdate` (`[InitializeOnLoad]`, also once per editor session by a `SessionState` flag, and never
in batch mode) keeps the project's Virtuademy git packages current: it runs the stale check of the `resolve`
step (`PackageLock.FindStaleAsync`: the lock and the manifest are read on the main thread, every
`git ls-remote` and the registry download run off it) and, **all or nothing**, unpins exactly the packages that
moved and calls `Client.Resolve()` when every remote answered, logging each as `<package> moved on its remote,
<old> -> <new>`; when any remote could not be asked it changes nothing and warns once. Unlike `resolve`, it
**includes this installer**: its tag is the one that moves when this feature changes, creators install it by git
URL, and nothing else would move it. That is safe because the flag is set before the lock is touched, so the
recompile of this assembly that follows finds it and does nothing. The second toggle at the foot of the window,
**Update Virtuademy git packages when the project opens**, turns it off (`EditorPrefs` key
`Virtuademy.SDK.Environments.Setup.AutoUpdatePackages.<productGUID>`, default on, per project and per machine). The header logo is `Icons/virtuademy-logo-{light,dark}.png`, picked by
`EditorGUIUtility.isProSkin`, rasterized from the Landing's `themes/virtuademy/full-*.svg`; the
`.meta` files are committed because a git package is immutable and Unity cannot generate them.

State lives in a `PackageManagerConfiguration` asset, created on first open at
`Assets/Virtuademy/Editor/Settings/SetupConfiguration.asset` and found afterwards by type
(`FindAssets("t:PackageManagerConfiguration")`), never by path.

"Our" packages are recognised by prefix — `com.anotherealitysrl.virtuademy`, `…reflectis`,
`…spacs` (`package_prefixes` in the window). A new prefix must be added there, or the window
neither sees those packages as installed nor unpins them on re-resolve (the button, or the
command line's `resolve` step).

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

`SetupCli.Run` does what the wizard and the window's buttons do, in batch mode. The steps, in the order they run:

| Step | Does | Default |
|---|---|---|
| `configure` | **Configure** | yes |
| `sdk` | **Install** next to Virtuademy SDK Environments, at a registry version | yes |
| `update` | **Update packages to selected version**, to the registry version given; `-vdAllowDowngrade` to go back | no |
| `resolve` | **Re-resolve packages from git**, only for the packages whose tag or branch moved | yes |
| `interpreter` | **Install interpreter**; with `-vdSetupScripting vs`, removes it (`-vdAllowScriptRemoval` when there are C# scripts) | yes |
| `addressables` | **Fix Addressables configurations** of the SDK's Addressables management window | yes |
| `vslibrary` | **Virtuademy ▸ Advanced ▸ Update Visual Scripting library** of the SDK (**Update VS Library** before 2026.7), when the library lacks the SDK's types | no |
| `tmp` | TextMesh Pro's **Import TMP Essential Resources**, when they are missing; keeps the editor open until the import ends | no |
| `check` | Changes nothing; reports | alone only |
| `plan` | Changes nothing; logs what `run` would do (`[SetupCli] PLAN`) | alone only |
| `run` | The wizard's whole setup run (`SetupRun`), in one launch | alone only |

```
Unity -batchmode -nographics -projectPath <project> -logFile - -ignoreCompilerErrors
      -executeMethod Virtuademy.SDK.Environments.Setup.Editor.SetupCli.Run
      -vdSetupVersion develop
      [-vdSetupSteps configure,sdk,update,resolve,interpreter,addressables,vslibrary,tmp | check | plan | run]
      [-vdSetupScripting vs | csharp] [-vdAllowDowngrade] [-vdAllowScriptRemoval]
      [-vdSetupRegistry <url or file>]
```

- **`run`: the whole setup in one launch.** It starts the wizard's engine (`SetupRun.Start`) with the choices of the command
  line and returns without exiting: Unity's batch mode keeps the editor and its update loop going until `EditorApplication.Exit`,
  so the engine resolves, waits for compiles and carries on across domain reloads as it does in the window. The watch that
  exits survives them: `SessionState` holds which run is watched (`run` or `tmp`) and since when, and `SetupCli`'s
  `[InitializeOnLoad]` constructor subscribes to `SetupRun.Changed` again and puts back what lives in static fields (the log
  prefix, `-vdSetupRegistry`, read again from the process's command line). When the run ends it logs the `[SetupCli] RUN` line
  (`steps` with `id`, `title`, `state`, `detail`; `failedStep`; `timedOut`; `restartPending`), the REPORT and the exit: `0` when
  every step is done or skipped (the restart step may wait: each batch launch is a new editor, so the next one applies Active
  Input Handling), `23` otherwise. A run still going after 90 minutes is ended with `23` (the steps' own waits are shorter).
  - **Choices.** `-vdSetupVersion` and `-vdSetupScripting` (`vs` or `csharp`) are required: the run never decides to keep or
    remove the interpreter by itself. Before anything is written it refuses a version the registry does not list or one before 2026.6 (`10`),
    another Unity (`14`), `csharp` on a version without an interpreter (`17`) and a project with packages from before 2026.6
    (`15`). The Summary's two ticks are options: `-vdAllowDowngrade` for an older version, `-vdAllowScriptRemoval` to remove
    the interpreter from a project that has C# scripts in `Assets/VirtuademyEnvironmentScripts`; without the one it needs, the
    run does not start and exits `21`. The Addressables reset and the update routines need no option: `run` is the Start click.
  - **`plan`** takes the same options, changes nothing and logs `[SetupCli] PLAN`: `version`, `fromVersion`, `scripting`,
    `steps` (`SetupRun.Plan` with every confirmation given), `irreversible` (`SetupRun.DestructiveActions`, the Summary's
    *Cannot be undone* list), `confirmationsNeeded` (the options missing) and `startable`. Its exit code is the one `run` would
    stop with before starting, `0` when it would start.
  - **`-quit` is refused** (`10`) with `run` and `tmp`: Unity would quit as soon as `Run` returns. Keep
    `-ignoreCompilerErrors`: after an update, the project may not compile until the update routines have run.
  - The engine's messages are the wizard's (`SetupSteps.Caller` is `Wizard` for `run`), with `[Setup] ` for the engine's lines
    and `[SetupCli] ` for the steps' own.
- **One launch, one round** (every step but `run` and `tmp`). Every package change ends in a resolve and a domain reload, which
  would cut a running method short. A launch applies what it can, stops after the first change
  that needs a restart and exits with `2`; the caller launches again until the code is `0`. A new
  project takes three launches, measured on a scratch project on 2026-10-09:
  1. Configure and the SDK packages, written to the manifest (about 20 s);
  2. the entry's HybridCLR (`interpreter`), written to the manifest, after the SDK packages
     resolve (about 90 s);
  3. the interpreter, through `HotUpdateSetupper.Setup`, which clones hybridclr and il2cpp_plus
     (about 40 s), and then the Addressables configuration, in the same launch.

  A fourth launch is needed only when the interpreter setup is not ready until a recompilation.
  An update takes two launches, and a moved tag one more.
- **The interpreter step follows the registry.** It installs the HybridCLR that the project's
  recorded version declares. When the manifest asks for another one it switches to it, which is a
  repair rather than a version update; exit `2`. When the local IL2CPP holds another libil2cpp,
  the setup reinstalls it.
- **The `addressables` step** calls `AddressablesConfiguration.Apply()` of
  Virtuademy-SDK-Environments, by reflection, as the interpreter step reaches `HotUpdateSetupper`:
  this package installs the SDK, so it cannot compile against it. It runs after `interpreter` and
  needs the SDK resolved (exit `16` when it is not). A project that is not configured gets the
  SDK's Addressables profile and its groups **reset to the single default group**: every other
  group and every entry is removed, as the window's button does. A configured project is left as
  it is. Without it a new project keeps Unity's default profile and groups, and the first
  "Build & Deploy" builds every platform for minutes and fails at the end. An SDK version that
  predates `AddressablesConfiguration` is skipped with a warning (exit `0`); one whose editor
  assembly did not compile exits `16`.
- **The `resolve` step** compares, for every git package of ours in `Packages/packages-lock.json`,
  the commit it pins with what the manifest's ref points to on the remote now (`git ls-remote`; a
  tag is peeled with `^{}`, so an annotated tag gives its commit; a commit ref is up to date by
  definition; no ref means the remote's HEAD). The ones that moved are unpinned and the step exits
  `2`; the next launch resolves them again and finds none moved (exit `0`), with the lock back on
  the tag's commit. Nothing else is unpinned. It sits among the default steps, after `sdk` and
  before `interpreter`, because it does nothing when nothing moved.
  - **Unknown is not stale, and one unknown stops the step.** When the remote cannot be asked (no
    network, no access, the ref is gone) its packages are "unknown". The step is all or nothing: if
    any package is unknown it unpins nothing, not even the packages known to have moved (a partial
    update would leave a mix of new and old packages), warns and exits `0` (`ls-remote` gives each
    package 30 s and never prompts for credentials). `check` reports the same.
  - **Which packages.** The window button's rule: git entries of the lock that a registry entry
    lists, with one of our prefixes. The installer package is left out on purpose, by name: it is
    the code that is running (unpinning it recompiles this assembly) and its version is the
    project's choice, not the registry's. It is not in any registry entry, so the button never
    touched it either. Only the editor-start update (`PackageAutoUpdate`, above) includes it.
  - **The window button keeps its meaning**: it unpins every one of those packages and resolves,
    moved or not, which is what to press when a pin is wrong for another reason. It shares the
    lock code (`PackageLock`) with this step.
- **The `update` step** is **Update packages to selected version** without the window: for a
  project that records another registry version it installs what the target adds, moves what it
  changes, **removes what it does not list**, and moves HybridCLR to the target's `interpreter` (or
  out of the project, for a version before 2026.6), through the same static method the window's
  button calls (`ApplyVersionUpdate`). It writes the manifest and `SetupConfiguration.asset` and
  exits `2`; the next launch resolves the packages and exits `0`, or `16` when
  Virtuademy-SDK-Environments did not resolve. It refuses a project that records no version (`16`),
  an editor that is not the target's `requiredUnityVersion` (`14`) and a version the registry does
  not list (`10`), and it cannot be combined with `sdk` (`10`). It is never a default step. Before
  2026.6 the packages are not installed by the command line, but a project that has them can be
  moved off them. "Resolve breaking changes automatically" is not honoured: a warning says so.
  - **Update routines.** When the move crosses a step that has an update routine in the new SDK (a
    menu item under `Virtuademy/Update routines`, such as `v2026.5 -> v2026.6`), the launch that
    finds the project at the target logs a warning with the menu name: the routine must run next.
    The step does not run it. The packages of the new SDK only exist after the resolve, so the
    first launch writes `Library/VirtuademySetupCli.updated-from` and the second lists the routines
    and deletes it. A pre-release target (`develop`) or a downgrade is not a step between released
    versions: the log points at the menu instead.
- **The `vslibrary` step** calls `VisualScriptingSetupEditor.Setup()` of Virtuademy-SDK-Environments,
  the **Virtuademy ▸ Advanced ▸ Update Visual Scripting library** menu item (**Update VS Library** before 2026.7), by reflection. It marks Visual Scripting as
  used, resets its type options to the defaults, adds every type the project's
  `VisualScriptingCustomTypeCollector` assets list, saves the settings and rebuilds the units
  (`Assets/Unity.VisualScripting.Generated/.../UnitOptions.db`, 36 MB on a scratch project).
  The SDK's method cannot tell it is already done, so the step asks first
  (`SetupSteps.ReadVisualScriptingLibraryCurrent`, after marking Visual Scripting as used) and leaves
  a library that has every type of the SDK alone. **Opt-in**: when it works it took 73 s and 80 s on
  two consecutive runs (2026-10-14). Run it after the SDK is installed, for example
  `-vdSetupSteps vslibrary`. An SDK without the method is skipped with a warning; an exception in it
  exits `20`.
- **The `tmp` step** imports TextMesh Pro's Essential Resources (`TMP_PackageResourceImporter.ImportResources`,
  by reflection) when TextMesh Pro is in the project and its settings asset is not. The import ends
  after the launch's method returns, so the step hands it to the engine's `tmp-essentials` step
  (`SetupRun.Start` with that step only) and the editor stays open, as for `run`, until the import
  is done: `0`, or `22` when it failed or did not end in 3 minutes. Nothing to import: `0` at once.
- **`-vdSetupScripting vs` with `interpreter`** removes HybridCLR from the manifest
  (`SetupSteps.RemoveInterpreter`, the engine's code) and exits `2`; the next launch finds it gone.
  With C# scripts in `Assets/VirtuademyEnvironmentScripts` it needs `-vdAllowScriptRemoval` (`21`
  otherwise). `-vdSetupScripting` with any other step is refused (`10`).
- **`-ignoreCompilerErrors`** lets Unity run the method on a project that does not compile, for
  example one that pulled HybridCLR 9.0.0, so the step can repair it. Measured: two launches, from
  24 compile errors to `0`. Without the flag, Unity aborts before `-executeMethod` (exit `1`). The
  report's `scriptsCompile` says whether the scripts compile, and `check` fails when they do not.
- **`-vdSetupRegistry`** reads the registry from another URL or from a local file (for example the
  meta-repo's `contracts/PackageRegistry.json`), to try a registry change before publishing it.
  The window always reads the published one.
- **Nothing has to survive a reload** (but in `run` and `tmp`, above). The steps write the manifest directly, as `InstallPackages`
  does, instead of calling `Client.Add`/`Client.Resolve`; the next launch resolves at startup. The
  interpreter retries are counted in `Library/VirtuademySetupCli.interpreter-attempts`, at most
  three, as `HotUpdateSetupper` allows itself.
- **Same logic as the window, and as the engine.** Configure, the install with dependencies, the configuration asset
  the interpreter checks and the version update (`ApplyVersionUpdate`, with the installed-list
  upkeep it starts from) are internal static methods of `CreatorKitSetupWindow`, called by both;
  the code of each step is in `SetupSteps`, which `SetupCli` and `SetupRun` call (below); the lock file code is `PackageLock`. The files have the same format: manifest lines `url#version`, and `SetupConfiguration.asset`
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
- **The `sdk` step does not update.** It refuses a project that already has Virtuademy packages of
  another version, or a manifest asking for another ref (exit `15`). Moving between versions is the
  `update` step, or the window's *Update packages to selected version*; both remove what the new
  version drops. "Already installed" compares the Virtuademy-SDK-Environments line only, not its
  dependencies.
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
    false when it could not;
  - the Addressables are configured: the report's `addressablesConfigured` is `false`. It is `null`
    when the SDK has no `AddressablesConfiguration` to ask (not installed yet, or older than the
    class), and `null` does not fail the check;
  - no git package of ours moved on the remote: `stalePackages` lists each one that did (`name`,
    `reference`, `lockedCommit`, `remoteCommit`) and fails the check. `stalePackagesChecked` is
    `false` when the remote could not be asked for some package, which does **not** fail it. The
    check reads the registry (to know which packages are ours) as soon as the project has a
    `packages-lock.json`, so a project without network reports `stalePackagesChecked: false`.
  - TextMesh Pro's Essential Resources are there when TextMesh Pro is (`tmpEssentialsMissing`), as the status page counts them.
  - Reported, not counted: `scripting` (`csharp` when HybridCLR is in the manifest, else `vs`), `environmentScripts` (the C#
    scripts in `Assets/VirtuademyEnvironmentScripts`), `vsLibraryCurrent` (whether the node library has the SDK's types; `null`
    when Visual Scripting has not started in the launch, or the SDK is not there), `tmpPresent`.
- The method exits the editor itself, so `-quit` is not needed (and is refused with `run` and `tmp`). Every launch ends with a
  `[SetupCli] REPORT` line (one JSON object with every check) and `[SetupCli] EXIT <code>`.
- **Exit `1` without those two lines is Unity's own failure, not SetupCli's.** It happens when the
  packages do not resolve or the scripts do not compile, and Unity aborts the batch run before
  `-executeMethod`. The cause is earlier in the log.

| Exit | Meaning |
|---|---|
| `0` | Done |
| `1` | Unexpected exception (stack trace in the log), or Unity aborted before `-executeMethod` |
| `2` | The project changed: launch again |
| `3` | `check`: not set up, including a git package that moved on the remote |
| `10` | Bad arguments (`sdk` with `update` included), a version the registry does not list, or one before 2026.6 for `sdk`, `plan` and `run` (no Virtuademy-SDK-Environments) |
| `11` | git cannot be run from the editor process |
| `12` | Editor modules missing (Android, iOS, WebGL, Windows) |
| `13` | Registry unreadable or empty, it no longer lists the project's recorded version, or its `interpreter` is malformed |
| `14` | The editor is not the entry's `requiredUnityVersion` (`sdk`, `update`, `plan`, `run`) |
| `15` | Another Virtuademy version is installed (`sdk`), or packages from before 2026.6 (`plan`, `run`) |
| `16` | Virtuademy-SDK-Environments missing, not resolved or not compiled, or the project records no version (`interpreter`, `update`) |
| `17` | Interpreter setup failed: the entry declares no `interpreter` (also `csharp` with `plan` and `run`), HybridCLR did not resolve or compile, or three configurations did not make it ready |
| `18` | Project settings could not be applied |
| `19` | The `addressables` step failed: `AddressablesConfiguration.Apply()` threw, or the project did not end configured |
| `20` | The `vslibrary` step failed: `VisualScriptingSetupEditor.Setup()` threw |
| `21` | A confirmation is missing: `-vdAllowDowngrade` (`update`, `plan`, `run`) or `-vdAllowScriptRemoval` (`interpreter` with `vs`, `plan`, `run`). Nothing was changed |
| `22` | The `tmp` step failed: the Essential Resources were not imported |
| `23` | A step of `run` failed, or the run did not end in 90 minutes: the `RUN` line names the step |

`Virtuademy-Env-Test` drives it with `scripts/unity.sh setup <version>`, which passes
`-ignoreCompilerErrors` and relaunches until the code is not `2`; `SETUP_REGISTRY=<file or url>`
becomes `-vdSetupRegistry`.

### The setup run (`SetupRun`)

`SetupRun` is the engine of the wizard's Run page: it runs the steps of the setup in the open editor,
one at a time, by itself, and goes on across the domain reloads that package installs and recompilations
cause. It draws nothing. The pages call `Plan(choices)`, `Start(choices)`, `StopAfterCurrentStep()` and
`RestartEditorNow()`, read `Steps`, `Progress`, `IsRunning` and `RestartPending`, and look again when
`Changed` is raised (always on the main thread). `SetupChoices` is the Choices page's output: `Version` (a registry
entry's `ReflectisVersion`), `Scripting` (`VisualScriptingOnly` or `VisualScriptingAndCSharp`), `AllowDowngrade`
(the confirmation tick of a downgrade) and `AllowScriptRemoval` (the Summary's tick: `Start` refuses to remove the interpreter
from a project that has C# scripts without it; `Plan` shows the removal either way).

**One step code for both callers.** What a step does is in `SetupSteps`, which `SetupCli` calls too: the command line
keeps its arguments, its REPORT and its exit codes, and the engine runs the same methods. A method does one round and
returns a `StepResult`: one of the command line's exit codes, a short message (the step's detail in the engine) and
whether it changed the project. `ExitRelaunch` means "the project changed: resolve and compile, then call me again".
The command line exits and is launched again; the engine calls `Client.Resolve`, waits until the Package Manager has
written `packages-lock.json` for the manifest's git packages of ours and the editor has been neither compiling nor
importing for 2.5 s, and calls the step again (at most 8 rounds per step; 15 minutes for the resolution). The logs
keep the same lines with another prefix: `SetupSteps.Prefix` is `[SetupCli] ` in the command line and `[Setup] ` in the editor. The few
messages that say what to do next (launch again, the `-vdSetup...` arguments, "run the sdk step") are worded for the caller:
`SetupSteps.Caller` is `CommandLine` (set by `SetupCli`, whose text is unchanged) or `Wizard`.

The steps, in order. A step with nothing to do is `Skipped`, with the reason as its detail; a failed step ends the run and
leaves the later ones `Pending`. The weights give `Progress`: the share, among the steps that were not skipped, of the weight
that is done (inside a step there is no percentage: Unity gives none for package resolution).

| Step id | Weight | What it does | Skipped when |
|---|---|---|---|
| `project-settings` | 1 | `SetupSteps.Configure`: URP, API compatibility, Active Input Handling Both, max texture size. A change of the input handling is noted for the editor session (`CreatorKitSetupWindow.InputHandlingChangedThisSession`, a `SessionState` flag set where the setting is applied); the step **never** offers or performs a restart | already configured |
| `packages` | 6 | `InstallSdk` (the project records no version) or `UpdateVersion` (another version, with `trackOrigin: false`: the engine keeps where the run started). Refuses a downgrade without `AllowDowngrade` and a version whose `requiredUnityVersion` is not the editor's. The registry is read off the main thread | the recorded version is the chosen one and the SDK is registered |
| `compile` | 2 | `AssetDatabase.Refresh`, then waits until the editor has settled; fails on `EditorUtility.scriptCompilationFailed` with a pointer to the Console. When `update-routine` is still to run it does not fail: an updated project often does not compile until the routines have renamed what the new SDK renamed, and the compile is checked again after them | never |
| `update-routine` | 4 | the SDK's update routines between the version the run started from and the new one, newest first (below) | a new project, the same version, a version that does not go forward (a pre-release counts as later than every release), a move the SDK has no routine for |
| `interpreter` | 6 | `InstallInterpreter`, rounds as above (manifest, resolve, compile, then the setupper, which blocks the editor while it clones) | `VisualScriptingOnly` |
| `addressables` | 1 | `ConfigureAddressables`: the SDK's profile and the single default group | already configured, or an SDK that predates it |
| `vs-nodes` | 3 | `UpdateVisualScriptingLibrary`, which blocks the editor for over a minute. The SDK's `VisualScriptingSetupEditor.Setup()` resets Visual Scripting's type options to the defaults first, so the types in `BoltCore.Configuration.typeOptions` are read before it and the ones it dropped are put back after it (by reflection, with a second unit rebuild only when there were such types): the library only grows | the library has the SDK's types (`ReadVisualScriptingLibraryCurrent`: every type of the SDK's `VisualScriptingCustomTypeCollector` assets in `typeOptions`, their namespaces in `assemblyOptions`), or an SDK without the method |
| `tmp-essentials` | 1 | `TMPro.TMP_PackageResourceImporter.ImportResources(true, false, false)` by reflection, then waits for the `TMP_Settings` asset | present, or TextMesh Pro is not in the project |
| `connections` | 0.5 | reads, and changes nothing: how many connections `%USERPROFILE%\.virtuademy\sdk\status.json` lists and whether one runs `Version` as its `platformVersion`. It never fails the run | never |
| `restart` | 0.5 | only when the input handling was changed in this editor session (by this run, an earlier one or the long page): ends the run with the step `Pending` (`Progress` is 1). `RestartPending` is that session flag, not a field of the run: a later run, a "Try again" or a Fix leaves it set. `RestartEditorNow()` reopens the project (`EditorApplication.OpenProject`, after the open scenes are saved or the person cancels). Never automatic | the input handling did not change in this session |

- **`Plan(choices)`** reads and changes nothing, with no network: the steps as `Start` would begin them, with the skips the
  project's state allows, and a `Failed` step with the reason for a choice that `Start` refuses (no version, a downgrade
  without confirmation, a version for another Unity, which it knows only when the registry was read before:
  `SetupSteps.UseRegistry`, which the window calls when it has read the registry). `Start` refuses the same choices: the steps are
  then the plan, `IsRunning` stays false. It also refuses while a run is going. `Start(choices, onlySteps)` limits the run to the steps
  named (the status page's Fix): the others are `Skipped` with `SetupRun.NotPartOfRun`. The version the run starts from is the recorded
  one **only when `virtuademy-sdk-environments` is registered**: the window records its default (the latest release) for a project
  that has no SDK, and that is not what is installed. `Start` itself skips only what follows from the choices (the interpreter, the
  update routines); what depends on the project each step decides when its turn comes, because an earlier step changes it.
  `Plan` and `Start` also fail `packages` up front, before any step runs, for a project that has packages of ours
  (`SetupSteps.OtherPackagesOfOurs`: the installer and `preparedPackages` left out) and no `virtuademy-sdk-environments`: it is from before
  2026.6 (other package ids), or was set up by hand, and the run would half change it. The reason points to the update documentation.
- **The interpreter step installs or removes.** With `VisualScriptingAndCSharp` it installs HybridCLR (`InstallInterpreter`); with
  `VisualScriptingOnly` on a project whose manifest has HybridCLR it takes it out (`RemoveInterpreter`: the manifest entry goes, the
  packages resolve, and the SDK's `HYBRIDCLR_INSTALLED` assembly drops out with it). The C# scripts in
  `Assets/VirtuademyEnvironmentScripts` stay, as do `HybridCLRData/` and `ProjectSettings/HybridCLRSettings.asset`. `Plan` gives the step
  the detail `SetupRun.RemovesInterpreterDetail` then, `DestructiveActions` lists it with the number of scripts, and the Summary adds a
  red box and a tick (`choiceAllowScriptRemoval`) when there are scripts. The window's defaults for the scripting choice (Change
  version, Run setup again, the Fixes) read the manifest (`SetupSteps.InterpreterInManifest`), not `IsHybridCLRInstalled`, which an
  asynchronous package listing fills a moment after the window opens: read early, it would have proposed to remove the interpreter.
- **Before a run starts** the Summary page lists `SetupRun.DestructiveActions(choices, plan)`, when it is not empty, above **Start
  setup** (Try again and a Fix do not go through it; a Fix has its own dialog): a version moved (the packages the new version does not
  list are removed), update routines (they rewrite scripts and assets), and the Addressables reset (only for a project that is not
  configured, and, without the SDK to ask, only when `Assets/AddressableAssetsData` exists). Before the routines close the open
  scenes, `ResolveUnsavedScenes` asks about the ones with unsaved changes (Save and continue; Continue without saving, which reopens them
  from disk; Stop the setup, which fails the step having changed nothing).
- **Waits are not polled every tick.** The Choices page reads `Plan` again only when a choice changes, the project is read again, or 3 s
  have passed; `packages` and the rounds of the interpreter check the Package Manager, the manifest and the lock once a second. The
  `vs-nodes` counter of attempts is saved before the call, so a reload that cuts the step in the middle is counted.
- **State and resume.** `UserSettings/Virtuademy/setup-run.json` (per user and project, not versioned) holds the choices,
  the version the run started from, every step's state and detail, and the scratch of the step in progress (rounds
  waited, phase of a routine). `SetupRun` is `[InitializeOnLoad]`: after each domain reload it reads the file and goes on with
  the step that was running, which starts again from its beginning (every step does only what is left). A `SessionState` flag,
  which outlives reloads and not the editor, tells the session: in a new editor session a run that was going is shown as
  failed at its step ("the editor was closed"), and a restart that was waiting is shown as done. A file whose `Format` is not
  the one this code writes is no run. It does nothing in
  Unity's asset import workers, which load this assembly in processes of their own without the flag and would rewrite the file.
- **Update routines.** `SetupUpdateRoutines` drives the routines by reflection, with the windows' defaults, as
  `UpdateRoutinesCli` does (whose `Run` cannot be called: it exits the editor). It finds the ones the SDK has from their
  menu items (`Virtuademy/Update routines/...`) and recognises them by the series they move to: `2026.6`
  (`VirtuademyRenameRoutine.ScanProject` and `Apply`; it rewrites files, so Unity recompiles and reloads, after which the editor re-saves
  the changed files by itself, so it is apply, wait for the re-save, scan again, at most 4 rounds) and `2026.5`
  (`CheckPlatformUnitMobilePortRoutine`; refused while `2026.6` still has graph data to migrate). A routine it does not know
  is named in the step's detail, to start from the menu. The rename closes the open scenes **without saving** them.
  After the routines the compile is checked: that is what they repair.
- **The step is idempotent, the run is not transactional.** Nothing is undone when a step fails; "Run setup again" does what is left.

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
  `Packages/manifest.json` of the Virtuademy application project pins at the release commit. The meta-repo's
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

The installer is not mounted in the Virtuademy application project — there the packages are embedded submodules and
the window has nothing to do. It is exercised in `Virtuademy-Env-Test`, an empty Unity project that
mounts this repository under `Packages/` and runs the install and publish flow from outside.

To test a registry change without touching the blob, point an entry at a branch with
`"prerelease": true`, publish, and select it with *Show pre-releases* on.

## Known issues

- **Not exercised end to end.** The pages were checked for layout in batch mode, 400 px wide, one by one (Introduction, Connection,
  Choices, Run, status), and a Fix was run through the window and the engine to its end. Not run: the Connection page against an
  SDK-Core that has `SetupBridge` (login, import, the install of SDK-Core from **Choose the file...** and the resume after the reload),
  a full install started from the Choices page (the engine itself did one across its reloads in batch mode, `SetupCli -vdSetupSteps
  run` on a new project, 2026-10-29: one launch, 398 s, every step done), the interpreter install, the update routines (written
  against the SDK's code, not run on a project that needs them), and the light skin (its classes are in `wizard.uss`, not seen). The
  interpreter's removal ran through the engine in batch mode on a project with a C# script (`run -vdSetupScripting vs
  -vdAllowScriptRemoval`, 56 s).
- **The interpreter step blocks the editor** while `HotUpdateSetupper` clones repositories (a few minutes on a new
  machine), as the window's button does: the page shows the step as running and cannot repaint until it ends.
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
