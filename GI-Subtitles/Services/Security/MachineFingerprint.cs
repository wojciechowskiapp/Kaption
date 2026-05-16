using System;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using GI_Subtitles.Common;

namespace GI_Subtitles.Services.Security
{
    /// <summary>
    /// Generates a stable, unique fingerprint from hardware identifiers.
    /// Used to derive machine-bound encryption keys — files encrypted on
    /// one machine cannot be decrypted on another.
    ///
    /// Components: CPU ProcessorId + Motherboard SerialNumber + First disk SerialNumber.
    /// These survive OS reinstalls and Windows updates.
    /// </summary>
    internal static class MachineFingerprint
    {
        private static byte[] _cachedFingerprint;
        private static (string Cpu, string Mobo, string Disk)? _cachedComponents;
        private static readonly object _lock = new object();

        /// <summary>
        /// Get the machine fingerprint as a SHA-256 hash (32 bytes).
        /// Result is cached for the process lifetime (hardware doesn't change mid-session).
        /// </summary>
        public static byte[] GetFingerprint()
        {
            if (_cachedFingerprint != null)
                return _cachedFingerprint;

            lock (_lock)
            {
                if (_cachedFingerprint != null)
                    return _cachedFingerprint;

                _cachedFingerprint = ComputeFingerprint();
                return _cachedFingerprint;
            }
        }

        private static byte[] ComputeFingerprint()
        {
            var sb = new StringBuilder(256);

            // Component 1: CPU Processor ID
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessorId FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        sb.Append(obj["ProcessorId"]?.ToString() ?? "");
                        break; // First CPU only
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"WMI CPU query failed: {ex.Message}");
                sb.Append("CPU_FALLBACK");
            }

            // Component 2: Motherboard Serial Number
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber FROM Win32_BaseBoard"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        sb.Append(obj["SerialNumber"]?.ToString() ?? "");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"WMI BaseBoard query failed: {ex.Message}");
                sb.Append("MB_FALLBACK");
            }

            // Component 3: First Disk Serial Number
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber FROM Win32_DiskDrive WHERE Index=0"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        sb.Append(obj["SerialNumber"]?.ToString()?.Trim() ?? "");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"WMI Disk query failed: {ex.Message}");
                sb.Append("DISK_FALLBACK");
            }

            // Hash the combined hardware string into a stable 32-byte fingerprint
            using (var sha256 = SHA256.Create())
            {
                return sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            }
        }

        /// <summary>
        /// Returns per-component SHA-256 hex hashes (CPU, motherboard, disk).
        /// The server uses these to perform a soft-match when a user replaces
        /// one hardware component (2-of-3 still identifies the same device).
        ///
        /// Each component is hashed independently — unlike <see cref="GetFingerprint"/>,
        /// which hashes the concatenated string. Result is cached for the process
        /// lifetime alongside the combined fingerprint.
        ///
        /// Fallback tokens (CPU_FALLBACK / MB_FALLBACK / DISK_FALLBACK) are still
        /// hashed so the server always sees a 64-char hex value — it can decide
        /// how to weight components it knows are fallbacks by comparing against
        /// other machines' fallback patterns.
        /// </summary>
        public static (string Cpu, string Mobo, string Disk) GetComponentHashesHex()
        {
            if (_cachedComponents.HasValue)
                return _cachedComponents.Value;

            lock (_lock)
            {
                if (_cachedComponents.HasValue)
                    return _cachedComponents.Value;

                string cpu = HashHex(QueryWmiSingle("SELECT ProcessorId FROM Win32_Processor", "ProcessorId", "CPU_FALLBACK"));
                string mobo = HashHex(QueryWmiSingle("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber", "MB_FALLBACK"));
                string disk = HashHex(QueryWmiSingle("SELECT SerialNumber FROM Win32_DiskDrive WHERE Index=0", "SerialNumber", "DISK_FALLBACK"));

                var components = (cpu, mobo, disk);
                _cachedComponents = components;
                return components;
            }
        }

        private static string QueryWmiSingle(string query, string property, string fallback)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var value = obj[property]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(value))
                            return value;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log.Warn($"WMI query failed ({query}): {ex.Message}");
            }
            return fallback;
        }

        private static string HashHex(string input)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
