# Changelog

All notable changes to `com.anotherealitysrl.virtuademy-sdk-environments-setup`.

## v2.0.0 — on `develop`, not released yet

Everything since `v1.0.1`. `main` and the latest tag stay on the 1.x installer until the 2026.6
platform version is published (see the README's Known issues).

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
  **Virtuademy ▸ Setup ▸ Setup project**.
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

### Fixed

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
