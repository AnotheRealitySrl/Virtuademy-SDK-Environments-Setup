using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using UnityEditor;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>
    /// Runs the SDK's update routines (the menu items under <b>Virtuademy ▸ Update routines</b>) for the engine's
    /// <c>update-routine</c> step, by reflection: this package installs the SDK, so it cannot reference it. It drives the
    /// same code the routines' windows and <c>UpdateRoutinesCli</c> drive, with the windows' defaults (every file the scan
    /// finds, every option on):
    /// <list type="bullet">
    /// <item><c>v2026.5 -&gt; v2026.6</c>: <c>VirtuademyRenameRoutine.ScanProject</c> and <c>Apply</c>, in
    /// <c>Virtuademy.SDK.Environments.UpdateRoutines</c>, which compiles even when the project does not;</item>
    /// <item><c>v2026.4.x -&gt; v2026.5.0</c>: <c>CheckPlatformUnitMobilePortRoutine.Scan</c> and <c>Apply</c>, in the SDK's
    /// editor assembly, which it runs only once the first has finished: it saves graphs, which are unreadable until the
    /// rename is done.</item>
    /// </list>
    /// <c>UpdateRoutinesCli.Run</c> itself cannot be called: it exits the editor when it is done. A routine is
    /// recognised by the series it moves to, as <c>-vdRoutine</c> names it; one that is not here is left to the menu.
    /// <para>
    /// The rename cannot finish in one editor session: it rewrites scripts, assembly definitions and the package manifest,
    /// so Unity recompiles and reloads, and only after that can the files it changed be re-saved (the SDK does it by itself,
    /// <c>VirtuademyUpdateResave</c>). So it is a small state machine kept in the step's scratch (<c>phase</c>): apply,
    /// wait for the re-save, scan again, and apply again while work is left (at most
    /// <see cref="max_apply_rounds"/> rounds). Each call does one thing and returns.
    /// </para>
    /// </summary>
    internal static class SetupUpdateRoutines
    {
        internal enum Kind { Rename, Mobile }

        internal enum Status { InProgress, Done, Failed }

        internal readonly struct RoutineTick
        {
            internal RoutineTick(Status status, string message)
            {
                Status = status;
                Message = message;
            }

            internal Status Status { get; }
            internal string Message { get; }
        }

        private const string rename_type = "Virtuademy.BreakingChangeSolvers.VirtuademyRenameRoutine";
        private const string resave_type = "Virtuademy.BreakingChangeSolvers.VirtuademyUpdateResave";
        private const string save_guard_type = "Virtuademy.BreakingChangeSolvers.VirtuademyUpdateSaveGuard";
        private const string mobile_type = "Virtuademy.SDK.Environments.VisualScripting.Editor.CheckPlatformUnitMobilePortRoutine";

        private const int max_apply_rounds = 4;

        private const BindingFlags public_static = BindingFlags.Public | BindingFlags.Static;

        /// <summary>The routine a menu name stands for: the one whose target series is the series it moves to.</summary>
        internal static bool TryGetKind(string menuName, out Kind kind)
        {
            kind = default;
            string[] sides = menuName.Split(new[] { "->" }, StringSplitOptions.None);
            if (sides.Length != 2 || !SetupSteps.TryParseSeries(sides[1], out var target))
                return false;

            switch (target)
            {
                case (2026, 6):
                    kind = Kind.Rename;
                    return true;
                case (2026, 5):
                    kind = Kind.Mobile;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Does one thing of the routine and says whether it is finished. <paramref name="data"/> is the step's scratch,
        /// kept across reloads; <paramref name="save"/> writes it to disk, which is done before anything that may start a reload.</summary>
        internal static RoutineTick Run(Kind kind, Dictionary<string, string> data, Action save)
        {
            try
            {
                return kind == Kind.Rename ? TickRename(data, save) : TickMobile();
            }
            catch (TargetInvocationException ex)
            {
                return Failed((ex.InnerException ?? ex).Message + " The Console has the details.", ex.InnerException ?? ex);
            }
        }

        #region 2026.6

        private static RoutineTick TickRename(Dictionary<string, string> data, Action save)
        {
            Type routine = FindType(rename_type);
            Type resave = FindType(resave_type);
            if (routine == null || resave == null)
                return Failed("The routine's code is not loaded: the SDK's update routines did not compile.");

            bool resavePending = (bool)resave.GetProperty("IsPending", public_static).GetValue(null);
            string phase = data.TryGetValue("phase", out string saved) ? saved : "start";

            // Left by an earlier round: the editor re-saves the changed files by itself once it has recompiled and been idle
            // for a few seconds. Nothing is saved before it, because a save would write the graphs out unreadable.
            if (phase == "start" || phase == "resave")
            {
                if (resavePending)
                {
                    data["phase"] = "resave";
                    if (!EditorApplication.isCompiling && !EditorApplication.isUpdating && EditorUtility.scriptCompilationFailed)
                        return Failed("The files changed by the update wait to be re-saved, which needs scripts that compile. Open the Console (Window > General > Console) and fix the errors, then run the setup again.");

                    return new RoutineTick(Status.InProgress, null);
                }

                phase = "apply";
            }

            object scan = routine.GetMethod("ScanProject", public_static).Invoke(null, new object[] { null });
            if (!HasRenameWork(scan))
            {
                RetireSaveGuard();
                int failed = ReadInt(data, "failed");
                return failed > 0
                    ? Failed($"{failed} file(s) could not be changed. The Console lists them; Virtuademy > Update routines > v2026.5 -> v2026.6 shows what is left.")
                    : new RoutineTick(Status.Done, ReadInt(data, "applyRounds") == 0 ? "nothing to change." : "applied.");
            }

            int rounds = ReadInt(data, "applyRounds");
            if (rounds >= max_apply_rounds)
                return Failed($"work is left after {max_apply_rounds} rounds. Open Virtuademy > Update routines > v2026.5 -> v2026.6 to see what.");

            // Written before the apply, which refreshes the asset database and queues a recompilation: the reload may come
            // as soon as the editor is free, and this call must be found again after it.
            data["applyRounds"] = (rounds + 1).ToString();
            data["phase"] = "resave";
            save();

            object options = Activator.CreateInstance(routine.GetNestedType("Options"));
            object result = routine.GetMethod("Apply", public_static).Invoke(null, new[] { scan, options, null, null });

            int failures = Count(result, "FailedFiles") + Count(result, "FailedPages") + Count(result, "FailedGuids");
            if (failures > 0)
                data["failed"] = (ReadInt(data, "failed") + failures).ToString();

            return new RoutineTick(Status.InProgress, null);
        }

        private static bool HasRenameWork(object scan)
        {
            Type type = scan.GetType();
            bool files = ((IEnumerable)type.GetField("Files").GetValue(scan)).Cast<object>()
                .Any(file => (bool)file.GetType().GetField("Selected").GetValue(file));

            return files
                   || CountOf(type.GetField("Pages").GetValue(scan)) > 0
                   || CountOf(type.GetField("Guids").GetValue(scan)) > 0
                   || (bool)type.GetField("HasLegacyFolders").GetValue(scan);
        }

        // The save guard retires itself once the routine has completed clean; the SDK's own command line asks it to as well.
        private static void RetireSaveGuard()
        {
            try
            {
                FindType(save_guard_type)?.GetMethod("RetireIfMigrated", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            }
            catch (TargetInvocationException ex)
            {
                UnityEngine.Debug.LogWarning("[Setup] The update routine's save guard could not be retired: " + (ex.InnerException ?? ex).Message);
            }
        }

        #endregion

        #region 2026.5

        private static RoutineTick TickMobile()
        {
            // It saves graphs, which nothing may do while the rename still has graph data to migrate.
            Type rename = FindType(rename_type);
            if (rename != null)
            {
                int blockers = CountOf(rename.GetMethod("FilesWithUnmigratedGraphData", public_static).Invoke(null, null));
                if (blockers > 0)
                    return Failed($"{blockers} file(s) still have Visual Scripting data that the v2026.5 -> v2026.6 routine renames: it has to finish first. Open Virtuademy > Update routines > v2026.5 -> v2026.6.");
            }

            Type mobile = FindType(mobile_type);
            MethodInfo scanMethod = mobile?.GetMethod("Scan", public_static);
            MethodInfo applyMethod = mobile?.GetMethod("Apply", public_static);
            if (scanMethod == null || applyMethod == null)
                return Failed("The routine's code is not loaded: the SDK's editor assembly did not compile.");

            int pending = NotConnected((Dictionary<string, object>)scanMethod.Invoke(null, null));
            if (pending == 0)
                return new RoutineTick(Status.Done, "nothing to change.");

            Dictionary<string, object> applied = (Dictionary<string, object>)applyMethod.Invoke(null, new object[] { new List<string>() });
            int failed = Convert.ToInt32(applied["failed"]);
            int still = NotConnected((Dictionary<string, object>)scanMethod.Invoke(null, null));

            if (failed > 0 || still > 0)
                return Failed($"{failed} node(s) could not be changed and {still} are still not connected. The Console has the details.");

            return new RoutineTick(Status.Done, $"connected the Mobile output of {pending} Platform Switch node(s).");
        }

        private static int NotConnected(Dictionary<string, object> scan)
            => ((IEnumerable)scan["occurrences"]).Cast<Dictionary<string, object>>().Count(occurrence => (string)occurrence["status"] == "NotConnected");

        #endregion

        #region Helpers

        private static RoutineTick Failed(string message, Exception exception = null)
        {
            if (exception != null)
                UnityEngine.Debug.LogException(exception);

            return new RoutineTick(Status.Failed, message);
        }

        private static Type FindType(string fullName)
            => AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(fullName, false)).FirstOrDefault(type => type != null);

        private static int CountOf(object list) => list is ICollection collection ? collection.Count : ((IEnumerable)list).Cast<object>().Count();

        private static int Count(object instance, string field)
            => instance.GetType().GetField(field) is FieldInfo info && info.GetValue(instance) is int value ? value : 0;

        private static int ReadInt(Dictionary<string, string> data, string key)
            => data.TryGetValue(key, out string value) && int.TryParse(value, out int number) ? number : 0;

        #endregion
    }
}
