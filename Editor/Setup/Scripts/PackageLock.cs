using Newtonsoft.Json.Linq;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using UnityEngine;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// What this installer does to <c>Packages/packages-lock.json</c>, for the window's
    /// <b>Re-resolve packages from git</b> button and for the <c>resolve</c> step of
    /// <see cref="SetupCli"/>.
    ///
    /// The lock pins each git dependency to a resolved COMMIT (<c>hash</c>), not to the ref the
    /// manifest asked for. Nothing else moves it: not the refresh button, which re-reads the
    /// registry; not <c>Client.Resolve</c> on its own, which honours the lock; not deleting
    /// <c>Library/PackageCache</c>, which re-clones the same locked hash. Dropping the entry is what
    /// lets UPM look the tag or branch up again.
    ///
    /// Only entries that are git packages of ours (<see cref="CreatorKitSetupWindow.IsManagedPackage(string, IEnumerable{PackageRegistry})"/>)
    /// are ever touched: a registry package's pin is a version, and re-resolving those is not what
    /// anyone wants. The installer itself is left out on purpose, see <see cref="ManagedGitPackages"/>;
    /// only <see cref="PackageAutoUpdate"/> asks for it.
    /// </summary>
    internal static class PackageLock
    {
        /// <summary>A pinned git package whose manifest ref points to another commit on the remote now.</summary>
        internal sealed class StalePackage
        {
            public string Name;
            /// <summary>The manifest's ref: a tag or a branch (never a commit), or null for the remote's default branch.</summary>
            public string Reference;
            public string LockedCommit;
            public string RemoteCommit;
        }

        /// <summary>The outcome of comparing the lock with the remotes.</summary>
        internal sealed class StaleReport
        {
            public readonly List<StalePackage> Stale = new();

            /// <summary>Packages the remote did not answer for, or whose lock entry has no commit.
            /// They are neither stale nor up to date, and nothing is unpinned for them.</summary>
            public readonly List<string> Unknown = new();

            /// <summary>How many packages were compared, commit refs excluded.</summary>
            public int Compared;

            /// <summary>True when every package that had to be compared was. False as soon as one is
            /// <see cref="Unknown"/>, for example with the network down.</summary>
            public bool Complete => Unknown.Count == 0;
        }

        private const int ls_remote_timeout_ms = 30000;

        // A manifest ref that is a commit needs no lookup: it cannot move.
        private static readonly Regex commit_ref = new("^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled);

        // Per launch: the answers of `git ls-remote`, by "url#ref". A null answer is cached too, so
        // a remote that is down is asked once.
        private static readonly Dictionary<string, string> remote_commits = new();

        /// <summary>
        /// Forgets what <c>git ls-remote</c> answered earlier in this editor session, so that the next comparison asks the remotes
        /// again. A check the person asked for ("Reload packages") must not be answered from what the last one saw. Not while a check
        /// is running: its lookups are in flight, and it reads its answers from here when they are in.
        /// </summary>
        internal static void ForgetRemoteAnswers()
        {
            if (!PackageAutoUpdate.IsRunning)
                remote_commits.Clear();
        }

        internal static string LockFilePath => Path.Combine(Application.dataPath, "../Packages/packages-lock.json");

        /// <summary>
        /// The git packages of ours that <c>packages-lock.json</c> pins: managed (listed by a
        /// registry entry) and not the installer. The installer is not in any registry entry, so it
        /// was never touched; it is excluded by name as well because it is the code that is running:
        /// unpinning it makes Unity recompile this very assembly, and its version is the project's
        /// own choice, not the registry's. <paramref name="includeInstaller"/> adds it, for the one caller
        /// that has thought it through (<see cref="PackageAutoUpdate"/>).
        /// </summary>
        private static List<string> ManagedGitPackages(JObject dependencies, IEnumerable<PackageRegistry> registry, bool includeInstaller = false)
            => dependencies.Properties()
                .Where(entry => (string)entry.Value["source"] == "git"
                                && (includeInstaller
                                    ? CreatorKitSetupWindow.packages_to_exclude.Contains(entry.Name) || CreatorKitSetupWindow.IsManagedPackage(entry.Name, registry)
                                    : CreatorKitSetupWindow.IsManagedPackage(entry.Name, registry) && !CreatorKitSetupWindow.packages_to_exclude.Contains(entry.Name)))
                .Select(entry => entry.Name)
                .ToList();

        private static bool TryReadLock(out JObject lockObj, out JObject dependencies, out string problem)
        {
            lockObj = null;
            dependencies = null;
            problem = null;

            if (!File.Exists(LockFilePath))
            {
                problem = "There is no packages-lock.json.";
                return false;
            }

            lockObj = JObject.Parse(File.ReadAllText(LockFilePath));
            dependencies = lockObj["dependencies"] as JObject;
            if (dependencies == null)
                problem = "packages-lock.json has no dependencies section.";

            return dependencies != null;
        }

        /// <summary>
        /// Drops <paramref name="names"/> from <c>packages-lock.json</c> (every managed git package
        /// when null), so UPM resolves them again. Returns the packages that were pinned and are now
        /// not. The caller starts the resolve (the window) or leaves it to the next editor launch
        /// (<see cref="SetupCli"/>).
        /// </summary>
        internal static List<string> Unpin(IEnumerable<PackageRegistry> registry, IEnumerable<string> names = null, bool includeInstaller = false)
        {
            List<string> unpinned = new();
            if (!TryReadLock(out JObject lockObj, out JObject dependencies, out string problem))
            {
                UnityEngine.Debug.LogWarning("[Setup] " + problem + " Nothing to re-resolve from.");
                return unpinned;
            }

            HashSet<string> candidates = new(ManagedGitPackages(dependencies, registry, includeInstaller));
            HashSet<string> wanted = names == null ? candidates : new HashSet<string>(names.Where(candidates.Contains));

            foreach (JProperty entry in dependencies.Properties().Where(entry => wanted.Contains(entry.Name)).ToList())
            {
                entry.Remove();
                unpinned.Add(entry.Name);
            }

            if (unpinned.Count > 0)
                File.WriteAllText(LockFilePath, lockObj.ToString());

            return unpinned;
        }

        /// <summary>
        /// Compares the commit each of our git packages is pinned to with what its manifest ref
        /// points to on the remote now, through <c>git ls-remote</c>. A tag is peeled (<c>^{}</c>), so
        /// an annotated tag gives its commit. A commit ref is up to date by definition; a package
        /// with no ref follows the remote's HEAD. Changes nothing.
        /// </summary>
        internal static StaleReport FindStale(IEnumerable<PackageRegistry> registry)
        {
            StaleComparison comparison = StartComparison(registry, includeInstaller: false);
            Task.WaitAll(comparison.Answers);
            return comparison.Finish();
        }

        /// <summary>
        /// <see cref="FindStale"/> without blocking the caller while <c>git ls-remote</c> runs: the lock and
        /// the manifest are read on the calling thread (they need <see cref="Application.dataPath"/>, a
        /// main-thread API), the remote lookups run on the thread pool and are awaited. Used at editor start,
        /// where a slow network must not freeze the editor. <paramref name="includeInstaller"/>: see
        /// <see cref="ManagedGitPackages"/>.
        /// </summary>
        internal static async Task<StaleReport> FindStaleAsync(IEnumerable<PackageRegistry> registry, bool includeInstaller)
        {
            StaleComparison comparison = StartComparison(registry, includeInstaller);
            await Task.WhenAll(comparison.Answers);
            return comparison.Finish();
        }

        /// <summary>The lookups a comparison started, and what it needs to turn their answers into a <see cref="StaleReport"/>.</summary>
        private sealed class StaleComparison
        {
            public StaleReport Report = new();
            public List<(string name, string url, string reference, string locked)> ToCompare = new();
            public List<(string url, string reference)> Lookups = new();
            public Task<string>[] Answers = new Task<string>[0];

            /// <summary>Call on the thread that started it, once every answer is in.</summary>
            public StaleReport Finish()
            {
                for (int i = 0; i < Lookups.Count; i++)
                    remote_commits[CacheKey(Lookups[i].url, Lookups[i].reference)] = Answers[i].Result;

                foreach (var item in ToCompare)
                {
                    string remote = remote_commits[CacheKey(item.url, item.reference)];
                    if (remote == null)
                    {
                        Report.Unknown.Add(item.name);
                        continue;
                    }

                    Report.Compared++;
                    if (!SameCommit(item.locked, remote))
                        Report.Stale.Add(new StalePackage { Name = item.name, Reference = item.reference, LockedCommit = item.locked, RemoteCommit = remote });
                }

                return Report;
            }
        }

        private static StaleComparison StartComparison(IEnumerable<PackageRegistry> registry, bool includeInstaller)
        {
            StaleComparison comparison = new();
            StaleReport report = comparison.Report;
            if (!TryReadLock(out _, out JObject dependencies, out _))
                return comparison;

            // One lookup per distinct url and ref, run side by side: ls-remote is a network call.
            List<(string name, string url, string reference, string locked)> toCompare = comparison.ToCompare;
            foreach (string name in ManagedGitPackages(dependencies, registry, includeInstaller))
            {
                string manifestValue = CreatorKitSetupWindow.ReadManifestDependency(name);
                if (manifestValue == null || !TryParseGitReference(manifestValue, out string url, out string reference))
                    continue;

                if (reference != null && commit_ref.IsMatch(reference))
                    continue;

                string locked = (string)dependencies[name]["hash"];
                if (string.IsNullOrEmpty(locked))
                {
                    report.Unknown.Add(name);
                    continue;
                }

                toCompare.Add((name, url, reference, locked));
            }

            comparison.Lookups = toCompare
                .Select(item => (item.url, item.reference))
                .Distinct()
                .Where(key => !remote_commits.ContainsKey(CacheKey(key.url, key.reference)))
                .ToList();
            comparison.Answers = comparison.Lookups.Select(key => Task.Run(() => LookUpRemoteCommit(key.url, key.reference))).ToArray();
            return comparison;
        }

        private static string CacheKey(string url, string reference) => url + "#" + reference;

        private static bool SameCommit(string a, string b)
            => a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Splits a manifest value the way UPM reads it: <c>url[?path=...][#ref]</c>. False when the
        /// value is not a git URL (a registry version, <c>file:</c>), which has nothing to look up.
        /// </summary>
        internal static bool TryParseGitReference(string manifestValue, out string url, out string reference)
        {
            url = null;
            reference = null;

            if (manifestValue.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                return false;

            string address = manifestValue;
            int hash = address.IndexOf('#');
            if (hash >= 0)
            {
                reference = address[(hash + 1)..];
                address = address[..hash];
            }

            int query = address.IndexOf('?');
            if (query >= 0)
                address = address[..query];

            if (address.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
                address = address[4..];

            bool isGit = address.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                         || address.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
                         || address.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
                         || address.StartsWith("git@", StringComparison.OrdinalIgnoreCase);
            if (!isGit)
                return false;

            url = address;
            if (string.IsNullOrEmpty(reference))
                reference = null;
            return true;
        }

        /// <summary>
        /// The commit <paramref name="reference"/> of <paramref name="url"/> points to on the remote:
        /// the peeled tag, else the branch, else the HEAD when there is no ref. Null when git could
        /// not ask (no network, no access, git missing) or the ref does not exist there. Safe to call
        /// from a worker thread.
        /// </summary>
        private static string LookUpRemoteCommit(string url, string reference)
        {
            string arguments = reference == null
                ? $"ls-remote \"{url}\" HEAD"
                : $"ls-remote \"{url}\" \"refs/tags/{reference}\" \"refs/tags/{reference}^{{}}\" \"refs/heads/{reference}\"";

            string output = RunGit(arguments);
            if (output == null)
                return null;

            Dictionary<string, string> commits = new();
            foreach (string line in output.Split('\n'))
            {
                string[] parts = line.Trim().Split('\t');
                if (parts.Length == 2)
                    commits[parts[1]] = parts[0];
            }

            if (reference == null)
                return commits.TryGetValue("HEAD", out string head) ? head : null;

            // Git prefers a tag to a branch of the same name; an annotated tag lists the tag object
            // first and the commit under ^{}.
            if (commits.TryGetValue($"refs/tags/{reference}^{{}}", out string peeled))
                return peeled;
            if (commits.TryGetValue($"refs/tags/{reference}", out string tag))
                return tag;
            return commits.TryGetValue($"refs/heads/{reference}", out string branch) ? branch : null;
        }

        /// <summary>
        /// Standard output of <c>git &lt;arguments&gt;</c>, or null when it failed or took longer than
        /// <see cref="ls_remote_timeout_ms"/>. Never prompts: a batch run has nobody to answer.
        /// </summary>
        private static string RunGit(string arguments)
        {
            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = "git",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                startInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "never";

                using Process process = new() { StartInfo = startInfo };
                System.Text.StringBuilder output = new();
                process.OutputDataReceived += (_, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                process.ErrorDataReceived += (_, _) => { };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(ls_remote_timeout_ms))
                {
                    try { process.Kill(); } catch { /* already gone */ }
                    return null;
                }

                // The overload without a timeout also waits for the output handlers to finish.
                process.WaitForExit();
                if (process.ExitCode != 0)
                    return null;

                lock (output)
                    return output.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
