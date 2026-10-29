# Changelog

All notable changes to `com.anotherealitysrl.virtuademy-sdk-environments-setup`.

## Unreleased

### Added

- **The setup window is a wizard of six pages.** **Introduction** (optional: three rows, a reminder to back up or commit the
  project, "Don't show this introduction again"), **Connection**, **Choices**, **Summary**, **Run** and **Project status**, one at a time, with
  **Back** and the main action in a bar at the bottom. The pages only start the work and show it; the work is the setup run's
  (below). **Development mode (internal testing)**, at the foot, is one switch for the whole setup: it lists the development
  version and adds the details of the Summary. It is the long page's **Show pre-releases**, saved in the setup configuration
  asset. The whole page as before (nothing the window did is gone) has its own menu entry, **Virtuademy ▸ Advanced ▸ All setup
  checks**, with **Back to the guided setup** at its top: it is out of the wizard, which no longer offers it.
  - **Connection** is optional at every point (**Connect later**). It opens the Virtuademy Back Office (`bo.virtuademy.io`; a note
    says that an organization with a Back Office of its own downloads the file from there: the page asks for no address), lists the connections of this computer (organization, environment, platform version,
    account, a warning when the app is not one environments can be published to) with **Log in** and **Log out**, and **Add an
    organization** imports the file from the Back Office and logs in. The connections are the SDK's, so the page reaches them
    through the SDK's `SetupBridge` by reflection (SDK 2026.7.0 on); without it the list is read from the SDK's status file and
    the page says what is not available. On a project that has no SDK yet, **Choose the file...** and **Log in** first offer to install
    just SDK-Core and what it needs; after Unity has recompiled, the file picker or the login opens by itself, so the file is chosen
    once. Organizations show their name alone; a sandbox or prep connection carries a coloured chip (blue Sandbox, orange Prep), as
    in Publish Environments. While only the development version has the connection
    support for this Unity, the page says that development mode makes **Add an organization** and **Log in** available.
    Removing a connection stays in Publish Environments.
  - **Choices**: the Virtuademy version (the one your connections run is the default and marked recommended, else the latest
    release; the development version only in development mode), which Unity it needs (another Unity blocks the start), an upgrade note or, for an older version, a
    downgrade warning with a confirmation, and Visual Scripting only or with C# scripts. Only versions from 2026.6 on are offered:
    they are the ones the setup run can install.
  - **Summary** ("Ready to set up") is the confirmation: the choices (a grid in a card with a blue edge: each name in bold, its value
    beside it, a "Development" chip on the development version, and a grey note on whether it updates or goes back), what the setup will do, what cannot be undone, and **Start
    setup**. Git and the editor modules show only when one is missing. Development mode adds each step's detail, what is already
    done, the checks that pass and the Unity the version needs.
  - **Run**: the step you are at and the percentage, a progress bar, the steps with what each did, **Stop after this step**, and a
    warning before the two steps that block the editor. A failure stays on the page with the reason and **Try again**.
  - **Project status** is the result of a run and the home of a project that is set up: a card laid out like the Summary's choices
    (the name in bold, the value beside it) with the Virtuademy version (**Change
    version**), the packages (**Reload packages** runs the check of the editor-start update now: it compares commits, never
    versions, and says what it reloaded or that all are up to date), the scripting (**Add C# scripts** or **Remove C# scripts**, both
    confirmed on the Summary at the installed version), the connections
    (**Manage**), and the checks that fail, each with **Fix** (all of them in development mode): this computer, project settings, Addressables, TextMesh Pro's Essential
    Resources, the interpreter, and a compile failure. **Restart now** applies Active Input Handling when the run changed it.
    **Run setup again** opens the Summary of the whole setup at the installed version.
- **A Readme window, shown when the project opens and nothing needs setup.** **Virtuademy ▸ Readme** opens
  it at any time: what the SDK is, the first steps (scene, placeholders, build and publish), the menu entries
  and links to the documentation. **Do not show this when the project opens** is saved per user and per
  project, in `UserSettings/Virtuademy/setup.json` (not versioned).
- **The setup run (`SetupRun`): an engine that runs every step of the setup inside the editor.** It does, one at a
  time and by itself, what the command line does: project settings, the SDK packages at a chosen version (install,
  update or change; a downgrade needs a confirmation, a version for another Unity is refused), the wait for the
  compilation, the update routines between the old version and the new one, the interpreter (installed with C# scripts; removed when
  a project that has it is set up with Visual Scripting only, after a red warning and a tick when it has C# scripts), the
  Addressables configuration, the Visual Scripting nodes, TextMesh Pro's Essential Resources when missing, a read of the
  computer's connections, and the editor restart, which is never automatic. It survives the domain reloads those steps
  cause (state in `UserSettings/Virtuademy/setup-run.json`), reports progress from the steps' weights and raises
  `Changed` on the main thread. `Plan` shows what a run would do and refuses a choice that cannot run. `Start` can limit a run
  to some steps (the status page's **Fix**). The version a run starts from is the recorded one only when the SDK is installed:
  the window records its default for a project that has none, and that is not what is installed. Removing the interpreter from a
  project that has C# scripts needs `SetupChoices.AllowScriptRemoval` (the Summary's tick): `Start` refuses it otherwise.
- **The whole setup in one command line launch: `SetupCli -vdSetupSteps run`.** It runs the setup run's engine in batch mode, with
  `-vdSetupVersion` and `-vdSetupScripting vs` or `csharp` (both required: the run never removes or keeps the interpreter by
  default), and keeps the editor open across the run's domain reloads until it ends: one launch instead of three or four, and the
  update routines, the Visual Scripting nodes and TextMesh Pro's essentials included. The Summary's confirmations are options:
  `-vdAllowDowngrade` and `-vdAllowScriptRemoval`; without the one a run needs, nothing changes and it exits `21`. It ends with a
  `[SetupCli] RUN` JSON line (the steps with state and detail, the step that failed, `restartPending`) and exits `0`, or `23` when a
  step failed. `-quit` is refused, and a run still going after 90 minutes is ended. `-vdSetupSteps plan` changes nothing and logs
  the Summary as a `[SetupCli] PLAN` JSON line: the steps, what cannot be undone, `confirmationsNeeded` and `startable`.
- **`SetupCli`: the rest of what the wizard does.** `-vdSetupScripting vs` makes the `interpreter` step remove the interpreter (with
  `-vdAllowScriptRemoval` when the project has C# scripts); a `tmp` step imports TextMesh Pro's Essential Resources when they are
  missing (opt-in; the editor stays open until the import ends); the `update` step needs `-vdAllowDowngrade` to go back to an older
  version (behaviour change: it went back without asking); the `vslibrary` step leaves a library that has the SDK's types alone.
  The REPORT has `scripting`, `environmentScripts`, `vsLibraryCurrent`, `tmpPresent` and `tmpEssentialsMissing`, and `check` exits `3`
  when the essentials are missing, as the status page counts them. New exit codes: `21` a confirmation is missing, `22` the TextMesh
  Pro import failed, `23` a step of `run` failed.

### Changed

- **The setup run says before it starts what cannot be undone.** The Summary page lists, above **Start setup**, what this run will
  really do of: moving Virtuademy to another version (the packages the new one does not list are removed), the update routines (they
  rewrite scripts and assets), and the Addressables reset (only for a project whose Addressables are not configured). Nothing when
  there is none. Before the update routines close the open scenes, a scene with unsaved changes gets its
  own dialog: **Save and continue**, **Continue without saving** (the changes are lost, the scenes are reopened as they are on disk) or
  **Stop the setup**, which ends the run before the routines change anything. A project's types in Visual Scripting's node library are
  kept: the library setup (the **Visual Scripting nodes** step and `SetupCli`'s `vslibrary`) puts back the ones its reset dropped, and
  the step is skipped when the library has the SDK's types already. On a new project, where Visual Scripting has not started yet, the
  step first marks Visual Scripting as used, as opening a graph does, so that its options can be read.
- **The editor restart stays due until it happens.** Active Input Handling changed in this editor session is a `SessionState` flag, no
  longer a field of the run: a later run, **Try again** or a **Fix** no longer drops it, and the status page keeps **Restart now** (and
  "almost ready") until the editor has restarted.
- **A project with Virtuademy packages from before 2026.6 is no longer half changed.** The packages step fails up front, before any step runs,
  and points to the update documentation. `preparedPackages` (the packages the Connection page installed to reach the connections) is
  documented, and cleared once the run has installed the SDK. A `setup-run.json` of another `Format` is ignored.
- **The Virtuademy menu is ordered for creators.** **Setup project** (priority 0) and **Readme** (2) sit with the SDK's **Publish
  Environments** (1) at the top; the SDK's **Update routines** and **Advanced** submenus follow, each after a separator. **All setup
  checks** is in Advanced (40), beside the SDK's Visual Scripting library update, which moved there in 2026.7. The Readme's list of
  menu entries and the setup's messages name the new places, and say the old one for an SDK before 2026.7.
- **Smaller.** *All setup checks* is refused while a run is going. The Run, Connection and Summary pages no longer come back empty
  after a domain reload: the signature of what they last drew, carried across the reload with the window's other private fields, is
  reset when the pages are built again. A step skipped because it had nothing to do is green on the Run page ("Not needed: ..."). The Summary
  and Choices pages no longer freeze the editor ("Hold on", every few seconds, in a project that has the SDK): the plan asked whether
  Visual Scripting's library has the SDK's types, and the SDK's collector resolves each type name by scanning every assembly, about
  4 s a time. The setup now reads the same names and resolves them in one pass over the domain's types (the same 21 types, in the
  same order, in about 1 s once per domain), and keeps the answer until the library's own setup runs or the domain reloads. A failing compile is no longer let through when the update routines are
  skipped. The install of SDK-Core from the Connection page saves its pending action only after the manifest was written, and gives up with a message when
  SDK-Core never resolves. The few messages that told the command line's way to a wizard run ("Launch again", `-vdSetupSteps`, "run the
  sdk step") are worded for the wizard (the command line's text is unchanged). The Choices page and the package waits no longer scan the
  project on every tick; the `vs-nodes` attempt counter is saved before the call; **Reload packages** no longer forgets the answers of a
  check that is running; the introduction says Unity restarts only if a setting needs it, and only when you click.
- **`SetupCli`'s steps moved to `SetupSteps`**, which the setup run calls too, so the command line and the
  engine run the same code. The command line keeps its arguments, its REPORT and its exit codes, and adds to them (above).
- **Project settings: Active Input Handling must be Both.** A new row in the Project settings group (and in the
  status page): Player Settings > Active Input Handling is **Both**, as in the Virtuademy player, where environment
  scripts may read the old `Input` class and the SDK uses the Input System package. **Configure** sets it and offers
  to restart the editor, because Unity applies it only when the editor starts; `SetupCli`'s `configure` step sets
  it too, and its report has `inputHandlingBoth`. Until now a new project met the Input System package's own
  dialog ("native platform backend not enabled", Enable and restart) in the middle of the SDK install, and a
  project on one input system only lost the other in Play mode (on the new one only, the old `Input` class throws).
- **Startup.** With a problem the window opens, as it did, unless **Open this window when the project opens and needs setup** is
  off: a project that is not set up opens the introduction (the Connection page when the introduction is turned off), one that
  is set up opens its status. The startup check also looks at the SDK's Addressables configuration and at TextMesh Pro's
  Essential Resources now. With no problem the Readme opens, unless it was turned off for the project. Still once per editor
  session and never in batch mode. `SetupCli` and the automatic package update are unchanged.

## v2.0.0

### Added to this release on 2026-10-14 (tag moved)

The `v2.0.0` tag was moved on 2026-10-14 onto the commit that adds the changes below. They are editor-only
(command lines, editor windows); nothing a published world or the app loads changed. A project resolved before
keeps the earlier commit until it re-resolves: the setup's editor-start update or `SetupCli -vdSetupSteps
resolve` does it. Projects whose installer is still the earlier v2.0.0 commit need one manual re-resolve of
the installer first: that commit has no editor-start update.

#### Added

- **The editor keeps the project's Virtuademy git packages current when it opens**. Once per
  editor session, in the editor and never in batch mode (`SetupCli`'s `resolve` step does it there), a
  background check compares, for every Virtuademy git package in `packages-lock.json`, the pinned commit with
  what the manifest's ref (tag, branch, or none) points to on the remote (`git ls-remote`, off the main thread).
  **All or nothing:** if every remote answered and some moved, exactly those are unpinned and the editor
  re-resolves, after logging each as `<package> moved on its remote, <old> -> <new>`; if any remote could not be
  checked nothing is changed and one warning is logged. **The installer package itself is included** (unlike the
  `resolve` step): it is the package whose tag moves when this feature is fixed, creators install it by git URL,
  and nothing else would ever move it; that is safe because the check is finished, and the session flag set,
  before the lock is touched, so the recompile that follows finds the flag and does nothing. **Opt-out:** the
  toggle **Update Virtuademy git packages when the project opens** at the foot of **Virtuademy ▸ Setup
  project**, an `EditorPrefs` key per project and per machine, on by default.
- **`SetupCli` arguments are strict** (behaviour change). Any `-vd...` argument that is not exactly
  `-vdSetupSteps`, `-vdSetupVersion` or `-vdSetupRegistry` (`--vdSetupSteps`, `-vdsetupsteps`, `/vdSetupSteps`),
  an option given twice, and a stray value (`-vdSetupSteps sdk update`) now exit `10` instead of being ignored,
  which used to run the default steps nobody asked for.

- **`SetupCli` has `resolve`, `update` and `vslibrary` steps**, so the whole setup
  can be driven from the command line.
  - **`resolve`** is part of the default steps, after `sdk` and before `interpreter`. For every
    Virtuademy git package in `packages-lock.json` it compares the pinned commit with what the
    manifest's ref points to on the remote now (`git ls-remote`, tags peeled), unpins the ones
    that moved and exits `2`; the next launch resolves them again and exits `0`. It does nothing
    when nothing moved. **All or nothing:** when the remote of any package cannot be reached (it is
    "unknown"), nothing is unpinned, not even the packages known to have moved, so a timeout cannot
    leave the project with a mix of new and old packages; that is a warning and exit `0`, the
    network being down never fails the step. The installer package itself is never touched.
  - The `[SetupCli] REPORT` has `stalePackages` (name, reference, locked and remote commit) and
    `stalePackagesChecked`; `-vdSetupSteps check` exits `3` when a package is stale, never because
    the network is down.
  - **`update`** (never a default step) is **Update packages to selected version** from the command
    line: `-vdSetupSteps update -vdSetupVersion <version>` moves a project that records another
    registry version to the target, with the window's own code: **what the target does not list is
    removed**, and HybridCLR follows the registry. It cannot be combined with `sdk`, which still
    refuses such a project (exit `15`, and the message now names the update step). When the move
    crosses an update routine of the new SDK (menu **Virtuademy ▸ Update routines**), the second
    launch logs a warning with its menu name and its command line
    (`-executeMethod Virtuademy.BreakingChangeSolvers.UpdateRoutinesCli.Run -vdRoutine 2026.6`): it must run
    next, newest first (2026.6 before 2026.5); SetupCli does not run it.
  - **`vslibrary`** (opt-in) calls the SDK's `VisualScriptingSetupEditor.Setup()`, the **Virtuademy
    ▸ Update VS Library** menu item, by reflection. It is not a default step: it took 73 to 80 s on
    a scratch project, every time, because it cannot tell it is already done. An SDK without the
    method is skipped with a warning.
  - New exit code `20`: the `vslibrary` step failed.
- The window's **Re-resolve packages from git** button, **Update packages to selected version** and
  the installed-packages upkeep moved into static methods (`PackageLock`,
  `CreatorKitSetupWindow.ApplyVersionUpdate`, `DropUnmanagedInstalledPackages`,
  `AddResolvedInstalledPackages`) that `SetupCli` calls too. The buttons do what they did.

- **`SetupCli` has an `addressables` step**. It is part of the default steps, after
  `interpreter`, and does what the **Fix Addressables configurations** button of the Addressables
  management window of Virtuademy-SDK-Environments does: it calls `AddressablesConfiguration.Apply()`
  by reflection once the SDK has compiled. A project that is not configured gets the SDK's profile
  and its Addressables groups **reset to the single default group** (every other group and every
  entry is removed); a configured one is left as it is. Before, a project set up entirely from the
  command line kept Unity's default profile and groups, and its first "Build & Deploy" failed
  after minutes of building.
  - The `[SetupCli] REPORT` has `addressablesConfigured` (`null` when the SDK has no
    `AddressablesConfiguration`), and `-vdSetupSteps check` exits `3` when it is `false`.
  - New exit code `19`: the step failed.
  - An SDK version without `AddressablesConfiguration` (every SDK-Environments before the `v10.0.0`
    tag moved on 2026-10-14) is skipped with a warning, so installing a published version still ends with `0`.

Everything since `v1.0.1`. Released with platform 2026.6.0, the first registry version whose
update routine moves a 1.x project to this installer's id.

### Breaking

- **The registry is read from `spacspublic/sdkpackagesregistry/`** (was
  `reflectis2023-public/PackageManager/`): `PackageRegistry.json` and
  `BreakingChangesSolverIndex.json`, same filenames, in `spacspublic` — the public container for
  generic tooling, which carries neither a brand nor a tenant. The old folder stays published, frozen after 2026.6, for the 1.x
  installers already in creators' projects.

- **Renamed** (2026-09-23) from `com.anotherealitysrl.reflectis-creatorkit-worlds-setup` /
  `Reflectis.CreatorKit.Worlds.Setup.Editor` to
  `com.anotherealitysrl.virtuademy-sdk-environments-setup` /
  `Virtuademy.SDK.Environments.Setup.Editor`, repository `Virtuademy-SDK-Environments-Setup`.
  Existing projects migrate with **Virtuademy ▸ Update routines ▸ v2026.5 -> v2026.6** in
  `Virtuademy-SDK-Environments`.
- The menu moved from **Reflectis Worlds ▸ Creator Kit ▸ Setup ▸ Setup project** to
  **Virtuademy ▸ Setup project** (the Setup submenu went away on 2026-10-07).
- New projects get their settings at `Assets/Virtuademy/Editor/Settings/SetupConfiguration.asset`
  (was `Assets/CreatorKit/Editor/Settings/CreatorKitSetupConfiguration.asset`); the asset is found
  by type, so existing ones keep working where they are.

### Added

- **Install interpreter**: installs HybridCLR and configures the project's hot-update assembly
  through `HotUpdateSetupper`; the reason the interpreter is not ready is shown in the window.
- **Re-resolve packages from git**: unpins the Virtuademy git packages in `packages-lock.json` so a
  moved branch is picked up.
- Registry entries can set `"prerelease": true`, and `version` can be any git ref (tag, branch,
  commit).
- The window ships its own icons, and a Virtuademy logo header (light or dark by editor skin).
- The window opens when the project is opened, once per editor session, if
  Virtuademy-SDK-Environments is not installed or a local check (git, editor modules, project
  settings) fails. A toggle at the foot of the window turns this off for the project on this
  machine.
- **Command line** (`SetupCli.Run`): Configure, the install of Virtuademy-SDK-Environments at a
  given registry version, and Install interpreter, in batch mode. One launch does one round and
  exits with `2` while another launch is needed, `0` when done, `1x` on a failure; `-vdSetupSteps
  check` only reports. Additive: the window behaves as before, and the files written have the same
  format as the window's. A version update (a project already on another version) stays in the
  window. `-vdSetupRegistry` reads a registry from another URL or a local file, to try a change
  before publishing it.
- **HybridCLR follows the Virtuademy version.** A registry entry declares the HybridCLR its release
  runs in `interpreter` (url, version tag), outside `packages`.
  - **Install interpreter** installs that one.
  - **Fix** switches a project that has another HybridCLR. It also reinstalls the interpreter in
    the local IL2CPP when Virtuademy-SDK-Environments reports another libil2cpp version
    (`GetInterpreterVersionIssue`, used only when the SDK has it).
  - **Update packages to selected version** moves HybridCLR with the version, or removes it when
    the target declares no interpreter, and lists it in the removed-packages dialog.
  - Older installers ignore the field.

### Fixed

- **Install interpreter no longer installs whatever HybridCLR released last.** It used to add
  HybridCLR's git URL without a ref. HybridCLR v9.0.0 (2026-10-08) renamed the namespaces the SDK
  compiles against, so every new install from that day failed to compile.
- **Only packages the registry lists are managed.** A package counted as the installer's when its
  id started with one of our prefixes (`virtuademy`, `reflectis`, `spacs`), so a package of ours
  that creators add by hand and no registry entry lists (`Virtuademy-Avatars`) was uninstalled by
  the next update as "not part of the selected version", and unpinned by Re-resolve. A package
  now needs the prefix **and** an id listed by at least one registry entry. Entries an earlier
  window saved on the prefix alone are dropped from the installed list, with a Console line, and
  are not uninstalled.
- The editor-modules check includes **iOS** build support. Interpreted scripts are compiled for
  iOS too, so without the module the setup looked complete and every build of a project with
  scripts stopped at "No DLL was produced for: iOS". Every project is now asked for it, scripts or not.
- The interpreter row names the folder interpreted scripts live in since
  Virtuademy-SDK-Environments moved them: `Assets/VirtuademyEnvironmentScripts` (was
  `Assets/HotUpdate`).
- Packages with the `virtuademy-*` and `spacs-*` prefixes are recognised as installed.
- A leaf package (no dependencies) can be installed; a dependency cycle or a typo in the registry
  no longer throws.
- A network failure while listing installed packages no longer corrupts the window's state.
- The git check reports a computed value, and explains a `PATH` problem in the Console instead of a
  modal dialog.
- The editor-modules check asks each target about its own build target group.
- Layout fixes in the project-settings list.
- A failed download of the version list is shown in the window, with a **Retry** button, instead of
  leaving it half-built; a failed download of the breaking-changes index is only a warning.
- Turning off *Show pre-releases* moves the selection off any prerelease entry, not only `develop`.
- Updating with automatic breaking-change resolution on no longer throws when no script is published
  for the version step.
- The window no longer comes back unbound — text placeholders and every warning icon showing —
  after an action that reloads the domain (install, update, configure).
- The interpreter is optional: its group is labelled so and warns only when the interpreter is
  installed but not ready to build. Its button reads **Fix** once HybridCLR is installed.
- A project whose recorded version is no longer in the registry keeps it (shown as *no longer
  available*) instead of being silently relabelled with the newest entry, so the update to a listed
  version is enabled and no longer throws. The update also follows a package whose repository URL
  changed, and lists removed packages in one dialog instead of one per package.

## v1.0.0

- First implementation.
