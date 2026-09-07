// Secure Credential Store - Uses OS-native keychain/credential manager
// API keys are NEVER stored in Unity project files, EditorPrefs, or PlayerPrefs

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Splatter.Editor.Security
{
    /// <summary>
    /// Secure credential storage using OS-native keychains.
    /// - Windows: Credential Manager
    /// - macOS: Keychain
    /// - Linux: Secret Service (libsecret)
    /// </summary>
    public static class SecureCredentialStore
    {
        private const string ServicePrefix = "splatter-ai";

        /// <summary>
        /// Store a credential securely in the OS keychain.
        /// </summary>
        public static bool SetCredential(string providerId, string apiKey)
        {
            if (string.IsNullOrEmpty(providerId) || string.IsNullOrEmpty(apiKey))
                return false;

            var target = $"{ServicePrefix}/{providerId}";

            try
            {
#if UNITY_EDITOR_WIN
                return WindowsCredentialManager.Write(target, apiKey);
#elif UNITY_EDITOR_OSX
                return MacOSKeychain.Write(target, apiKey);
#elif UNITY_EDITOR_LINUX
                return LinuxSecretService.Write(target, apiKey);
#else
                UnityEngine.Debug.LogWarning("[Splatter] Secure credential storage not supported on this platform");
                return false;
#endif
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[Splatter] Failed to store credential: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Retrieve a credential from the OS keychain.
        /// </summary>
        public static string GetCredential(string providerId)
        {
            if (string.IsNullOrEmpty(providerId))
                return null;

            var target = $"{ServicePrefix}/{providerId}";

            try
            {
#if UNITY_EDITOR_WIN
                return WindowsCredentialManager.Read(target);
#elif UNITY_EDITOR_OSX
                return MacOSKeychain.Read(target);
#elif UNITY_EDITOR_LINUX
                return LinuxSecretService.Read(target);
#else
                return null;
#endif
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[Splatter] Failed to read credential: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Delete a credential from the OS keychain.
        /// </summary>
        public static bool DeleteCredential(string providerId)
        {
            if (string.IsNullOrEmpty(providerId))
                return false;

            var target = $"{ServicePrefix}/{providerId}";

            try
            {
#if UNITY_EDITOR_WIN
                return WindowsCredentialManager.Delete(target);
#elif UNITY_EDITOR_OSX
                return MacOSKeychain.Delete(target);
#elif UNITY_EDITOR_LINUX
                return LinuxSecretService.Delete(target);
#else
                return false;
#endif
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[Splatter] Failed to delete credential: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if a credential exists without retrieving it.
        /// </summary>
        public static bool HasCredential(string providerId)
        {
            return !string.IsNullOrEmpty(GetCredential(providerId));
        }

#if UNITY_EDITOR_WIN
        /// <summary>
        /// Windows Credential Manager implementation using P/Invoke.
        /// </summary>
        private static class WindowsCredentialManager
        {
            [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool CredWrite([In] ref CREDENTIAL credential, [In] uint flags);

            [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

            [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern bool CredDelete(string target, uint type, uint flags);

            [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
            private static extern bool CredFree([In] IntPtr credential);

            private const uint CRED_TYPE_GENERIC = 1;
            private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct CREDENTIAL
            {
                public uint Flags;
                public uint Type;
                public string TargetName;
                public string Comment;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
                public uint CredentialBlobSize;
                public IntPtr CredentialBlob;
                public uint Persist;
                public uint AttributeCount;
                public IntPtr Attributes;
                public string TargetAlias;
                public string UserName;
            }

            public static bool Write(string target, string secret)
            {
                var secretBytes = Encoding.Unicode.GetBytes(secret);
                var credentialBlob = Marshal.AllocHGlobal(secretBytes.Length);

                try
                {
                    Marshal.Copy(secretBytes, 0, credentialBlob, secretBytes.Length);

                    var credential = new CREDENTIAL
                    {
                        Type = CRED_TYPE_GENERIC,
                        TargetName = target,
                        CredentialBlobSize = (uint)secretBytes.Length,
                        CredentialBlob = credentialBlob,
                        Persist = CRED_PERSIST_LOCAL_MACHINE,
                        UserName = "SplatterAI"
                    };

                    return CredWrite(ref credential, 0);
                }
                finally
                {
                    Marshal.FreeHGlobal(credentialBlob);
                }
            }

            public static string Read(string target)
            {
                if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var credentialPtr))
                    return null;

                try
                {
                    var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                    if (credential.CredentialBlobSize == 0)
                        return null;

                    var secretBytes = new byte[credential.CredentialBlobSize];
                    Marshal.Copy(credential.CredentialBlob, secretBytes, 0, (int)credential.CredentialBlobSize);
                    return Encoding.Unicode.GetString(secretBytes);
                }
                finally
                {
                    CredFree(credentialPtr);
                }
            }

            public static bool Delete(string target)
            {
                return CredDelete(target, CRED_TYPE_GENERIC, 0);
            }
        }
#endif

#if UNITY_EDITOR_OSX
        /// <summary>
        /// macOS Keychain implementation using security command.
        /// </summary>
        private static class MacOSKeychain
        {
            // Keep the existing Keychain identifier so saved credentials survive package renames.
            private const string ServiceName = "com.splatter.ai";

            public static bool Write(string account, string secret)
            {
                // Delete existing first (security add-generic-password fails if exists)
                Delete(account);

                var args = $"add-generic-password -a \"{EscapeArg(account)}\" -s \"{ServiceName}\" -w \"{EscapeArg(secret)}\" -U";
                return RunSecurity(args) == 0;
            }

            public static string Read(string account)
            {
                var args = $"find-generic-password -a \"{EscapeArg(account)}\" -s \"{ServiceName}\" -w";
                var (exitCode, output) = RunSecurityWithOutput(args);
                return exitCode == 0 ? output?.Trim() : null;
            }

            public static bool Delete(string account)
            {
                var args = $"delete-generic-password -a \"{EscapeArg(account)}\" -s \"{ServiceName}\"";
                RunSecurity(args); // Ignore errors (may not exist)
                return true;
            }

            private static string EscapeArg(string arg)
            {
                return arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
            }

            private static int RunSecurity(string args)
            {
                var psi = new ProcessStartInfo("security", args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                process?.WaitForExit(5000);
                return process?.ExitCode ?? -1;
            }

            private static (int exitCode, string output) RunSecurityWithOutput(string args)
            {
                var psi = new ProcessStartInfo("security", args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                var output = process?.StandardOutput.ReadToEnd();
                process?.WaitForExit(5000);
                return (process?.ExitCode ?? -1, output);
            }
        }
#endif

#if UNITY_EDITOR_LINUX
        /// <summary>
        /// Linux Secret Service implementation using secret-tool.
        /// Falls back to encrypted file if secret-tool unavailable.
        /// </summary>
        private static class LinuxSecretService
        {
            private const string ServiceAttribute = "service";
            private const string ServiceValue = "splatter-ai";
            private const string AccountAttribute = "account";

            public static bool Write(string account, string secret)
            {
                // Try secret-tool first
                if (HasSecretTool())
                {
                    var args = $"store --label=\"Splatter AI: {account}\" {ServiceAttribute} {ServiceValue} {AccountAttribute} \"{EscapeArg(account)}\"";
                    return RunSecretToolWithInput(args, secret) == 0;
                }

                // Fallback to encrypted file storage
                return EncryptedFileStore.Write(account, secret);
            }

            public static string Read(string account)
            {
                if (HasSecretTool())
                {
                    var args = $"lookup {ServiceAttribute} {ServiceValue} {AccountAttribute} \"{EscapeArg(account)}\"";
                    var (exitCode, output) = RunSecretToolWithOutput(args);
                    return exitCode == 0 ? output?.Trim() : null;
                }

                return EncryptedFileStore.Read(account);
            }

            public static bool Delete(string account)
            {
                if (HasSecretTool())
                {
                    var args = $"clear {ServiceAttribute} {ServiceValue} {AccountAttribute} \"{EscapeArg(account)}\"";
                    RunSecretTool(args);
                    return true;
                }

                return EncryptedFileStore.Delete(account);
            }

            private static bool? _hasSecretTool;
            private static bool HasSecretTool()
            {
                if (_hasSecretTool.HasValue) return _hasSecretTool.Value;

                try
                {
                    var psi = new ProcessStartInfo("which", "secret-tool")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(psi);
                    process?.WaitForExit(2000);
                    _hasSecretTool = process?.ExitCode == 0;
                }
                catch
                {
                    _hasSecretTool = false;
                }

                return _hasSecretTool.Value;
            }

            private static string EscapeArg(string arg)
            {
                return arg.Replace("\\", "\\\\").Replace("\"", "\\\"");
            }

            private static int RunSecretTool(string args)
            {
                var psi = new ProcessStartInfo("secret-tool", args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                process?.WaitForExit(5000);
                return process?.ExitCode ?? -1;
            }

            private static int RunSecretToolWithInput(string args, string input)
            {
                var psi = new ProcessStartInfo("secret-tool", args)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return -1;

                process.StandardInput.Write(input);
                process.StandardInput.Close();
                process.WaitForExit(5000);
                return process.ExitCode;
            }

            private static (int exitCode, string output) RunSecretToolWithOutput(string args)
            {
                var psi = new ProcessStartInfo("secret-tool", args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                var output = process?.StandardOutput.ReadToEnd();
                process?.WaitForExit(5000);
                return (process?.ExitCode ?? -1, output);
            }

            /// <summary>
            /// Fallback encrypted file storage for Linux without secret-tool.
            /// Uses machine-specific key derivation.
            /// </summary>
            private static class EncryptedFileStore
            {
                private static string StorePath => System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config", "splatter-ai", "credentials");

                public static bool Write(string account, string secret)
                {
                    try
                    {
                        var dir = System.IO.Path.GetDirectoryName(StorePath);
                        if (!System.IO.Directory.Exists(dir))
                            System.IO.Directory.CreateDirectory(dir);

                        // Simple XOR obfuscation with machine ID (not truly secure, but better than plaintext)
                        var key = GetMachineKey();
                        var encrypted = XorEncrypt(secret, key);
                        var data = LoadData();
                        data[account] = Convert.ToBase64String(Encoding.UTF8.GetBytes(encrypted));
                        SaveData(data);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }

                public static string Read(string account)
                {
                    try
                    {
                        var data = LoadData();
                        if (!data.TryGetValue(account, out var encoded))
                            return null;

                        var encrypted = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                        var key = GetMachineKey();
                        return XorEncrypt(encrypted, key);
                    }
                    catch
                    {
                        return null;
                    }
                }

                public static bool Delete(string account)
                {
                    try
                    {
                        var data = LoadData();
                        data.Remove(account);
                        SaveData(data);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                }

                private static System.Collections.Generic.Dictionary<string, string> LoadData()
                {
                    if (!System.IO.File.Exists(StorePath))
                        return new System.Collections.Generic.Dictionary<string, string>();

                    var json = System.IO.File.ReadAllText(StorePath);
                    return JsonUtility.FromJson<CredentialData>(json)?.ToDict()
                           ?? new System.Collections.Generic.Dictionary<string, string>();
                }

                private static void SaveData(System.Collections.Generic.Dictionary<string, string> data)
                {
                    var json = JsonUtility.ToJson(CredentialData.FromDict(data));
                    System.IO.File.WriteAllText(StorePath, json);
                    // Set restrictive permissions
                    try { Process.Start("chmod", $"600 \"{StorePath}\"")?.WaitForExit(1000); } catch { }
                }

                private static string GetMachineKey()
                {
                    // Use machine-id as key derivation source
                    try
                    {
                        if (System.IO.File.Exists("/etc/machine-id"))
                            return System.IO.File.ReadAllText("/etc/machine-id").Trim();
                        if (System.IO.File.Exists("/var/lib/dbus/machine-id"))
                            return System.IO.File.ReadAllText("/var/lib/dbus/machine-id").Trim();
                    }
                    catch { }
                    return Environment.MachineName + Environment.UserName;
                }

                private static string XorEncrypt(string text, string key)
                {
                    var result = new StringBuilder(text.Length);
                    for (int i = 0; i < text.Length; i++)
                        result.Append((char)(text[i] ^ key[i % key.Length]));
                    return result.ToString();
                }

                [Serializable]
                private class CredentialData
                {
                    public string[] keys = Array.Empty<string>();
                    public string[] values = Array.Empty<string>();

                    public System.Collections.Generic.Dictionary<string, string> ToDict()
                    {
                        var dict = new System.Collections.Generic.Dictionary<string, string>();
                        for (int i = 0; i < Math.Min(keys.Length, values.Length); i++)
                            dict[keys[i]] = values[i];
                        return dict;
                    }

                    public static CredentialData FromDict(System.Collections.Generic.Dictionary<string, string> dict)
                    {
                        var data = new CredentialData
                        {
                            keys = new string[dict.Count],
                            values = new string[dict.Count]
                        };
                        int i = 0;
                        foreach (var kvp in dict)
                        {
                            data.keys[i] = kvp.Key;
                            data.values[i] = kvp.Value;
                            i++;
                        }
                        return data;
                    }
                }
            }
        }
#endif
    }
}
