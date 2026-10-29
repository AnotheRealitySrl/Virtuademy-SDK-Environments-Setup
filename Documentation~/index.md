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
the project settings below are not configured, the Addressables are not configured, or TextMesh Pro's Essential
Resources are missing. The Console says which. A project that has nothing to fix opens the Readme instead (see below), and if
you close a window, it stays closed until the editor is restarted. You can always open the setup window from **Virtuademy ▸
Setup project**. A project that is not set up yet opens its introduction; one that is set up opens its status.

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

The window has six pages, one at a time, with **Back** and the main action in a bar at the bottom. A project that is not set up
starts at the first, a project that is set up at the last.

| Page | What it does |
|---|---|
| **Introduction** | What the setup does in three steps, and a reminder to back up or commit the project first, because the setup changes project settings. Tick **Don't show this introduction again** to skip it (saved for this project and this user). **Get started** goes on. |
| **Connection** | Connect your organization, which you need to publish. Your login is kept for all your projects on this computer. Choose the Back Office (`bo.virtuademy.io`, or **Another Back Office...** with its address) and press **Open the Back Office**: the configuration file is downloaded from a world's page with **Download SDK Environments Config**. The page lists the connections of this computer, each with its organization, environment, platform version and account, and **Log in** or **Log out**; a warning says when a connection's app is not one environments can be published to. **Add an organization** imports the file and logs in. On a project that does not have the SDK yet, **Choose the file...** and **Log in** first offer to install the part of the SDK that holds the connections, which takes about a minute; then the file picker, or the login, opens by itself. A connection shows its organization alone; a sandbox or prep one carries a coloured chip (Sandbox, Prep). Connections need Virtuademy 2026.7 or later: while only the development version has them for your Unity, the page says so, and adding an organization or logging in needs **Development mode** (see below). This page is optional: **Connect later** goes on without it, and nothing after it needs a connection. To remove a connection, use **Manage in Publish Environments**. |
| **Choices** | **Virtuademy version**: the one your organization runs is the default and is marked *recommended* (when your connections agree on one and the list has it), else the latest release; the development version shows only in **Development mode**. Under it, which Unity the version needs: it blocks the start when the open editor is another. For a project that has the SDK, a note says whether this is an upgrade or a downgrade; a downgrade needs the tick **I committed or backed up the project**. **Scripting**: *Visual Scripting only*, or *Visual Scripting and C# scripts*, which also installs the interpreter (about 5 minutes more, needs git). On a project that has the interpreter, *Visual Scripting only* removes it, and the page says so in orange. **Next** opens the summary. |
| **Summary** | "Ready to set up": your choices (the version, and whether it is an update, a downgrade or already installed; the scripting), what the setup will do, and, when there is any, what cannot be undone from the window (see *Before a run starts* below). **This computer** (git and the editor build modules) shows only when something is missing, and blocks the start until **Check again** finds it. **Start setup** begins. In development mode the page also shows each step's detail, what is already done or not needed, the computer's checks that pass, and the Unity the version needs. |
| **Run** | "Setting up your project": the step you are at, the percentage, a progress bar and the list of steps with what each did. Unity reloads a few times: keep the editor open. Before the two long steps that block the editor (the interpreter and the Visual Scripting nodes) it says Unity may not respond for a minute or two. **Stop after this step** ends the run when the step in progress is done. A run that ends well goes to the status; one that fails stays, with the reason on the step, and **Try again** repeats it: it only does what is left. |
| **Project status** | The result of a run, and the home of a project that is set up: "Your project is ready", or how many things need attention. A card shows, for each, the name in bold and the value beside it: **Virtuademy version** (**Change version** opens the Choices page), **Packages** (**Reload packages** asks the remote of each Virtuademy git package whether its commit moved, compares commits and not versions, reloads the ones that did, and says what it did, or *All up to date*), **Scripting** (**Add C# scripts** or **Remove C# scripts**: both open the summary at the installed version, which says what the change does and, when C# scripts would stop running, asks for the tick first), **Connections** (**Manage** opens the Connection page). Under it, the checks that fail, under *Needs attention*, each with **Fix**; development mode shows all of them, under *Checks*: this computer, project settings (URP, API compatibility, Active Input Handling, max texture size), Addressables (a single default group: **Fix** removes every other group and every entry, and asks first), TextMesh Pro's Essential Resources, the interpreter when installed. When Active Input Handling was changed, **Restart now** restarts the editor once to turn it on; it stays on the page until the editor has restarted, whatever ran in between (another run, a **Fix**), and the project is only "ready" after it. **Run setup again** opens the summary of the whole setup at the installed version, and starts it from there. |

**Development mode (internal testing)**, at the foot of the window, is one switch for the whole setup. It shows the development version
of Virtuademy, which points at work in progress, in the lists of the setup, and the details of the summary. It is the same setting as
**Show pre-releases** on the full page, it is saved in the project's setup configuration, and installing the development version turns
it on by itself.

**Virtuademy ▸ Advanced ▸ All setup checks** opens the window on the full page instead, every check and every action on one page, as
described below: it is for those who want to drive each step by hand, and nothing the wizard does is missing from it. **Back to the
guided setup**, at its top, returns to the wizard, and so does **Virtuademy ▸ Setup project**. It is not available while a setup run is
going (the buttons of the full page would start work beside the run).

### The setup run

Behind the wizard there is one engine that does every step of the setup by itself, in this order, and skips
the ones that have nothing to do (the Console and the step's detail say why): **project settings**, the **Virtuademy
packages** at the version you chose (installed, updated or changed; a version older than the installed one is
refused until you confirm the downgrade, and so is one that needs another Unity), **compile** (it waits until
Unity has compiled, and stops with a pointer to the Console when the scripts do not), the **update routines** when the
new version has one for the step you moved across, the **interpreter** (installed when you write C# scripts, removed when you chose
*Visual Scripting only* on a project that has it), the **Addressables**
configuration, the **Visual Scripting nodes**, TextMesh Pro's **Essential Resources** when they are missing, a look at
the **connections** of this computer (it only reads them; it never fails the run), and last the **editor restart**,
when Active Input Handling was changed. The restart is never automatic: you press **Restart now**. It is the very last step: it comes after every package install and
recompilation, and after it the status page shows no check in red. Whether it is due belongs to the editor session, not to a run: a change
of Active Input Handling made by any run (or by the full page) keeps **Restart now** on the status page until the editor has restarted.

#### Before a run starts

The summary is the confirmation: before **Start setup**, it lists under *Cannot be undone from here* what this run will really do of
these, and nothing when there is none:

- **Move Virtuademy from one version to another** (a change or a downgrade): the Virtuademy packages that the new version does not list are
  removed from the project.
- **Run the update routines** of the new version: they rename and rewrite scripts, graphs and assets of the project, and close the open
  scenes. If a scene has changes that are not saved, a second dialog, just before the routines, lists those scenes and offers three
  choices: **Save and continue**; **Continue without saving**, which loses those changes for good and reopens the scenes as they are on
  disk (an untitled scene is closed); **Stop the setup**, which ends the run before the routines change anything.
- **Remove the interpreter for C# scripts**, when you chose *Visual Scripting only* on a project that has it. The C# scripts in
  `Assets/VirtuademyEnvironmentScripts` stay in the project but no longer run in Virtuademy. When the project has some, the summary also
  shows a red box, *Your C# scripts will stop working*, with the number of scripts, and **Start setup** waits for the tick **I understand
  that my C# scripts will no longer run** (asked again each time the summary opens). Choose *Visual Scripting and C# scripts* to keep
  them.
- **Reset the Addressables**: every group other than the SDK's single default group, and every entry in the groups, is removed. This only
  happens when the project's Addressables are not configured as the SDK expects; a configured project is left as it is.

**Try again** repeats what was confirmed, and each **Fix** that resets something asks for itself.
The Visual Scripting nodes step does not need a confirmation: it only adds the SDK's types to the node library, and keeps the ones you added by
hand (it does nothing when the library has the SDK's types already). A project that has Virtuademy packages from before 2026.6 (other package ids, no
`com.anotherealitysrl.virtuademy-sdk-environments`) is not touched at all: the **Virtuademy SDK packages** step fails up front, before any step runs, and points
to *Updating from an older platform version* below.

The run survives the recompilations and domain reloads that installing packages causes: it carries on by itself and
keeps its state in `UserSettings/Virtuademy/setup-run.json`. If the editor is closed in the middle of a step, the next
session shows that step as failed; run the setup again, it only does what is left. The Visual Scripting nodes step
takes over a minute and the interpreter step can take a few minutes, and Unity does not respond while they run.

The command line (below) runs this same engine with `-vdSetupSteps run`, and its single steps call the same code for each step. Only a
few messages differ: they say what to do next, and the single steps' talk about launching again and the `-vdSetup...` arguments, the
run's about the wizard.

**What is kept per project and user** (`UserSettings/Virtuademy/`, not versioned): `setup-run.json` is the last run, and is ignored when
its `Format` is not one this version of the installer writes. `setup.json` holds the choices of the pages: `hideReadmeOnStartup`,
`hideIntroduction`, `backOfficeUrl`, `pendingConnection` (a login, or the choice of a configuration file, that waits for the SDK's
connection support to be installed, which expires after 30 minutes) and `preparedPackages`: the packages the Connection page installed
(SDK-Core and what it needs) before the SDK itself. The run does not take them for a project that is at another version, and clears the list once it has
installed the SDK. The Connection page saves its pending action only after it has written `Packages/manifest.json`, and gives up with a
message when Unity's Package Manager does not resolve SDK-Core after a minute.

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
  switches back to the installed version. It is the wizard's **Development mode**: turning it off
  also goes back to the wizard.
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

The setup also runs without the window, for a build machine, a fresh clone or an assistant. Close the editor on the project
first.

**The whole setup in one launch.** `-vdSetupSteps run` does what **Start setup** does, with the same engine and the same steps
(see *The setup run* above):

```
Unity -batchmode -nographics -projectPath <your project> -logFile - -ignoreCompilerErrors
      -executeMethod Virtuademy.SDK.Environments.Setup.Editor.SetupCli.Run
      -vdSetupSteps run -vdSetupVersion <version> -vdSetupScripting vs
```

- `<version>` is a version as the wizard's list shows it, for example `2026.6.0`, or `develop` for the development version.
- `-vdSetupScripting` is the wizard's *Scripting* choice, and is required: `vs` for Visual Scripting only, `csharp` for Visual
  Scripting and C# scripts (it installs the interpreter). `vs` on a project that has the interpreter removes it.
- The confirmations of the summary are options. Without the one a run needs, nothing is changed and the command exits with `21`:
  - `-vdAllowDowngrade`: go back to a version older than the installed one;
  - `-vdAllowScriptRemoval`: remove the interpreter from a project that has C# scripts in `Assets/VirtuademyEnvironmentScripts`
    (they stay, and no longer run in Virtuademy).
- The editor stays open across the reloads of the run and exits by itself when the run ends, so one launch is enough. Do not add
  `-quit`: it would close the editor before the run ends, and the command refuses it. Keep `-ignoreCompilerErrors`: after an update,
  the project's scripts may not compile until the update routines have run.
- The log ends with a `[SetupCli] RUN` line (each step with its state and what it did, the step that failed, and `restartPending`
  when Active Input Handling changed: it applies from the next launch of the editor), the `[SetupCli] REPORT` line and the exit code:
  `0` when every step is done or not needed, `23` when a step failed (the `RUN` line names it and says why).

`-vdSetupSteps plan`, with the same options, changes nothing: it logs what the run would do as one `[SetupCli] PLAN` line (each step
with its state and detail, what cannot be undone, the confirmations missing as `confirmationsNeeded`, and `startable`) and exits with
the code the run would stop with, `0` when it would start.

**One step at a time.** Without `run`, the command runs steps one round per launch.
**Configure**, **Install** of Virtuademy SDK Environments, **Install interpreter** and a smarter
**Re-resolve packages from git** run by default, and so does the Addressables configuration that the SDK's Addressables management window
applies with **Fix Addressables configurations**:

```
Unity -batchmode -nographics -projectPath <your project> -logFile -
      -executeMethod Virtuademy.SDK.Environments.Setup.Editor.SetupCli.Run
      -vdSetupVersion <version>
```

The command has to be run again until it exits with `0`. Exit code `2` means "the
project changed, run again", and a new project takes three runs. The last run also configures
Addressables: it gives the project the SDK's Addressables profile and resets the Addressables
groups to the single default group, removing every other group and every entry, as the window's
button does. A project that is already configured is left as it is. Every run also re-resolves the
Virtuademy git packages whose tag or branch has moved since the project pinned them (a new run is
needed for that, so it exits with `2` too), and does nothing when none moved. Any other code is an
error, named in the log. On a fresh clone of a project that is already set up, the same command skips the
packages, which arrive with the clone. It redoes the two things stored per machine: the max
texture size override and the interpreter inside the local IL2CPP. With `-vdSetupScripting vs`
the interpreter step removes the interpreter instead (with `-vdAllowScriptRemoval` when the project
has C# scripts). `-vdSetupSteps check` changes nothing and exits with `0` only when the project is
set up, counting what the status page counts, TextMesh Pro's Essential Resources included.

Three things run only when you ask for them, with `-vdSetupSteps`:

- **Update packages to selected version**, as `-vdSetupSteps update -vdSetupVersion <version>`.
  It does what the button does: it installs what the new version adds, **removes what it does not
  list**, and moves HybridCLR with the version. Going back to an older version needs
  `-vdAllowDowngrade`. The first run changes the project and exits with
  `2`, the second one finishes and exits with `0`. When the move crosses a version that has an
  update routine, the log says so and names it, for example **Virtuademy ▸ Update routines ▸
  v2026.5 -> v2026.6**: run it next, from the menu, after backing up the project. This step does
  not run it; `run` does.
- **Update Visual Scripting library** (**Virtuademy ▸ Advanced ▸ Update Visual Scripting library**; **Virtuademy ▸ Update VS Library** before 2026.7), as `-vdSetupSteps vslibrary`, once
  Virtuademy SDK Environments is installed. It adds the Virtuademy nodes to the Visual Scripting
  node library, and does nothing when the library has them already. It takes more than a minute
  when it works, which is why it is not part of the default steps.
- **TextMesh Pro's Essential Resources**, as `-vdSetupSteps tmp`, when they are missing. The import
  ends after the launch's method returns, so the editor stays open until it is done: do not add `-quit`.

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
- `UserSettings/Virtuademy/setup-run.json` — the state of the setup run, for this user and this project: the
  choices, each step's result, and where a run that Unity interrupted has to go on. `UserSettings` is not
  versioned.
- `UserSettings/Virtuademy/setup.json` — your choices in this project: whether the Readme and the introduction show by
  themselves, the Back Office the Connection page opens, and, while the SDK's connection support installs, the login or import
  that waits for it. Not versioned.
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
