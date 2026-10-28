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
- **Editor modules**: Android, iOS, WebGL and Windows build support, installed from Unity Hub.

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

When you open the project, the setup window opens by itself **only if something needs doing**:
Virtuademy-SDK-Environments is not installed yet, git cannot be run, an editor module is missing,
or the project settings below are not configured. The Console says which. A project that is set up
opens the Readme instead (see below), and if you close a window, it stays closed until the editor is restarted. You can
always open the setup window from **Virtuademy ▸ Setup project**.

To stop it opening by itself, clear **Open this window when the project opens and needs setup** at
the foot of the window. The choice is saved for this project on this machine.

The startup check does not cover the Unity version or the interpreter: open the window to see
those.

**Packages update by themselves.** Once per editor session, when the project opens (never in a batch run),
the editor checks the remote of each Virtuademy git package, and of this installer, in the background. If
every remote answers and some moved (a tag was re-cut, a branch has new work), exactly those are updated and
the Console says which, from which commit to which. If any remote cannot be reached, nothing is changed and one
warning says so. To turn it off, clear **Update Virtuademy git packages when the project opens** at the foot
of the window; the choice is saved for this project on this machine.

### The wizard

The window shows one step at a time, with the steps listed under the logo, and **Back** and **Next**
at the bottom. **Next** is enabled when the checks of the step are green; **Check again** reads the
computer and the project again (use it after installing git or a module). The window opens on the first
step that fails.

| Step | What it shows |
|---|---|
| **1. Computer** | The Git installation and the Editor configuration groups below: git, the Unity version, the editor modules. Next needs git and the modules; a Unity version other than the one required shows as a warning that names it, and does not stop you. |
| **2. Project** | The Project settings group below: URP, the API compatibility level, Active Input Handling, the max texture size. **Configure** applies them, and offers to restart the editor when it changes the input handling. |
| **3. SDK** | Choose the version: **Latest release** (the default), **A previous release** (a list, newest first) or, when **Show the development version** is ticked, the development version (unreleased, for internal testing). One button does it, and says what it will do: **Install**, **Update to** or **Change to** the version; it reads **Installed** when that version is already in. It is disabled, with the reason, when the version needs another Unity than the open editor. After an update, run the update routine the release notes name, from **Virtuademy ▸ Update routines**. Next is enabled once the SDK is installed at the chosen version **and the project compiles**; if it does not, the step says so and **Open the Console** shows the errors. The Package manager section below holds the same install and update, per package, on the full page. |
| **4. C# scripts** | The interpreter's state and its button: **Install interpreter**, **Fix the interpreter**, or **Ready**. It says why it cannot run yet when the SDK is missing or the project does not compile. Optional: **Skip** if you only use Visual Scripting. |
| **5. Connection** | Opens the SDK's own window to import the configuration file you downloaded from the Back Office and to log in: **Publish Environments** from SDK 2026.7.0, **Available Tenants** or **Addressables management** in earlier versions. The setup cannot see whether you logged in, so this step never blocks you; tick the box when you have. |
| **6. Done** | A summary of the steps, and buttons for the Readme, the documentation and the publish window. |

**Show all checks** at the foot of the window shows everything on one page instead, as described
below. Nothing in the wizard is missing from that page.

### The Readme

When the project opens and nothing needs setup, the **Virtuademy Readme** opens instead of the setup
window: what the SDK is, the first steps and the menu entries. Open it at any time from
**Virtuademy ▸ Readme**. Tick **Do not show this when the project opens** to stop it: the choice is
saved for this project and this user, in `UserSettings/Virtuademy/setup.json`, which is not versioned.

### The full page

The page has two sections, **Project settings** and
**Package manager**. Work through them top to bottom: the package installs need git, and the
packages expect the project settings below.

A row with a green icon is fine; a red icon means that check failed, and a yellow warning on a
collapsed group means something inside it needs attention. The interpreter group is optional: it
shows the warning only when the interpreter is installed but not ready to build.

### Project settings

| Group | What is checked | How to fix it |
|---|---|---|
| **Git installation** | `git --version` runs from the editor | **Download** opens the git website. If git works in a terminal but the row is red, the editor was started without git on its `PATH` (common when launched from Unity Hub): restart the editor from a shell where `git` works. The Console says which case it is. |
| **Editor configuration** | The editor version equals the one required by the installed platform version, character for character (e.g. `6000.3.21f1`); Android, iOS, WebGL and Windows build support are installed | Install the right editor and the missing modules from Unity Hub. |
| **Project settings** | URP is the render pipeline (default and quality); API compatibility level is .NET Framework 4.8 for Standalone; Active Input Handling (Player Settings) is **Both**, as in the Virtuademy player, which reads both the Input Manager and the Input System package; the build's max texture size override is 1024 | **Configure** applies all four. It overwrites those project settings — make a backup or commit first. When it changes the input handling it offers to restart the editor: Unity applies that setting only when the editor starts. |
| **Interpreter installation (optional)** | The HybridCLR interpreter package is installed at the version your Virtuademy version uses, and the project's hot-update assembly is set up | **Install interpreter** installs the HybridCLR of your Virtuademy version and configures the hot-update assembly in one go, once Unity has recompiled. When HybridCLR is already installed the button reads **Fix**: it switches HybridCLR to your version's when the project has another one, reinstalls the interpreter when the one on this computer belongs to another HybridCLR version, and runs the configuration again. It clones two repositories and patches a project-local copy of il2cpp, so it takes a while. When the assembly is not ready, the reason is printed under the two rows. Versions before 2026.6 have no interpreter. |

The interpreter is what runs the C# you write under `Assets/VirtuademyEnvironmentScripts`. It is **optional**: a project
that only uses Visual Scripting does not need it, and nothing else in the window depends on it. Its setup is owned by the
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

The update moves HybridCLR too, when the project has it: to the version the new Virtuademy version
uses, and out of the project when the new version has no interpreter. The interpreter on this
computer is reinstalled for the new HybridCLR after Unity recompiles.

### From the command line

**Configure**, **Install** of Virtuademy SDK Environments, **Install interpreter** and a smarter
**Re-resolve packages from git** also run without the window, for a build machine or a fresh
clone, and so does the Addressables configuration that the SDK's Addressables management window
applies with **Fix Addressables configurations**. Close the editor first, then run:

```
Unity -batchmode -nographics -projectPath <your project> -logFile -
      -executeMethod Virtuademy.SDK.Environments.Setup.Editor.SetupCli.Run
      -vdSetupVersion <version>
```

`<version>` is a version as the window's list shows it, for example `2026.6.0`, or `develop` for
the pre-release. The command has to be run again until it exits with `0`. Exit code `2` means "the
project changed, run again", and a new project takes three runs. The last run also configures
Addressables: it gives the project the SDK's Addressables profile and resets the Addressables
groups to the single default group, removing every other group and every entry, as the window's
button does. A project that is already configured is left as it is. Every run also re-resolves the
Virtuademy git packages whose tag or branch has moved since the project pinned them (a new run is
needed for that, so it exits with `2` too), and does nothing when none moved. Any other code is an
error, named in the log. On a fresh clone of a project that is already set up, the same command skips the
packages, which arrive with the clone. It redoes the two things stored per machine: the max
texture size override and the interpreter inside the local IL2CPP. `-vdSetupSteps check` changes
nothing and exits with `0` only when the project is set up.

Two things run only when you ask for them, with `-vdSetupSteps`:

- **Update packages to selected version**, as `-vdSetupSteps update -vdSetupVersion <version>`.
  It does what the button does: it installs what the new version adds, **removes what it does not
  list**, and moves HybridCLR with the version. The first run changes the project and exits with
  `2`, the second one finishes and exits with `0`. When the move crosses a version that has an
  update routine, the log says so and names it, for example **Virtuademy ▸ Update routines ▸
  v2026.5 -> v2026.6**: run it next, from the menu, after backing up the project. The command
  line does not run it.
- **Update VS Library** (**Virtuademy ▸ Update VS Library**), as `-vdSetupSteps vslibrary`, once
  Virtuademy SDK Environments is installed. It adds the Virtuademy nodes to the Visual Scripting
  node library. It takes more than a minute and cannot tell that it has already run, which is why
  it is not part of the default steps.

## What the installer writes to your project

- `Packages/manifest.json` — one `"<package id>": "<git url>#<ref>"` line per installed package.
- `Packages/packages-lock.json` — only when you press **Re-resolve packages from git** (it drops
  the pins of the Virtuademy git packages), or from the command line when a package's tag or
  branch has moved (it drops the pins of those packages only).
- `Assets/Virtuademy/Editor/Settings/SetupConfiguration.asset` — the installer's own state: the
  current platform version, the packages it installed, the two advanced toggles. Keep it under
  version control with the rest of the project so a colleague opening the project sees the same
  state. The window finds it by type anywhere in `Assets`, so an older project that still has it
  under `Assets/CreatorKit/Editor/Settings/` keeps working.
- `Assets/Virtuademy/Editor/Scripts/` — the downloaded breaking-change scripts, only when automatic
  resolution is on.
- Project settings, only when you press **Configure** or **Install interpreter**, or run them from
  the command line.
- `Assets/AddressableAssetsData/`, only from the command line (the `addressables` step): the
  Addressables settings, profile and default group. The window of Virtuademy-SDK-Environments
  writes the same files when you press **Fix Addressables configurations**.

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
