using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Virtuademy.SDK.Environments.Setup.Editor
{
    /// <summary>One connection of this computer, as the SDK summarises it: no token, no secret.</summary>
    internal sealed class SetupConnection
    {
        internal string Key { get; private set; }
        internal string TenantLabel { get; private set; }
        internal string Environment { get; private set; }
        internal string PlatformVersion { get; private set; }

        /// <summary>The platform of the connection's app (<c>CreatorKit</c> for an app that environments are published to); null when the SDK did not say.</summary>
        internal string Platform { get; private set; }
        internal string User { get; private set; }
        internal bool LoggedIn { get; private set; }

        /// <summary>False only when the SDK names another platform: an app that is not a CreatorKit one cannot receive environments.</summary>
        internal bool IsCreatorKitApp => string.IsNullOrEmpty(Platform) || string.Equals(Platform, "CreatorKit", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The kind of the connection's environment, from the SDK's environment names (<c>Production</c>, <c>PreProduction</c>,
        /// <c>Sandbox</c>): <c>prod</c>, <c>prep</c>, <c>sandbox</c>, or null for a name it does not know.
        /// </summary>
        internal string EnvironmentKind =>
            string.Equals(Environment, "Production", StringComparison.OrdinalIgnoreCase) ? "prod"
            : string.Equals(Environment, "PreProduction", StringComparison.OrdinalIgnoreCase) ? "prep"
            : string.Equals(Environment, "Sandbox", StringComparison.OrdinalIgnoreCase) ? "sandbox"
            : null;

        internal static SetupConnection From(JToken summary) => new()
        {
            Key = (string)summary["key"],
            TenantLabel = (string)summary["tenantLabel"],
            Environment = (string)summary["environment"],
            PlatformVersion = (string)summary["platformVersion"],
            Platform = (string)summary["platform"],
            User = (string)summary["user"],
            LoggedIn = (bool?)summary["loggedIn"] ?? false,
        };
    }

    /// <summary>What a <see cref="SetupConnections"/> call came back with: the bridge's <c>ok</c>, <c>code</c>, <c>message</c> and the connections it names.</summary>
    internal sealed class SetupBridgeResult
    {
        internal bool Ok { get; private set; }
        internal string Code { get; private set; }
        internal string Message { get; private set; }

        /// <summary>The connections an import added or refreshed; empty for the other calls.</summary>
        internal List<SetupConnection> Connections { get; private set; } = new();

        internal static SetupBridgeResult Failed(string code, string message) => new() { Ok = false, Code = code, Message = message };

        internal static SetupBridgeResult Parse(string json)
        {
            JObject result = JObject.Parse(json);
            SetupBridgeResult parsed = new()
            {
                Ok = (bool?)result["ok"] ?? false,
                Code = (string)result["code"],
                Message = (string)result["message"],
            };

            if (result["connections"] is JArray summaries)
                parsed.Connections = summaries.Select(SetupConnection.From).ToList();
            else if (result["connection"] is JObject single)
                parsed.Connections = new List<SetupConnection> { SetupConnection.From(single) };

            return parsed;
        }

        /// <summary>What to tell the person: the bridge's own message, or a sentence for the codes that come without one.</summary>
        internal string Describe()
        {
            if (!string.IsNullOrEmpty(Message))
                return Message;

            return Code switch
            {
                "busy" => "Another operation on the connections is still running. Wait for it to finish.",
                "login-timeout" => "The login was not finished in time. Try again.",
                "not-found" => "That connection is no longer on this computer.",
                "worker" => "This process cannot reach the connections.",
                _ => $"It did not work ({Code ?? "no code"}). The Console may say more.",
            };
        }
    }

    /// <summary>
    /// The computer's connections, for the wizard's Connection page. They belong to the SDK (SDK-Core, <c>SetupBridge</c>), which this
    /// package installs and so cannot reference: every call goes by name, through reflection, with strings in and out, and only once
    /// SDK-Core is installed and compiled. Before that the list is read from <c>%USERPROFILE%\.virtuademy\sdk\status.json</c>, a file
    /// the SDK keeps with no secret in it, and nothing else is possible: the page says what is not available.
    /// </summary>
    internal static class SetupConnections
    {
        private const string bridge_type_name = "Virtuademy.SDK.TenantConfiguration.Editor.SetupBridge, Virtuademy.SDK.Core.Editor";

        // The contract this code was written against: SetupBridge.Version. Another number means a member or a JSON shape changed.
        private const int supported_bridge_version = 1;

        private const string status_file_name = "status.json";

        private static Type FindBridge() => Type.GetType(bridge_type_name, false);

        /// <summary>Whether <c>SetupBridge</c> is there and speaks the version this code knows. <paramref name="why"/> says what is missing when it is not.</summary>
        internal static bool IsAvailable(out string why)
        {
            Type bridge = FindBridge();
            if (bridge == null)
            {
                why = "The SDK's connection support is not installed (it needs Virtuademy 2026.7 or later).";
                return false;
            }

            object version = bridge.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue();
            if (version is not int number || number != supported_bridge_version)
            {
                why = $"The SDK's connection support is version {version ?? "unknown"}, and this setup knows version {supported_bridge_version}. Update the setup package.";
                return false;
            }

            why = null;
            return true;
        }

        /// <summary>
        /// The computer's connections: through the bridge when it is available, else from the status file. <paramref name="fromBridge"/> says
        /// which; <paramref name="error"/> is set when neither could be read (an empty list then means "unknown").
        /// </summary>
        internal static List<SetupConnection> List(out bool fromBridge, out string error)
        {
            fromBridge = false;
            error = null;

            if (IsAvailable(out _))
            {
                try
                {
                    MethodInfo method = FindBridge().GetMethod("Connections", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    string json = (string)method.Invoke(null, null);
                    fromBridge = true;
                    return JArray.Parse(json).Select(SetupConnection.From).ToList();
                }
                catch (Exception ex) when (ex is TargetInvocationException or JsonException or NullReferenceException or InvalidCastException)
                {
                    error = (ex.InnerException ?? ex).Message;
                    return new List<SetupConnection>();
                }
            }

            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".virtuademy", "sdk", status_file_name);
            if (!File.Exists(path))
                return new List<SetupConnection>();

            try
            {
                return (JObject.Parse(File.ReadAllText(path))["connections"] as JArray)?.Select(SetupConnection.From).ToList() ?? new List<SetupConnection>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidCastException)
            {
                error = ex.Message;
                return new List<SetupConnection>();
            }
        }

        internal static Task<SetupBridgeResult> ImportAsync(string path) => CallAsync("ImportConfigurationFileAsync", path);

        internal static Task<SetupBridgeResult> LoginAsync(string connectionKey) => CallAsync("LoginAsync", connectionKey);

        internal static Task<SetupBridgeResult> LogoutAsync(string connectionKey) => CallAsync("LogoutAsync", connectionKey);

        /// <summary>Calls one <c>Task&lt;string&gt;</c> member of the bridge. On the editor's main thread, like the bridge asks. Never throws.</summary>
        private static async Task<SetupBridgeResult> CallAsync(string member, string argument)
        {
            if (!IsAvailable(out string why))
                return SetupBridgeResult.Failed("unavailable", why);

            try
            {
                MethodInfo method = FindBridge().GetMethod(member, BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (method == null)
                    return SetupBridgeResult.Failed("unavailable", $"The SDK's connection support has no {member}.");

                string json = await (Task<string>)method.Invoke(null, new object[] { argument });
                return SetupBridgeResult.Parse(json);
            }
            catch (Exception ex) when (ex is TargetInvocationException or JsonException or InvalidCastException or NullReferenceException or InvalidOperationException)
            {
                return SetupBridgeResult.Failed("error", (ex.InnerException ?? ex).Message);
            }
        }
    }
}
