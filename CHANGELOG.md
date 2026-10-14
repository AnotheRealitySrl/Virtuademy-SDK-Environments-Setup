# Changelog

All notable changes to `com.anotherealitysrl.virtuademy-sdk-environments-setup`.

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
