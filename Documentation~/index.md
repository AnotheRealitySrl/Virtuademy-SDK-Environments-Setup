# Virtuademy-SDK-Environments-Setup

The installer for the Virtuademy SDK. You add this one package to your Unity project by hand; it
checks that the project is set up correctly and installs every other Virtuademy package for the
platform version you pick.

Package id: `com.anotherealitysrl.virtuademy-sdk-environments-setup` · Unity `6000.3` or later ·
editor-only (it adds nothing to a build).

## Before you start

- **Git** must be installed and on the `PATH` of the Unity editor — every Virtuademy package is
  fetched from a git repository. Download it from <https://git-scm.com/downloads>.
- **The Unity version** required by the platform version you want. The setup window tells you
  which one it expects and marks the row red when the open editor does not match.
- **Editor modules**: Android, WebGL and Windows build support, installed from Unity Hub.

## How to install

Pick one of the two ways.

### From the Package Manager (recommended)

1. Open **Window ▸ Package Manager**.
2. Click **+ ▸ Install package from git URL…**.
3. Paste `https://github.com/AnotheRealitySrl/Virtuademy-SDK-Environments-Setup.git` and click
   **Install**.

### As a git submodule

If your project is itself a git repository and you want the installer's sources in it:

1. In your git client, add a submodule with source
   `https://github.com/AnotheRealitySrl/Virtuademy-SDK-Environments-Setup.git`.
2. Use `Packages/Virtuademy-SDK-Environments-Setup` as the destination path.

Unity picks up the folder as an embedded package on the next refresh.

## How to use

When you open the project, the window opens by itself **only if something needs doing**:
Virtuademy-SDK-Environments is not installed yet, git cannot be run, an editor module is missing,
or the project settings below are not configured. The Console says which. A project that is set up
opens without it, and if you close it, it stays closed until the editor is restarted. You can
always open it from **Virtuademy ▸ Setup ▸ Setup project**.

To stop it opening by itself, clear **Open this window when the project opens and needs setup** at
the foot of the window. The choice is saved for this project on this machine.

The startup check does not cover the Unity version or the interpreter: open the window to see
those.

The window has two sections, **Project settings** and
**Package manager**. Work through them top to bottom: the package installs need git, and the
packages expect the project settings below.

A row with a green icon is fine; a red icon means that check failed, and a yellow warning on a
collapsed group means something inside it needs attention.

### Project settings

| Group | What is checked | How to fix it |
|---|---|---|
| **Git installation** | `git --version` runs from the editor | **Download** opens the git website. If git works in a terminal but the row is red, the editor was started without git on its `PATH` (common when launched from Unity Hub): restart the editor from a shell where `git` works. The Console says which case it is. |
| **Editor configuration** | The editor version equals the one required by the installed platform version, character for character (e.g. `6000.3.21f1`); Android, WebGL and Windows build support are installed | Install the right editor and the missing modules from Unity Hub. |
| **Project settings** | URP is the render pipeline (default and quality); API compatibility level is .NET Framework 4.8 for Standalone; the build's max texture size override is 1024 | **Configure** applies all three. It overwrites those project settings — make a backup or commit first. |
| **Interpreter installation** | The HybridCLR interpreter package is installed, and the project's hot-update assembly is set up | **Install interpreter** installs HybridCLR, then configures the hot-update assembly once Unity has recompiled. It clones two repositories and patches a project-local copy of il2cpp, so it takes a while. When the assembly is not ready, the reason is printed under the two rows. |

The interpreter is what runs the C# you write under `Assets/HotUpdate`. Its setup is owned by the
`Virtuademy-SDK-Environments` package, so the second row can only turn green once that package is
installed (see below). If the button reports that HybridCLR is installed but the setup could not
compile, fix the compilation errors shown in the Console and press it again.

### Package manager

- **Current Virtuademy version** — the platform version the installed packages belong to. If it
  says *no longer available*, that version was withdrawn: select a listed one and update.
- **Select another version** — shows the packages of another platform version. The list under it
  shows the packages you can install for the version selected; expand one to see its description,
  repository and dependencies.
- **Install / Uninstall** on a package — installs it together with all its dependencies, or
  removes it together with the dependencies nothing else needs. The button is disabled when the
  package was pulled in as a dependency of another one, when it is embedded in the project
  (shown as **Embedded**: manage it with git instead), and while the selected version differs from
  the current one — update first.
- **Update packages to selected version** — moves the whole project to the selected version: every
  package of that version is installed or moved to its new version, and packages the version no
  longer has are removed (a dialog names each removed package). Updating to an **older** version
  may break the project; back it up first.
- **Refresh** (the icon next to *Last refresh*) — reloads the list of versions and re-reads what is
  installed.

**Advanced settings**

- **Show pre-releases** — adds test versions to the list. They point at work in progress and can
  change under you; use them only when asked to. Turning it off while a test version is selected
  switches back to the installed version.
- **Resolve breaking changes automatically when updating version** — after an update, downloads
  the conversion script for that version step into `Assets/Virtuademy/Editor/Scripts/`. The script
  adds a menu entry; run it from there once Unity has recompiled. It only covers a step of one
  minor version, it modifies your assets (back up first), and the only step published is
  `2025.3 → 2025.4` — for any other step the Console says that no script exists and nothing is
  downloaded. Newer steps use **Virtuademy ▸ Update routines** instead (see below).
- **Re-resolve packages from git** — re-downloads every Virtuademy package at the branch or tag the
  project asks for. Use it when you are on a pre-release and new work has been pushed to its branch:
  Unity pins each git package to the commit it first resolved, so a plain refresh does not pick it
  up. It is slow, because each package is cloned again.

### Updating from an older platform version

Some version steps need more than new packages — renamed scripts, moved assets. Those are handled
by the update routines under **Virtuademy ▸ Update routines** (shipped with
`Virtuademy-SDK-Environments`). Run the routine for your step when the release notes say so.

## What the installer writes to your project

- `Packages/manifest.json` — one `"<package id>": "<git url>#<ref>"` line per installed package.
- `Packages/packages-lock.json` — only when you press **Re-resolve packages from git** (it drops
  the pins of the Virtuademy git packages).
- `Assets/Virtuademy/Editor/Settings/SetupConfiguration.asset` — the installer's own state: the
  current platform version, the packages it installed, the two advanced toggles. Keep it under
  version control with the rest of the project so a colleague opening the project sees the same
  state. The window finds it by type anywhere in `Assets`, so an older project that still has it
  under `Assets/CreatorKit/Editor/Settings/` keeps working.
- `Assets/Virtuademy/Editor/Scripts/` — the downloaded breaking-change scripts, only when automatic
  resolution is on.
- Project settings, only when you press **Configure** or **Install interpreter**.

## Troubleshooting

- **"Could not download the list of Virtuademy versions"**. The window needs the internet to read
  the list of versions; the message says what went wrong (no network, a proxy, a firewall). Fix the
  connection and press **Retry**.
- **The Git row is red but git is installed.** See the Git row in the table above: the editor's
  `PATH` is the one that counts.
- **A pre-release does not pick up new work.** Press **Re-resolve packages from git**.
- **Packages disappear after an update.** They are not part of the selected version — the dialog
  named them. Select the previous version and update back if you need them.

## Known issues

- **Projects installed before 2026-09-23 still reference the old package id**
  (`com.anotherealitysrl.reflectis-creatorkit-worlds-setup`). Run
  **Virtuademy ▸ Update routines ▸ v2026.5 -> v2026.6** first: it rewrites the id and the URL in
  `Packages/manifest.json`.
