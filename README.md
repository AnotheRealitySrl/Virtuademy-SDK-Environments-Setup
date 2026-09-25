# Virtuademy-SDK-Environments-Setup

The Creator Kit installer. A creator adds **this** package by git URL, by hand, and everything else
arrives through it — so it is the one package whose own URL has to be typed.

[Documentation here](Documentation~/index.md)

## What it reads

`PackageRegistry.json`, published in the public blob container `reflectis2023-public`, folder
`PackageManager`. **The filename is load-bearing**: the installer looks for exactly that name, so
`PackageRegistry (1).json` is the same as not publishing.

That blob copy is the only copy — not versioned in any repository, hand-edited, no review, no
backup. Treat every edit as a production change: download the current file, add to it, re-upload.
Full release procedure in the meta-repo's `docs/deploy.md`.

## Registry entry format

```jsonc
[
  {
    "reflectisVersion": "2026.4.0",          // platform version, or a free label for a test entry
    "requiredUnityVersion": "6000.3.21f1",   // engine the creator must open the project with
    "prerelease": false,                     // true = hidden unless "show prereleases" is on
    "packages": [
      {
        "name": "com.anotherealitysrl.virtuademy-creatorkit",
        "displayName": "Virtuademy Creator Kit",
        "description": "…",
        "visibility": "Visible",             // Visible = offered in the UI, Hidden = pulled as a dependency
        "installationSource": "Git",         // Git or Submodule
        "url": "https://github.com/AnotheRealitySrl/Virtuademy-CreatorKit.git",
        "version": "v1.0.0"                  // ANY git ref — see below
      }
    ],
    "dependencies": {
      "com.anotherealitysrl.virtuademy-creatorkit": [ "com.anotherealitysrl.virtuademy-interface" ],
      "com.anotherealitysrl.virtuademy-interface":  [ "com.anotherealitysrl.spacs-utilities" ]
    }
  }
]
```

### `version` is a git ref, not only a tag

The installer writes `"{url}#{version}"` into `Packages/manifest.json`, and the `#` fragment of a
git URL is any ref git understands — **a tag, a branch, or a commit SHA**. So an entry can point a
tester at work in flight:

```jsonc
{ "version": "feature/creatorkit-restructure" }   // a branch
{ "version": "9564ed8" }                          // a commit
{ "version": "v8.1.0" }                           // a tag, the normal case for a release
```

Release entries should always pin **tags** — a branch moves, and a creator on a released version
must get the same bytes tomorrow.

### `prerelease` keeps a test entry away from creators

An entry marked `"prerelease": true` is left out of the version list unless the window's *show
prereleases* toggle is on. That is how a branch-pointing entry coexists with the release entries
without appearing to creators.

For backward compatibility an entry named exactly `develop` is treated as a prerelease even without
the flag — that behaviour used to be hardcoded in the window.

### `dependencies` declares direct edges only

Resolution is recursive, so a package does not repeat its grandchildren: if `creatorkit` needs
`interface` and `interface` needs `spacs-utilities`, declaring the first two edges is enough.
Fewer entries in a hand-edited file means less to get wrong.

Every package in `packages` may appear as a key, including one with no dependencies of its own —
that case is handled and no longer needs an empty array to avoid an exception.

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
  because the key no longer matches the name in `package.json` (not yet observed on a real project). Such a project must run
  `Virtuademy ▸ Update routines ▸ v2026.5 -> v2026.6` (shipped in `Virtuademy-SDK-Environments`)
  first: it rewrites the key and the URL and unpins this project's own git packages in
  `packages-lock.json`, so UPM resolves them again. The old id stays in the
  self-exclusion list of `CreatorKitSetupWindow` for projects that have not migrated yet.
