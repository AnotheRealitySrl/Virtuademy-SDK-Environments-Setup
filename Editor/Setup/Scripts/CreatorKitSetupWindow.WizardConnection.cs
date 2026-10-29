using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

using UnityEngine;
using UnityEngine.UIElements;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// The Connection page: the Virtuademy Back Office, where the configuration file is downloaded (an organization with a Back Office of
    /// its own downloads it from there, which the page says rather than asks for its address), the connections this computer already holds
    /// (one row each, with Log in and Log out), and "Add an organization" (choose the file, import it, log in). It is optional at
    /// every point: <b>Connect later</b> is always there, and nothing after it needs a connection.
    ///
    /// The connections are the SDK's, kept in one store per user and shared by every project on the computer. This package installs
    /// the SDK and cannot reference it, so <see cref="SetupConnections"/> reaches the SDK's <c>SetupBridge</c> by name and only once
    /// SDK-Core is installed. When it is not (a project that has no SDK yet, which is when the page is first seen), the rows come
    /// from the SDK's status file, and the first Log in or Choose the file asks to install just SDK-Core, then goes on by itself
    /// after the domain reload: the login opens, or the file is asked for then, so it is chosen once. The action waits in
    /// <see cref="SetupUserSettings.Pending"/>.
    /// </summary>
    public partial class CreatorKitSetupWindow
    {
        private const string back_office_url = "https://bo.virtuademy.io";

        internal const string sdk_core_package_name = "com.anotherealitysrl.virtuademy-sdk-core";

        // The first platform version whose SDK-Core has the setup bridge.
        private const string first_connections_version = "2026.7.0";

        private const double connections_refresh_seconds = 3;
        private static readonly TimeSpan pending_connection_lifetime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan pending_connection_load_wait = TimeSpan.FromSeconds(60);

        // Serialized, like the page: installing SDK-Core ends in a domain reload.
        [SerializeField] private bool connectionFromStatus;
        [SerializeField] private string connectionNote;

        private VisualElement connectionList;
        private Label connectionSupportLabel;
        private Button connectionManageLink;
        private Button connectionAddButton;
        private Label connectionNoteLabel;

        private List<SetupConnection> connections = new();
        private bool connectionsFromBridge;
        private string connectionsError;
        private double connectionsReadAt = -100;
        [NonSerialized] private string connectionsSignature;
        private readonly HashSet<string> busyConnections = new();

        // Asked once the wait for SDK-Core is over: the answer comes after the Package Manager has finished what it was resolving.
        private ListRequest packageIdleProbe;
        private bool importingFile;
        private bool resumingConnection;
        private SetupUserSettings.PendingConnection pendingConnection;

        private enum ConnectionSupport { Ready, CanPrepare, Waiting, Unavailable }

        #region Building

        private VisualElement BuildConnectionPage()
        {
            VisualElement page = NewPage("wizard-connection");
            page.Add(MakeLabel("Connect your organization", "wizard-title"));
            page.Add(MakeLabel("You need it to publish. Your login is kept for all your projects on this computer.", "wizard-text"));

            // The Back Office is only a link to open in the browser: the Virtuademy one, and a note for the organizations that have their own.
            page.Add(MakeLabel("Back Office", "wizard-heading"));
            VisualElement buttons = new();
            buttons.AddToClassList("wizard-buttons");
            Button backOfficeOpenButton = MakeButton("Open the Virtuademy Back Office", () => Application.OpenURL(back_office_url), "wizard-button");
            backOfficeOpenButton.tooltip = back_office_url;
            buttons.Add(backOfficeOpenButton);
            page.Add(buttons);
            page.Add(MakeLabel("The file is downloaded from a world's page: open the world and choose \"Download SDK Environments Config\". " +
                               "If your organization has a Back Office of its own, download the file from there, the same way.", "wizard-note"));

            page.Add(MakeLabel("Connections on this computer", "wizard-heading"));
            connectionSupportLabel = MakeLabel(string.Empty, "wizard-note");
            page.Add(connectionSupportLabel);
            connectionList = new VisualElement();
            page.Add(connectionList);

            connectionManageLink = MakeButton(string.Empty, () => SdkWindowLinks.Open(SdkWindowLinks.FindConnectionWindow()), "wizard-link");
            page.Add(connectionManageLink);

            page.Add(MakeLabel("Add an organization", "wizard-heading"));
            VisualElement addButtons = new();
            addButtons.AddToClassList("wizard-buttons");
            connectionAddButton = MakeButton("Choose the file...", OnChooseFile, "wizard-button");
            addButtons.Add(connectionAddButton);
            page.Add(addButtons);

            connectionNoteLabel = MakeLabel(string.Empty, "wizard-text");
            page.Add(connectionNoteLabel);
            return page;
        }

        #endregion

        #region Connections

        private void ReadConnections()
        {
            connections = SetupConnections.List(out connectionsFromBridge, out connectionsError);
            connectionsReadAt = EditorApplication.timeSinceStartup;
        }

        private static string NormalizedVersion(string version) => (version ?? string.Empty).Trim().TrimStart('v', 'V').ToLowerInvariant();

        /// <summary>
        /// The newest release of the registry that has the SDK's connection support (2026.7.0 on) for this editor, or the development version
        /// when its toggle is on and no release has it. Only SDK-Core is installed from it, with what it needs.
        /// </summary>
        private bool FindSdkCoreEntry(out PackageRegistry entry, out PackageDefinition core)
            => FindSdkCoreEntry(packageManagerConfig != null && packageManagerConfig.ShowPrereleases, out entry, out core);

        /// <summary>Whether no release has the connection support for this editor and the development version has: the page then points to development mode.</summary>
        private bool OnlyDevelopmentHasConnections()
            => !FindSdkCoreEntry(false, out _, out _) && FindSdkCoreEntry(true, out _, out _);

        private bool FindSdkCoreEntry(bool includeDevelopment, out PackageRegistry entry, out PackageDefinition core)
        {
            entry = null;
            core = null;

            bool Offers(PackageRegistry candidate)
                => candidate.RequiredUnityVersion == UnityVersion
                   && candidate.Packages != null
                   && candidate.Packages.Any(package => package.Name == sdk_core_package_name);

            PackageRegistry[] all = packageManagerConfig?.AllVersionsPackageRegistry ?? new PackageRegistry[0];
            entry = all.LastOrDefault(candidate => !candidate.Prerelease && SetupRun.CompareVersions(candidate.ReflectisVersion, first_connections_version) >= 0 && Offers(candidate));
            if (entry == null && includeDevelopment)
                entry = all.LastOrDefault(candidate => candidate.Prerelease && Offers(candidate));

            core = entry?.Packages.FirstOrDefault(package => package.Name == sdk_core_package_name);
            return core != null;
        }

        private ConnectionSupport GetConnectionSupport(out string message)
        {
            if (SetupConnections.IsAvailable(out string why))
            {
                message = null;
                return ConnectionSupport.Ready;
            }

            if (pendingConnection != null || compiling || importing)
            {
                message = pendingConnection != null
                    ? "Installing part of the SDK: Unity fetches and compiles it. What you asked for continues by itself."
                    : "Unity is compiling. The connections are available when it is done.";
                return ConnectionSupport.Waiting;
            }

            if (sdkRegisteredSnapshot)
            {
                string installed = InstalledVersion();
                message = SetupRun.CompareVersions(installed, first_connections_version) < 0
                    ? $"This project's SDK ({installed}) has no connection support: it needs Virtuademy {first_connections_version} or later. Change the version, then come back here. The setup works without connections."
                    : why;
                return ConnectionSupport.Unavailable;
            }

            if (FindSdkCoreEntry(out _, out _))
            {
                message = "Connections are kept by part of the SDK, which this project does not have yet.";
                return ConnectionSupport.CanPrepare;
            }

            message = OnlyDevelopmentHasConnections()
                ? $"Connections need Virtuademy {first_connections_version} or later. No release for this Unity has it yet, the development version does: " +
                  "turn on Development mode, at the bottom of the window, to add an organization or log in. The setup works without connections."
                : $"Connections need Virtuademy {first_connections_version} or later, which the list of versions does not offer for this Unity yet. The setup works without them.";
            return ConnectionSupport.Unavailable;
        }

        /// <summary>Reads the computer's connections now and then (a login finished in the browser, or in another project) and rebuilds the rows only when something changed.</summary>
        private void RefreshConnectionPage(bool force)
        {
            if (connectionList == null)
                return;

            if (force || EditorApplication.timeSinceStartup - connectionsReadAt > connections_refresh_seconds)
                ReadConnections();

            ConnectionSupport support = GetConnectionSupport(out string supportMessage);
            if (connectionsError != null && support == ConnectionSupport.Ready)
                supportMessage = $"The connections could not be read: {connectionsError}";

            string signature = string.Join("|", connections.Select(c => $"{c.Key}/{c.LoggedIn}/{c.User}/{c.PlatformVersion}/{c.Platform}/{busyConnections.Contains(c.Key)}"))
                               + $"#{support}#{supportMessage}#{importingFile}#{connectionNote}#{SdkWindowLinks.FindConnectionWindow()?.Name}";
            if (signature == connectionsSignature)
                return;

            connectionsSignature = signature;

            connectionSupportLabel.text = supportMessage ?? string.Empty;
            SetShown(connectionSupportLabel, !string.IsNullOrEmpty(supportMessage));

            connectionList.Clear();
            foreach (SetupConnection connection in connections)
                connectionList.Add(BuildConnectionRow(connection, support));

            if (connections.Count == 0)
                connectionList.Add(MakeLabel("None yet.", "wizard-note"));

            SdkWindowLink manage = sdkRegisteredSnapshot ? SdkWindowLinks.FindConnectionWindow() : null;
            connectionManageLink.text = manage == null ? string.Empty : $"Manage in {manage.Name}";
            SetShown(connectionManageLink, manage != null);

            bool busy = importingFile || support == ConnectionSupport.Waiting;
            connectionAddButton.SetEnabled(!busy && support != ConnectionSupport.Unavailable);

            connectionNoteLabel.text = connectionNote ?? string.Empty;
            SetShown(connectionNoteLabel, !string.IsNullOrEmpty(connectionNote));
        }

        private VisualElement BuildConnectionRow(SetupConnection connection, ConnectionSupport support)
        {
            RowView row = new(withIcon: true);

            // The organization's name alone: a customer's connections are production ones. The other environments, ours for
            // testing, carry a coloured chip, as in Publish Environments.
            string title = string.IsNullOrEmpty(connection.TenantLabel) ? connection.Key : connection.TenantLabel;
            string kind = connection.EnvironmentKind;
            string chip = kind switch
            {
                "prod" => null,
                "prep" => "Prep",
                "sandbox" => "Sandbox",
                _ => connection.Environment,
            };

            string account = connection.LoggedIn
                ? (string.IsNullOrEmpty(connection.User) ? "Logged in" : $"Logged in as {connection.User}")
                : "Not logged in";
            string detail = string.IsNullOrEmpty(connection.PlatformVersion) ? account : $"Platform version {connection.PlatformVersion} · {account}";

            RowState state = connection.LoggedIn ? RowState.Ok : RowState.None;
            if (!connection.IsCreatorKitApp)
            {
                state = RowState.Warn;
                detail += $"\nThis app is made for {connection.Platform}, not for the SDK: it cannot receive environments.";
            }

            bool busy = busyConnections.Contains(connection.Key);
            string button = null;
            if (busy)
                button = "Waiting for the browser...";
            else if (support == ConnectionSupport.Ready)
                button = connection.LoggedIn ? "Log out" : "Log in";
            else if (support == ConnectionSupport.CanPrepare && !connection.LoggedIn)
                button = "Log in";

            row.Set(state, title, detail, button, !busy && !importingFile, () => OnConnectionButton(connection));
            row.SetChip(chip, kind == "prod" ? null : kind);
            return row.Root;
        }

        private void LeaveConnectionPage()
        {
            bool fromStatus = connectionFromStatus;
            connectionFromStatus = false;
            connectionNote = null;
            choicesChangeMode = false;
            GoTo(fromStatus ? WizardPage.Status : WizardPage.Choices);
        }

        #endregion

        #region Actions

        private async void OnConnectionButton(SetupConnection connection)
        {
            if (!SetupConnections.IsAvailable(out _))
            {
                if (ConfirmPrepare("Log in", "Then the login opens in your browser."))
                    RequestPrepare(new SetupUserSettings.PendingConnection { Action = "login", Key = connection.Key });
                return;
            }

            if (connection.LoggedIn)
            {
                if (!busyConnections.Add(connection.Key))
                    return;

                RefreshConnectionPage(true);
                SetupBridgeResult result = await SetupConnections.LogoutAsync(connection.Key);
                busyConnections.Remove(connection.Key);
                connectionNote = result.Ok ? "Logged out." : result.Describe();
                RefreshConnectionPage(true);
                return;
            }

            await LoginConnectionAsync(connection.Key);
        }

        private async Task LoginConnectionAsync(string key)
        {
            if (!busyConnections.Add(key))
                return;

            connectionNote = "The login opens in your browser. Finish it there.";
            RefreshConnectionPage(true);

            SetupBridgeResult result = await SetupConnections.LoginAsync(key);
            busyConnections.Remove(key);
            connectionNote = result.Ok ? "Logged in." : result.Describe();
            RefreshConnectionPage(true);
        }

        private async void OnChooseFile()
        {
            // Without the connection support the file is asked for once it is installed, so it is chosen once, not before and after.
            if (!SetupConnections.IsAvailable(out _))
            {
                if (ConfirmPrepare("Add an organization", "Then it asks you for the configuration file."))
                    RequestPrepare(new SetupUserSettings.PendingConnection { Action = "choose-file" });
                return;
            }

            string path = ChooseConfigurationFile();
            if (!string.IsNullOrEmpty(path))
                await ImportAndLoginAsync(path);
        }

        private static string ChooseConfigurationFile()
            => EditorUtility.OpenFilePanel("Choose the configuration file from the Back Office", string.Empty, string.Empty);

        private static bool ConfirmPrepare(string title, string then)
            => EditorUtility.DisplayDialog(title,
                "Connections are kept by a part of the SDK that this project does not have yet. The setup installs it first: about a " +
                "minute, while Unity downloads and compiles it. " + then,
                "Install and continue", "Cancel");

        /// <summary>Imports the file the person chose and logs in to the connection it brought, through the SDK. A file with several environments
        /// imports them all and logs in to the first: the others have their own Log in.</summary>
        private async Task ImportAndLoginAsync(string path)
        {
            if (importingFile)
                return;

            importingFile = true;
            connectionNote = "Importing the file...";
            RefreshConnectionPage(true);

            try
            {
                SetupBridgeResult import = await SetupConnections.ImportAsync(path);
                if (!import.Ok && import.Connections.Count == 0)
                {
                    connectionNote = import.Describe();
                    return;
                }

                string note = import.Ok
                    ? (import.Connections.Count == 1 ? "Imported 1 connection." : $"Imported {import.Connections.Count} connections.")
                    : $"Imported {import.Connections.Count}, but not all: {import.Describe()}";

                SetupConnection target = import.Connections.FirstOrDefault(connection => !connection.LoggedIn);
                if (target != null)
                {
                    busyConnections.Add(target.Key);
                    connectionNote = note + " The login opens in your browser. Finish it there.";
                    RefreshConnectionPage(true);

                    SetupBridgeResult login = await SetupConnections.LoginAsync(target.Key);
                    busyConnections.Remove(target.Key);
                    note += login.Ok ? " Logged in." : " " + login.Describe();
                }

                connectionNote = note;
            }
            finally
            {
                importingFile = false;
                RefreshConnectionPage(true);
            }
        }

        /// <summary>Installs just SDK-Core (and what it needs) from the registry entry that has the connection support, and remembers
        /// what to do once it has compiled.</summary>
        private void RequestPrepare(SetupUserSettings.PendingConnection pending)
        {
            if (!FindSdkCoreEntry(out PackageRegistry entry, out PackageDefinition core))
            {
                connectionNote = "The list of versions has no SDK-Core with connections for this Unity.";
                RefreshConnectionPage(true);
                return;
            }

            pending.CreatedUtc = DateTime.UtcNow.ToString("o");

            // The manifest only: the setup's own record of installed packages is the run's business, and it must not see a half install.
            List<PackageDefinition> packages = entry.FullDependencies[core.Name].Select(name => entry.PackageDictionary[name]).Append(core).ToList();
            try
            {
                InstallPackages(packages);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
            {
                // Nothing was installed, so nothing waits for it.
                connectionNote = $"The connection support could not be installed: Packages/manifest.json could not be changed ({ex.Message}).";
                RefreshConnectionPage(true);
                return;
            }

            // Saved only now: the action waits for an install that was written.
            pendingConnection = pending;
            SetupUserSettings.Pending = pending;
            SetupUserSettings.PreparedPackages = packages.Select(package => package.Name).ToList();

            connectionNote = null;
            RefreshConnectionPage(true);

            UnityEngine.Debug.Log($"[Setup] Installing {core.DisplayName ?? core.Name} {core.Version} (from Virtuademy {entry.ReflectisVersion}) to reach the connections.");
            Client.Resolve();
        }

        /// <summary>Goes on with the action that waited for SDK-Core, once it has compiled; drops it when it never does, or is too old.</summary>
        private async void ResumePendingConnection()
        {
            if (pendingConnection == null || resumingConnection)
                return;

            TimeSpan age = DateTime.UtcNow - pendingConnection.Created;
            if (age > pending_connection_lifetime)
            {
                ClearPendingConnection();
                return;
            }

            // A setup run reloads the domain at every package step: the action waits for it to end.
            if (SetupRun.IsRunning)
                return;

            if (SetupConnections.IsAvailable(out _))
            {
                SetupUserSettings.PendingConnection pending = pendingConnection;
                ClearPendingConnection();

                resumingConnection = true;
                try
                {
                    ReadConnections();
                    if (Page != WizardPage.Connection)
                        GoTo(WizardPage.Connection);

                    if (pending.Action == "choose-file")
                    {
                        connectionNote = "The connection support is installed. Choose the configuration file.";
                        RefreshConnectionPage(true);

                        string path = ChooseConfigurationFile();
                        if (!string.IsNullOrEmpty(path))
                        {
                            await ImportAndLoginAsync(path);
                        }
                        else
                        {
                            connectionNote = "The connection support is installed. Choose the file when you are ready.";
                            RefreshConnectionPage(true);
                        }
                    }
                    else if (pending.Action == "login" && !string.IsNullOrEmpty(pending.Key))
                    {
                        await LoginConnectionAsync(pending.Key);
                    }
                }
                finally
                {
                    resumingConnection = false;
                }

                return;
            }

            if (age > pending_connection_load_wait && !compiling && !importing)
            {
                bool registered = SetupSteps.IsRegistered(sdk_core_package_name);

                // Not registered: the Package Manager may still be resolving. Its list answers when it is idle; asked once, then looked at.
                if (!registered)
                {
                    packageIdleProbe ??= Client.List(offlineMode: true, includeIndirectDependencies: false);
                    if (!packageIdleProbe.IsCompleted)
                        return;

                    packageIdleProbe = null;
                    if (SetupSteps.IsRegistered(sdk_core_package_name))
                        return;
                }

                ClearPendingConnection();
                connectionNote = registered
                    ? "The connection support was installed but did not load. The Console may show why. You can go on without it."
                    : "The connection support (SDK-Core) was not installed: the Package Manager did not resolve it. Its errors are in the Console. You can go on without it.";
                RefreshConnectionPage(true);
            }
        }

        private void ClearPendingConnection()
        {
            pendingConnection = null;
            SetupUserSettings.Pending = null;
            connectionsSignature = null;
        }

        #endregion
    }
}
