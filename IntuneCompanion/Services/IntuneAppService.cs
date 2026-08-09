using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.RegularExpressions;
using IntuneCompanion.Models;
using Microsoft.Win32;

namespace IntuneCompanion.Services;

public static class IntuneAppService
{
    private const string ImeBasePath = @"SOFTWARE\Microsoft\IntuneManagementExtension";
    private const string Win32AppsPath = $@"{ImeBasePath}\Win32Apps";
    private const string ReportingPath = $@"{Win32AppsPath}\Reporting";
    private const string SideCarPath = $@"{ImeBasePath}\SideCarPolicies\StatusServiceReports";
    private const string EntDesktopMgmtPath = @"SOFTWARE\Microsoft\EnterpriseDesktopAppManagement";
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ZeroGuid = "00000000-0000-0000-0000-000000000000";
    private const string ImeServiceName = "IntuneManagementExtension";
    private const string ImeLogsFolder = @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs";

    /// <summary>
    /// Enumerates all Intune-managed apps from multiple registry sources.
    /// </summary>
    public static List<IntuneApp> GetInstalledApps()
    {
        var apps = new Dictionary<string, IntuneApp>(StringComparer.OrdinalIgnoreCase);

        // Primary source: Reporting path has ALL app IDs Intune tracks
        EnumerateFromReporting(apps);

        // Secondary: Win32Apps per-SID entries (may have display names)
        EnumerateWin32Apps(apps);

        // Tertiary: EnterpriseDesktopAppManagement for LOB/Store apps
        EnumerateStoreApps(apps);

        // Try to resolve GUIDs that have no display name yet
        ResolveDisplayNames(apps);

        return [.. apps.Values.OrderBy(a => a.DisplayName)];
    }

    /// <summary>
    /// Dumps raw registry data for all discovered app keys to a text file for diagnostics.
    /// </summary>
    public static string DumpRegistryDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== Intune Registry Diagnostics ({DateTime.Now}) ===");
        sb.AppendLine();

        // Dump Win32Apps structure
        sb.AppendLine($"--- {Win32AppsPath} ---");
        DumpKey(sb, Win32AppsPath, maxDepth: 3);

        sb.AppendLine();
        sb.AppendLine($"--- {ReportingPath} ---");
        DumpKey(sb, ReportingPath, maxDepth: 3);

        sb.AppendLine();
        sb.AppendLine($"--- {EntDesktopMgmtPath} ---");
        DumpKey(sb, EntDesktopMgmtPath, maxDepth: 3);

        var path = Path.Combine(Path.GetTempPath(), "IntuneCompanion_Diagnostics.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static void DumpKey(System.Text.StringBuilder sb, string keyPath, int maxDepth, int indent = 0)
    {
        if (maxDepth <= 0) return;
        var prefix = new string(' ', indent * 2);

        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        if (key is null)
        {
            sb.AppendLine($"{prefix}[NOT FOUND] {keyPath}");
            return;
        }

        sb.AppendLine($"{prefix}[KEY] {keyPath}");

        // Dump values
        foreach (var valName in key.GetValueNames())
        {
            var val = key.GetValue(valName);
            var display = val switch
            {
                string s when s.Length > 200 => s[..200] + "...",
                byte[] b => $"[binary {b.Length} bytes]",
                _ => val?.ToString() ?? "(null)"
            };
            sb.AppendLine($"{prefix}  {(string.IsNullOrEmpty(valName) ? "(Default)" : valName)} = {display}");
        }

        // Recurse into subkeys
        foreach (var subName in key.GetSubKeyNames())
        {
            DumpKey(sb, $@"{keyPath}\{subName}", maxDepth - 1, indent + 1);
        }
    }

    /// <summary>
    /// Removes all IME registry traces for the given app ID.
    /// </summary>
    public static void NukeApp(string appId, IntuneAppType appType, Action<string>? progress = null)
    {
        DeleteSubKey($@"{ReportingPath}\{ZeroGuid}", appId, progress);
        DeleteValue($@"{ReportingPath}\AppAuthority", appId, progress);
        DeleteMatchingSubKeys(SideCarPath, appId, progress);
        DeleteMatchingSubKeys(Win32AppsPath, appId, progress);
        DeleteSubKey($@"{Win32AppsPath}\OperationalState\{ZeroGuid}", appId, progress);
        DeleteGrsEntries(appId, progress);

        if (appType == IntuneAppType.Store)
        {
            DeleteMatchingMsiKeys(appId, progress);
        }
    }

    /// <summary>
    /// Attempts to uninstall the app by looking up its UninstallString in the Windows registry.
    /// Returns true if an uninstall command was found and executed.
    /// </summary>
    public static bool UninstallApp(string appId, string displayName, Action<string>? progress = null)
    {
        var uninstallCmd = FindUninstallString(appId, displayName);
        if (uninstallCmd is null)
        {
            progress?.Invoke($"No uninstall command found for '{displayName}' ({appId}).");
            return false;
        }

        progress?.Invoke($"Uninstalling '{displayName}' using: {uninstallCmd}");

        // Determine if it's an MSI uninstall (contains msiexec or product code GUID)
        string fileName;
        string arguments;

        if (uninstallCmd.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "msiexec.exe";
            // Extract args after msiexec, ensure quiet mode
            var args = Regex.Replace(uninstallCmd, @"(?i)msiexec(?:\.exe)?", "").Trim();
            if (!args.Contains("/qn", StringComparison.OrdinalIgnoreCase) &&
                !args.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
            {
                args += " /qn";
            }
            arguments = args;
        }
        else
        {
            // Generic EXE uninstaller
            if (uninstallCmd.StartsWith('"'))
            {
                var endQuote = uninstallCmd.IndexOf('"', 1);
                fileName = uninstallCmd[1..endQuote];
                arguments = uninstallCmd[(endQuote + 1)..].Trim();
            }
            else
            {
                var spaceIdx = uninstallCmd.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    fileName = uninstallCmd[..spaceIdx];
                    arguments = uninstallCmd[(spaceIdx + 1)..];
                }
                else
                {
                    fileName = uninstallCmd;
                    arguments = "";
                }
            }
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process.Start();
            process.WaitForExit(120_000);
            progress?.Invoke($"Uninstall exited with code {process.ExitCode}.");
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            progress?.Invoke($"Uninstall failed: {ex.Message}");
            return false;
        }
    }

    private static string? FindUninstallString(string appId, string displayName)
    {
        // Search both 64-bit and 32-bit uninstall registry paths
        var result = FindUninstallStringInPath(UninstallPath, appId, displayName);
        return result ?? FindUninstallStringInPath(UninstallPath32, appId, displayName);
    }

    private static string? FindUninstallStringInPath(string registryPath, string appId, string displayName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(registryPath);
        if (key is null) return null;

        foreach (var subName in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(subName);
            if (sub is null) continue;

            // Match by subkey name (product code) or display name
            var isMatch = subName.Equals(appId, StringComparison.OrdinalIgnoreCase) ||
                          subName.Contains(appId, StringComparison.OrdinalIgnoreCase);

            if (!isMatch)
            {
                var regDisplayName = sub.GetValue("DisplayName") as string;
                if (regDisplayName is not null &&
                    regDisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase))
                {
                    isMatch = true;
                }
            }

            if (isMatch)
            {
                // Prefer QuietUninstallString, fallback to UninstallString
                var quiet = sub.GetValue("QuietUninstallString") as string;
                if (!string.IsNullOrWhiteSpace(quiet)) return quiet;

                var standard = sub.GetValue("UninstallString") as string;
                if (!string.IsNullOrWhiteSpace(standard)) return standard;
            }
        }

        return null;
    }

    /// <summary>
    /// Stops the IME service, clears logs, and starts the service again.
    /// </summary>
    public static void RestartImeService(Action<string>? progress = null)
    {
        progress?.Invoke("Stopping IME service...");
        using var sc = new ServiceController(ImeServiceName);

        try
        {
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException)
        {
            progress?.Invoke("IME service not found — skipping service restart.");
            return;
        }

        progress?.Invoke("Deleting IME logs...");
        if (Directory.Exists(ImeLogsFolder))
        {
            foreach (var log in Directory.EnumerateFiles(ImeLogsFolder, "*.log"))
            {
                try { File.Delete(log); } catch { /* best effort */ }
            }
        }

        progress?.Invoke("Starting IME service...");
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    // ── enumeration helpers ──────────────────────────────────────────

    /// <summary>
    /// Enumerates from the Reporting path — this is the most complete source.
    /// All apps (Win32, Store, MSI) that Intune tracks appear here.
    /// </summary>
    private static void EnumerateFromReporting(Dictionary<string, IntuneApp> apps)
    {
        using var reportKey = Registry.LocalMachine.OpenSubKey(ReportingPath);
        if (reportKey is null) return;

        foreach (var sidOrContext in reportKey.GetSubKeyNames())
        {
            if (sidOrContext.Equals("AppAuthority", StringComparison.OrdinalIgnoreCase))
                continue;

            using var contextKey = reportKey.OpenSubKey(sidOrContext);
            if (contextKey is null) continue;

            foreach (var appId in contextKey.GetSubKeyNames())
            {
                if (apps.ContainsKey(appId))
                    continue;

                var appType = DetectAppType(contextKey, appId);

                apps[appId] = new IntuneApp
                {
                    AppId = appId,
                    DisplayName = appId, // Will be resolved later
                    UserSid = sidOrContext,
                    AppType = appType
                };
            }
        }
    }

    private static void EnumerateWin32Apps(Dictionary<string, IntuneApp> apps)
    {
        using var win32Key = Registry.LocalMachine.OpenSubKey(Win32AppsPath);
        if (win32Key is null) return;

        foreach (var sidName in win32Key.GetSubKeyNames())
        {
            if (sidName.Equals("Reporting", StringComparison.OrdinalIgnoreCase) ||
                sidName.Equals("OperationalState", StringComparison.OrdinalIgnoreCase))
                continue;

            using var sidKey = win32Key.OpenSubKey(sidName);
            if (sidKey is null) continue;

            foreach (var rawAppId in sidKey.GetSubKeyNames())
            {
                if (!IsGuidLike(rawAppId))
                    continue;

                var appId = StripSuffix(rawAppId);
                var displayName = TryGetDisplayName(sidKey, rawAppId);

                if (apps.TryGetValue(appId, out var existing))
                {
                    if (displayName is not null && existing.DisplayName == existing.AppId)
                    {
                        apps[appId] = existing with { DisplayName = displayName, UserSid = sidName };
                    }
                }
                else
                {
                    apps[appId] = new IntuneApp
                    {
                        AppId = appId,
                        DisplayName = displayName ?? appId,
                        UserSid = sidName,
                        AppType = IntuneAppType.Win32
                    };
                }
            }
        }
    }

    private static void EnumerateStoreApps(Dictionary<string, IntuneApp> apps)
    {
        using var edmKey = Registry.LocalMachine.OpenSubKey(EntDesktopMgmtPath);
        if (edmKey is null) return;

        foreach (var sidName in edmKey.GetSubKeyNames())
        {
            var msiPath = $@"{EntDesktopMgmtPath}\{sidName}\MSI";
            using var msiKey = Registry.LocalMachine.OpenSubKey(msiPath);
            if (msiKey is null) continue;

            foreach (var appId in msiKey.GetSubKeyNames())
            {
                using var appKey = msiKey.OpenSubKey(appId);
                var displayName = TryGetStoreDisplayName(appKey);

                if (apps.TryGetValue(appId, out var existing))
                {
                    if (displayName is not null && existing.DisplayName == existing.AppId)
                    {
                        apps[appId] = existing with { DisplayName = displayName, AppType = IntuneAppType.Store };
                    }
                }
                else
                {
                    apps[appId] = new IntuneApp
                    {
                        AppId = appId,
                        DisplayName = displayName ?? appId,
                        UserSid = sidName,
                        AppType = IntuneAppType.Store
                    };
                }
            }
        }
    }

    /// <summary>
    /// For apps still showing their GUID as display name, try:
    /// 1. IME log files (contain "id = {guid}, name = App Name")
    /// 2. Windows Uninstall registry
    /// </summary>
    private static void ResolveDisplayNames(Dictionary<string, IntuneApp> apps)
    {
        var unresolved = apps.Values.Where(a => a.DisplayName == a.AppId).ToList();
        if (unresolved.Count == 0) return;

        // 1. Parse IME logs for app names
        var logNames = ParseAppNamesFromLogs();
        foreach (var app in unresolved)
        {
            if (logNames.TryGetValue(app.AppId, out var name))
            {
                apps[app.AppId] = app with { DisplayName = name };
            }
        }

        // 2. Fallback: Windows Uninstall registry
        unresolved = apps.Values.Where(a => a.DisplayName == a.AppId).ToList();
        if (unresolved.Count == 0) return;

        var uninstallNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectUninstallNames(UninstallPath, uninstallNames);
        CollectUninstallNames(UninstallPath32, uninstallNames);

        foreach (var app in unresolved)
        {
            if (uninstallNames.TryGetValue(app.AppId, out var name2))
            {
                apps[app.AppId] = app with { DisplayName = name2 };
            }
        }
    }

    /// <summary>
    /// Parses IME log files for lines containing app id-to-name mappings.
    /// Patterns found in logs:
    ///   "app with id = {guid}, name = Some App"
    ///   "ExecManager: processing app with id: {guid}, name: Some App"
    ///   "id: {guid}, effectiveName: Some App"
    /// </summary>
    private static Dictionary<string, string> ParseAppNamesFromLogs()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ImeLogsFolder)) return names;

        // Match patterns like: id = {guid}, name = AppName  OR  id: {guid}, name: AppName
        var pattern = new Regex(
            @"(?:id\s*[:=]\s*)([0-9a-f\-]{36})(?:.*?)(?:name|effectiveName)\s*[:=]\s*(.+?)(?:,|\.|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(ImeLogsFolder, "*.log"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var match = pattern.Match(line);
                    if (match.Success)
                    {
                        var appId = match.Groups[1].Value;
                        var appName = match.Groups[2].Value.Trim();
                        if (!string.IsNullOrWhiteSpace(appName) && !names.ContainsKey(appId))
                        {
                            names[appId] = appName;
                        }
                    }
                }
            }
            catch { /* log may be locked */ }
        }

        return names;
    }

    private static void CollectUninstallNames(string path, Dictionary<string, string> results)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        if (key is null) return;

        foreach (var subName in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(subName);
            if (sub is null) continue;

            var displayName = sub.GetValue("DisplayName") as string;
            if (!string.IsNullOrWhiteSpace(displayName) && !results.ContainsKey(subName))
            {
                results[subName] = displayName;
            }
        }
    }

    private static IntuneAppType DetectAppType(RegistryKey contextKey, string appId)
    {
        using var appKey = contextKey.OpenSubKey(appId);
        if (appKey is null) return IntuneAppType.Win32;

        var appType = appKey.GetValue("AppType") as string;
        if (appType is not null && appType.Contains("Store", StringComparison.OrdinalIgnoreCase))
            return IntuneAppType.Store;

        return IntuneAppType.Win32;
    }

    private static string? TryGetStoreDisplayName(RegistryKey? appKey)
    {
        if (appKey is null) return null;
        foreach (var name in new[] { "DisplayName", "PackageFamilyName", "ProductCode" })
        {
            if (appKey.GetValue(name) is string val && !string.IsNullOrWhiteSpace(val))
                return val;
        }
        return null;
    }

    /// <summary>
    /// Returns true if no registry traces remain for the given app ID.
    /// </summary>
    public static bool VerifyAppRemoved(string appId)
    {
        // Check Reporting path
        using var reportKey = Registry.LocalMachine.OpenSubKey($@"{ReportingPath}\{ZeroGuid}\{appId}");
        if (reportKey is not null) return false;

        // Check Win32Apps per-SID
        using var win32Key = Registry.LocalMachine.OpenSubKey(Win32AppsPath);
        if (win32Key is not null)
        {
            foreach (var sidName in win32Key.GetSubKeyNames())
            {
                if (sidName.Equals("Reporting", StringComparison.OrdinalIgnoreCase) ||
                    sidName.Equals("OperationalState", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var sidKey = Registry.LocalMachine.OpenSubKey($@"{Win32AppsPath}\{sidName}");
                if (sidKey is null) continue;

                foreach (var subName in sidKey.GetSubKeyNames())
                {
                    var stripped = StripSuffix(subName);
                    if (stripped.Equals(appId, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
        }

        // Check SideCarPolicies
        using var sideCarKey = Registry.LocalMachine.OpenSubKey(SideCarPath);
        if (sideCarKey is not null)
        {
            foreach (var childName in sideCarKey.GetSubKeyNames())
            {
                using var child = Registry.LocalMachine.OpenSubKey($@"{SideCarPath}\{childName}\{appId}");
                if (child is not null) return false;
            }
        }

        return true;
    }

    // ── nuke helpers ─────────────────────────────────────────────────

    private static void DeleteMatchingMsiKeys(string appId, Action<string>? progress)
    {
        using var edmKey = Registry.LocalMachine.OpenSubKey(EntDesktopMgmtPath, writable: false);
        if (edmKey is null) return;

        foreach (var sidName in edmKey.GetSubKeyNames())
        {
            var msiPath = $@"{EntDesktopMgmtPath}\{sidName}\MSI";
            using var msiKey = Registry.LocalMachine.OpenSubKey(msiPath, writable: true);
            if (msiKey is null) continue;

            if (msiKey.GetSubKeyNames().Any(n => n.Equals(appId, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    msiKey.DeleteSubKeyTree(appId, throwOnMissingSubKey: false);
                    progress?.Invoke($"Deleted store app key {msiPath}\\{appId}");
                }
                catch { /* best effort */ }
            }
        }
    }

    // ── common helpers ───────────────────────────────────────────────

    /// <summary>
    /// Accepts GUIDs with optional _N suffix (e.g., "2544200f-..._1").
    /// </summary>
    private static bool IsGuidLike(string value)
    {
        var raw = StripSuffix(value);
        return Guid.TryParse(raw, out _);
    }

    /// <summary>
    /// Strips the _N suffix Intune appends to app subkeys.
    /// </summary>
    private static string StripSuffix(string value)
    {
        var idx = value.LastIndexOf('_');
        if (idx > 0 && int.TryParse(value.AsSpan(idx + 1), out _))
            return value[..idx];
        return value;
    }

    private static string? TryGetDisplayName(RegistryKey parentKey, string appId)
    {
        using var appKey = parentKey.OpenSubKey(appId);
        if (appKey is null) return null;

        // Try plain DisplayName value first
        if (appKey.GetValue("DisplayName") is string plain && !string.IsNullOrWhiteSpace(plain))
            return plain;

        // Try extracting from JSON state messages stored by IME
        return TryExtractNameFromJsonValues(appKey);
    }

    /// <summary>
    /// IME stores app metadata as JSON in registry values like
    /// ComplianceStateMessage, EnforcementStateMessage, etc.
    /// These JSON blobs contain an "ApplicationName" or "AppName" field.
    /// </summary>
    private static string? TryExtractNameFromJsonValues(RegistryKey appKey)
    {
        foreach (var valueName in appKey.GetValueNames())
        {
            if (appKey.GetValue(valueName) is not string json)
                continue;

            // Quick check before attempting JSON parse
            if (!json.Contains("Name", StringComparison.OrdinalIgnoreCase) || !json.StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Try known property names used by Intune
                foreach (var propName in new[] { "ApplicationName", "AppName", "DisplayName", "Name" })
                {
                    if (root.TryGetProperty(propName, out var prop) &&
                        prop.ValueKind == JsonValueKind.String)
                    {
                        var name = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                            return name;
                    }
                }
            }
            catch { /* not valid JSON, skip */ }
        }

        return null;
    }

    private static void DeleteSubKey(string parentPath, string subKeyName, Action<string>? progress)
    {
        using var parent = Registry.LocalMachine.OpenSubKey(parentPath, writable: true);
        if (parent is null) return;
        try
        {
            parent.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
            progress?.Invoke($"Deleted {parentPath}\\{subKeyName}");
        }
        catch (UnauthorizedAccessException)
        {
            progress?.Invoke($"Access denied: {parentPath}\\{subKeyName} (run as Administrator)");
        }
        catch { /* best effort */ }
    }

    private static void DeleteValue(string keyPath, string valueName, Action<string>? progress)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
        if (key is null) return;
        try
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            progress?.Invoke($"Deleted value {valueName} from {keyPath}");
        }
        catch { /* best effort */ }
    }

    private static void DeleteMatchingSubKeys(string parentPath, string appId, Action<string>? progress)
    {
        using var parent = Registry.LocalMachine.OpenSubKey(parentPath, writable: false);
        if (parent is null) return;

        foreach (var childName in parent.GetSubKeyNames())
        {
            var childPath = $@"{parentPath}\{childName}";
            using var child = Registry.LocalMachine.OpenSubKey(childPath, writable: true);
            if (child is null) continue;

            var matchingKeys = child.GetSubKeyNames()
                .Where(n => n.Equals(appId, StringComparison.OrdinalIgnoreCase) ||
                            StripSuffix(n).Equals(appId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var matchedKey in matchingKeys)
            {
                try
                {
                    child.DeleteSubKeyTree(matchedKey, throwOnMissingSubKey: false);
                    progress?.Invoke($"Deleted {childPath}\\{matchedKey}");
                }
                catch (UnauthorizedAccessException)
                {
                    progress?.Invoke($"Access denied: {childPath}\\{matchedKey} (run as Administrator)");
                }
                catch { /* best effort */ }
            }
        }
    }

    private static void DeleteGrsEntries(string appId, Action<string>? progress)
    {
        using var win32Key = Registry.LocalMachine.OpenSubKey(Win32AppsPath, writable: false);
        if (win32Key is null) return;

        foreach (var sidName in win32Key.GetSubKeyNames())
        {
            var grsPath = $@"{Win32AppsPath}\{sidName}\GRS";
            using var grsKey = Registry.LocalMachine.OpenSubKey(grsPath, writable: false);
            if (grsKey is null) continue;

            foreach (var hashName in grsKey.GetSubKeyNames())
            {
                var hashPath = $@"{grsPath}\{hashName}";
                using var hashKey = Registry.LocalMachine.OpenSubKey(hashPath, writable: false);
                if (hashKey is null) continue;

                if (hashKey.GetValueNames().Any(v => v.Equals(appId, StringComparison.OrdinalIgnoreCase)))
                {
                    using var parentWritable = Registry.LocalMachine.OpenSubKey(grsPath, writable: true);
                    parentWritable?.DeleteSubKeyTree(hashName, throwOnMissingSubKey: false);
                    progress?.Invoke($"Deleted GRS entry {hashPath}");
                    return;
                }
            }
        }
    }

    private static void RunPowerShell(string script)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.Start();
        process.WaitForExit(60_000);
    }
}
