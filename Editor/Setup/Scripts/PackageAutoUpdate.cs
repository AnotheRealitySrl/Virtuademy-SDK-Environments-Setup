using Newtonsoft.Json;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using UnityEditor;
using UnityEditor.PackageManager;

using UnityEngine;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// Keeps the project's Virtuademy git packages current without anybody pressing a button. Once per
    /// editor session, when the project opens in the editor (never in batch mode: <see cref="SetupCli"/>
    /// has the <c>resolve</c> step for that), it runs the same stale check as that step: for every git
    /// package of ours, the commit <c>packages-lock.json</c> pins against what the manifest's ref (a tag,
    /// a branch, or none) points to on the remote now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> A tag or a branch is a moving name, and Unity's lock pins the commit it first saw:
    /// when a release is re-cut on the same tag, nobody's project moves until somebody drops the lock
    /// entry by hand (the window's <b>Re-resolve packages from git</b> button). This does it.
    /// </para>
    /// <para>
    /// <b>All or nothing.</b> If every remote answered and some package moved, exactly the moved ones are
    /// unpinned and <see cref="Client.Resolve"/> is called, after logging which package moved from which
    /// commit to which. If any remote could not be asked (no network, no access, a ref that is gone) nothing
    /// is changed and a warning is logged once: updating only the packages whose remote answered could leave
    /// the project with some at the new commit and the others at the old one, a combination nobody tested.
    /// </para>
    /// <para>
    /// <b>Off the main thread.</b> The registry is downloaded with an awaited <see cref="HttpClient"/> and
    /// every <c>git ls-remote</c> runs on the thread pool (<see cref="PackageLock.FindStaleAsync"/>); the
    /// editor is never blocked by a slow network. The lock and the manifest are read on the main thread,
    /// because they need <see cref="Application.dataPath"/>.
    /// </para>
    /// <para>
    /// <b>The installer is included</b>, unlike in <c>resolve</c>: it is the package whose tag moves when
    /// this very feature is fixed or improved, and creators install it by a git URL, so nothing else would
    /// ever move it. That is safe because the check is finished, and the session flag set, before the lock
    /// is touched: the recompile that follows <see cref="Client.Resolve"/> replaces this assembly, the new
    /// one finds the flag and does nothing, and no state this class needs is held in memory.
    /// </para>
    /// <para>
    /// <b>Opt-out.</b> The footer toggle of the setup window, <see cref="Enabled"/>, saved per project and per
    /// machine like the one that controls the window's own startup check. On by default.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    internal static class PackageAutoUpdate
    {
        internal enum Outcome
        {
            /// <summary>Not run: batch mode, or already done in this editor session.</summary>
            Skipped,
            /// <summary>Turned off with <see cref="Enabled"/>.</summary>
            Disabled,
            /// <summary>The project has no <c>packages-lock.json</c>: nothing is pinned.</summary>
            NoLockFile,
            /// <summary>The registry, which says which packages are ours, could not be read. Nothing changed.</summary>
            RegistryUnavailable,
            /// <summary>At least one remote could not be asked. Nothing changed.</summary>
            RemotesUnavailable,
            /// <summary>Every remote answered and none moved.</summary>
            NothingMoved,
            /// <summary>Every remote answered and the moved packages were unpinned; the resolve was started.</summary>
            Updated,
        }

        private const string checked_this_session_key = "Virtuademy.SDK.Environments.Setup.AutoUpdateCheckedThisSession";
        private const string warned_this_session_key = "Virtuademy.SDK.Environments.Setup.AutoUpdateWarnedThisSession";

        // The same published registry the window and SetupCli's default read.
        private const int registry_timeout_seconds = 30;

        private static bool running;

        /// <summary>A check is going (the lookups are in flight): <see cref="PackageLock.ForgetRemoteAnswers"/> leaves the answers alone then.</summary>
        internal static bool IsRunning => running;

        /// <summary>The packages the last check unpinned, and so reloaded: empty when none moved or nothing was changed.</summary>
        internal static IReadOnlyList<string> LastMoved { get; private set; } = new string[0];

        // Per project (productGUID) and per machine, like the window's startup toggle: whether the editor
        // may move the project's packages by itself is a personal setting, not something to commit.
        private static string EnabledPrefKey => "Virtuademy.SDK.Environments.Setup.AutoUpdatePackages." + PlayerSettings.productGUID;

        /// <summary>Whether the editor may update the project's Virtuademy git packages when it opens. On by default.</summary>
        internal static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPrefKey, true);
            set => EditorPrefs.SetBool(EnabledPrefKey, value);
        }

        static PackageAutoUpdate()
        {
            // Batch builds and the command line have nobody to log a warning to, and SetupCli's resolve
            // step does this there on request. [InitializeOnLoad] runs on every domain reload, so the
            // session flag, which survives them, keeps it to the first one (and to the one the update
            // itself causes: the flag is set before the lock is touched).
            if (Application.isBatchMode || SessionState.GetBool(checked_this_session_key, false))
            {
                return;
            }

            // Deferred, like the window's startup check: on project open the asset database is not ready.
            EditorApplication.delayCall += () => _ = CheckAsync();
        }

        /// <summary>
        /// The check and, when something moved, the update. Call on the main thread. <paramref name="force"/>
        /// ignores the batch-mode, session and opt-out guards (the test hook).
        /// </summary>
        internal static async Task<Outcome> CheckAsync(bool force = false)
        {
            if (!force && (Application.isBatchMode || SessionState.GetBool(checked_this_session_key, false)))
            {
                return Outcome.Skipped;
            }

            if (!force && !Enabled)
            {
                return Outcome.Disabled;
            }

            if (running)
            {
                return Outcome.Skipped;
            }

            running = true;
            LastMoved = new string[0];
            try
            {
                if (!File.Exists(PackageLock.LockFilePath))
                {
                    SessionState.SetBool(checked_this_session_key, true);
                    return Outcome.NoLockFile;
                }

                PackageRegistry[] registry = await ReadRegistryAsync();
                if (registry == null || registry.Length == 0)
                {
                    Warn("the package registry could not be read, so which packages are ours is unknown. Nothing was changed.");
                    SessionState.SetBool(checked_this_session_key, true);
                    return Outcome.RegistryUnavailable;
                }

                PackageLock.StaleReport stale = await PackageLock.FindStaleAsync(registry, includeInstaller: true);

                if (!stale.Complete)
                {
                    Warn($"could not tell whether these moved on the remote (no network, no access, or the ref is gone): " +
                         $"{string.Join(", ", stale.Unknown)}. Nothing was changed, so the project never mixes new and old packages" +
                         (stale.Stale.Count > 0 ? $"; still pinned to an older commit: {string.Join(", ", stale.Stale.Select(package => package.Name))}" : "") + ".");
                    SessionState.SetBool(checked_this_session_key, true);
                    return Outcome.RemotesUnavailable;
                }

                if (stale.Stale.Count == 0)
                {
                    Debug.Log($"[Setup] Packages: {stale.Compared} Virtuademy git package(s) checked against their remotes, none moved.");
                    SessionState.SetBool(checked_this_session_key, true);
                    return Outcome.NothingMoved;
                }

                // Before the lock is touched: the reload the resolve causes must find the flag set.
                SessionState.SetBool(checked_this_session_key, true);

                foreach (PackageLock.StalePackage package in stale.Stale)
                {
                    Debug.Log($"[Setup] Packages: {package.Name} moved on its remote, {ShortCommit(package.LockedCommit)} -> " +
                              $"{ShortCommit(package.RemoteCommit)} ({package.Reference ?? "the default branch"}).");
                }

                LastMoved = stale.Stale.Select(package => package.Name).ToList();
                var unpinned = PackageLock.Unpin(registry, stale.Stale.Select(package => package.Name), includeInstaller: true);
                Debug.Log($"[Setup] Packages: unpinned {unpinned.Count} of {stale.Stale.Count} moved package(s) from packages-lock.json; " +
                          "resolving. Each one is cloned again, so give it a moment. Turn this off in Virtuademy > Setup project.");
                Client.Resolve();
                return Outcome.Updated;
            }
            catch (Exception ex)
            {
                Warn($"the check failed ({ex.GetType().Name}: {ex.Message}). Nothing was changed.");
                SessionState.SetBool(checked_this_session_key, true);
                return Outcome.RemotesUnavailable;
            }
            finally
            {
                running = false;
            }
        }

        /// <summary>
        /// The same check, run now because the person asked (the status page's "Reload packages"): it ignores the session and opt-out
        /// guards, asks the remotes again, and says in a sentence what it did. Compares commit SHAs, never versions. Call on the main thread.
        /// </summary>
        internal static async Task<string> CheckNowAsync()
        {
            PackageLock.ForgetRemoteAnswers();
            Outcome outcome = await CheckAsync(force: true);

            return outcome switch
            {
                Outcome.NothingMoved => "All up to date.",
                Outcome.Updated => $"Reloaded: {string.Join(", ", LastMoved)}. Unity is resolving them now.",
                Outcome.NoLockFile => "This project has no packages-lock.json yet, so nothing is pinned.",
                Outcome.RegistryUnavailable => "The list of Virtuademy versions could not be read, so the packages were not checked.",
                Outcome.RemotesUnavailable => "Some remotes could not be reached (no network, or no access), so nothing was changed.",
                _ => "A check is already running.",
            };
        }

        private static async Task<PackageRegistry[]> ReadRegistryAsync()
        {
            try
            {
                using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(registry_timeout_seconds) };
                string body = await client.GetStringAsync(CreatorKitSetupWindow.package_registry_path);
                return JsonConvert.DeserializeObject<PackageRegistry[]>(body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                return null;
            }
        }

        // Once per editor session: a project opened offline gets one line, not one per domain reload.
        private static void Warn(string message)
        {
            if (SessionState.GetBool(warned_this_session_key, false))
            {
                return;
            }

            SessionState.SetBool(warned_this_session_key, true);
            Debug.LogWarning("[Setup] Packages: " + message);
        }

        private static string ShortCommit(string commit) => commit.Length > 8 ? commit[..8] : commit;
    }
}
