using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;


/**********************************************************************************************************
 * V2X Controller - ExportSettingsStorage.cs
 * Author: Michal Švrček
 * Version: 1.2.4 (+utils)
 * Description: Storage logic for export settings in the V2X Controller application. Handles loading and saving
 *              export profiles, including TCP and tunnel configurations, as well as settings for profiles.
 *
 *              Added: additional utility helpers (merge/clone/rename/remove/export/import/validate/listing),
 *                     thread-safe save/backup, portable filename helpers and normalization utilities.
 *
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/

namespace V2XController
{
    public sealed class ExportProfile
    {
        public string Name { get; set; } = "Default";
        public ExportSettings Settings { get; set; } = new ExportSettings();
    }

    public static class ExportSettingsStorage
    {
        private static readonly object _fileLock = new object();

        // Folder next to the executable, e.g. bin/Release/net8.0-windows/Export data
        private static string GetDefaultDirectory()
        {
            return Path.Combine(AppContext.BaseDirectory, "Export data");
        }

        public static string GetDefaultFilePath()
        {
            var baseDir = AppContext.BaseDirectory;
            var targetDir = Path.Combine(baseDir, "ExportData");
            Directory.CreateDirectory(targetDir);
            return Path.Combine(targetDir, "export_profiles.xml");
        }


        public static List<ExportProfile> Load()
        {
            var list = new List<ExportProfile>();
            var file = GetDefaultFilePath();
            if (!File.Exists(file)) return list;

            try
            {
                var doc = XDocument.Load(file);
                var root = doc.Root;
                if (root == null || !string.Equals(root.Name.LocalName, "ExportProfiles", StringComparison.OrdinalIgnoreCase))
                    return list;

                foreach (var p in root.Elements("Profile"))
                {
                    var name = (string?)p.Attribute("name") ?? "Default";
                    var type = (string?)p.Attribute("type") ?? "TCP";
                    var modemEl = p.Element("Modem");
                    string? modemRaw = modemEl?.Attribute("raw")?.Value;
                    int? modemDec = TryParseInt(modemEl?.Attribute("dec")?.Value);
                    string? modemHex = modemEl?.Attribute("hex")?.Value;

                    var settings = new ExportSettings();

                    if (string.Equals(type, "TCP", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = p.Element("TCP");
                        var host = t?.Attribute("host")?.Value;
                        int? port = TryParseInt(t?.Attribute("port")?.Value);

                        settings = new ExportSettingsBuilder()
                            .WithTcp(host, port)
                            .WithModem(modemRaw, modemDec, modemHex)
                            .Build();
                    }
                    else if (string.Equals(type, "Tunnel", StringComparison.OrdinalIgnoreCase))
                    {
                        // Tunnel persisted as its own element
                        var t = p.Element("Tunnel");
                        var host = t?.Attribute("host")?.Value;
                        int? port = TryParseInt(t?.Attribute("port")?.Value);

                        // Treat tunnel as TCP endpoint for connectivity, but also preserve Tunnel fields
                        settings = new ExportSettingsBuilder()
                            .WithTcp(host, port)
                            .WithModem(modemRaw, modemDec, modemHex)
                            .Build();

                        // Ensure tunnel-specific fields are restored
                        typeof(ExportSettings).GetProperty(nameof(ExportSettings.TunnelRemoteHost))?.SetValue(settings, string.IsNullOrWhiteSpace(host) ? null : host.Trim());
                        typeof(ExportSettings).GetProperty(nameof(ExportSettings.TunnelRemotePort))?.SetValue(settings, port);
                    }
                    else
                    {
                        var s = p.Element("Serial");
                        var portName = s?.Attribute("port")?.Value;
                        int? baud = TryParseInt(s?.Attribute("baudrate")?.Value);
                        int? db = TryParseInt(s?.Attribute("databits")?.Value);
                        var parity = s?.Attribute("parity")?.Value;
                        var stopbits = s?.Attribute("stopbits")?.Value;
                        var handshake = s?.Attribute("handshake")?.Value ?? "None";

                        settings = new ExportSettingsBuilder()
                            .WithSerial(portName, baud, db, parity, stopbits, handshake)
                            .WithModem(modemRaw, modemDec, modemHex)
                            .Build();
                    }

                    list.Add(new ExportProfile { Name = name, Settings = settings });
                }
            }
            catch
            {
                // ignore malformed file
            }

            return list;
        }

        public static void Save(IEnumerable<ExportProfile> profiles)
        {
            var root = new XElement("ExportProfiles");

            foreach (var p in profiles)
            {
                var s = p.Settings;

                // Determine type: Tunnel if tunnel fields present, else TCP or Serial
                string typeAttr;
                if (!string.IsNullOrWhiteSpace(s.TunnelRemoteHost))
                    typeAttr = "Tunnel";
                else
                    typeAttr = s.IsTcpSelected ? "TCP" : "Serial";

                var profEl = new XElement("Profile",
                    new XAttribute("name", p.Name),
                    new XAttribute("type", typeAttr));

                profEl.Add(new XElement("Modem",
                    new XAttribute("raw", s.ModemRaw ?? ""),
                    new XAttribute("dec", s.ModemDec?.ToString(CultureInfo.InvariantCulture) ?? ""),
                    new XAttribute("hex", s.ModemHex ?? "")
                ));

                if (string.Equals(typeAttr, "Tunnel", StringComparison.OrdinalIgnoreCase))
                {
                    profEl.Add(new XElement("Tunnel",
                        new XAttribute("host", s.TunnelRemoteHost ?? ""),
                        new XAttribute("port", s.TunnelRemotePort?.ToString(CultureInfo.InvariantCulture) ?? "")
                    ));
                }
                else if (s.IsTcpSelected)
                {
                    profEl.Add(new XElement("TCP",
                        new XAttribute("host", s.TcpHost ?? ""),
                        new XAttribute("port", s.TcpPort?.ToString(CultureInfo.InvariantCulture) ?? "")
                    ));
                }
                else
                {
                    profEl.Add(new XElement("Serial",
                        new XAttribute("port", s.SerialPortName ?? ""),
                        new XAttribute("baudrate", s.SerialBaudrate?.ToString(CultureInfo.InvariantCulture) ?? ""),
                        new XAttribute("databits", s.SerialDataBits?.ToString(CultureInfo.InvariantCulture) ?? ""),
                        new XAttribute("parity", s.SerialParity ?? ""),
                        new XAttribute("stopbits", s.SerialStopBits ?? ""),
                        new XAttribute("handshake", string.IsNullOrWhiteSpace(s.SerialHandshake) ? "None" : s.SerialHandshake)
                    ));
                }

                root.Add(profEl);
            }

            var file = GetDefaultFilePath();
            root.Save(file);
        }

        private static int? TryParseInt(string? s)
        {
            if (int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
            return null;
        }

        // Builder stays unchanged...
        private sealed class ExportSettingsBuilder
        {
            private bool _isTcp;
            private string? _tcpHost;
            private int? _tcpPort;
            private string? _serialPort;
            private int? _baud;
            private int? _db;
            private string? _parity;
            private string? _stop;
            private string? _handshake;
            private string? _modemRaw;
            private int? _modemDec;
            private string? _modemHex;

            public ExportSettingsBuilder WithTcp(string? host, int? port)
            {
                _isTcp = true;
                _tcpHost = host?.Trim();
                _tcpPort = port;
                return this;
            }
            public ExportSettingsBuilder WithSerial(string? port, int? baud, int? db, string? parity, string? stop, string? handshake)
            {
                _isTcp = false;
                _serialPort = port?.Trim();
                _baud = baud;
                _db = db;
                _parity = parity?.Trim();
                _stop = stop?.Trim();
                _handshake = string.IsNullOrWhiteSpace(handshake) ? "None" : handshake.Trim();
                return this;
            }
            public ExportSettingsBuilder WithModem(string? raw, int? dec, string? hex)
            {
                _modemRaw = raw?.Trim();
                _modemDec = dec;
                _modemHex = hex?.Trim();
                return this;
            }

            public ExportSettings Build()
            {
                var es = new ExportSettings();
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.IsTcpSelected))?.SetValue(es, _isTcp);
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.TcpHost))?.SetValue(es, _tcpHost);
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.TcpPort))?.SetValue(es, _tcpPort);
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.SerialPortName))?.SetValue(es, _serialPort);
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.SerialBaudrate))?.SetValue(es, _baud);
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.SerialDataBits))?.SetValue(es, _db);
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.SerialParity))?.SetValue(es, _parity);
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.SerialStopBits))?.SetValue(es, _stop);
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.SerialHandshake))?.SetValue(es, _handshake ?? "None");
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.ModemRaw))?.SetValue(es, _modemRaw);
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.ModemDec))?.SetValue(es, _modemDec);
                typeof(ExportSettings).GetProperty(nameof(ExportSettings.ModemHex))?.SetValue(es, _modemHex);
                return es;
            }
        }

        // ---------------------------
        // Additional utility methods
        // ---------------------------

        /// <summary>
        /// Safely saves profiles to the default path using atomic replace (write to temp and move).
        /// Useful to avoid file corruption in case the app crashes while saving.
        /// Thread-safe.
        /// </summary>
        public static void SaveAtomic(IEnumerable<ExportProfile> profiles)
        {
            lock (_fileLock)
            {
                var target = GetDefaultFilePath();
                var dir = Path.GetDirectoryName(target) ?? GetDefaultDirectory();
                Directory.CreateDirectory(dir);

                var tmp = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(target)}.tmp.{Guid.NewGuid():N}.xml");

                var root = new XElement("ExportProfiles");
                foreach (var p in profiles)
                {
                    var s = p.Settings;
                    string typeAttr = !string.IsNullOrWhiteSpace(s.TunnelRemoteHost) ? "Tunnel" : s.IsTcpSelected ? "TCP" : "Serial";
                    var profEl = new XElement("Profile",
                        new XAttribute("name", p.Name),
                        new XAttribute("type", typeAttr));

                    profEl.Add(new XElement("Modem",
                        new XAttribute("raw", s.ModemRaw ?? ""),
                        new XAttribute("dec", s.ModemDec?.ToString(CultureInfo.InvariantCulture) ?? ""),
                        new XAttribute("hex", s.ModemHex ?? "")
                    ));

                    if (string.Equals(typeAttr, "Tunnel", StringComparison.OrdinalIgnoreCase))
                    {
                        profEl.Add(new XElement("Tunnel",
                            new XAttribute("host", s.TunnelRemoteHost ?? ""),
                            new XAttribute("port", s.TunnelRemotePort?.ToString(CultureInfo.InvariantCulture) ?? "")
                        ));
                    }
                    else if (s.IsTcpSelected)
                    {
                        profEl.Add(new XElement("TCP",
                            new XAttribute("host", s.TcpHost ?? ""),
                            new XAttribute("port", s.TcpPort?.ToString(CultureInfo.InvariantCulture) ?? "")
                        ));
                    }
                    else
                    {
                        profEl.Add(new XElement("Serial",
                            new XAttribute("port", s.SerialPortName ?? ""),
                            new XAttribute("baudrate", s.SerialBaudrate?.ToString(CultureInfo.InvariantCulture) ?? ""),
                            new XAttribute("databits", s.SerialDataBits?.ToString(CultureInfo.InvariantCulture) ?? ""),
                            new XAttribute("parity", s.SerialParity ?? ""),
                            new XAttribute("stopbits", s.SerialStopBits ?? ""),
                            new XAttribute("handshake", string.IsNullOrWhiteSpace(s.SerialHandshake) ? "None" : s.SerialHandshake)
                        ));
                    }

                    root.Add(profEl);
                }

                // Save to temporary file first
                root.Save(tmp);

                // Replace original
                var backup = target + ".bak";
                try
                {
                    if (File.Exists(target))
                    {
                        File.Replace(tmp, target, backup, ignoreMetadataErrors: true);
                        // remove backup if not needed
                        if (File.Exists(backup)) File.Delete(backup);
                    }
                    else
                    {
                        File.Move(tmp, target);
                    }
                }
                catch
                {
                    // attempt fallback: move temp to target (may overwrite)
                    try
                    {
                        if (File.Exists(tmp))
                        {
                            File.Copy(tmp, target, overwrite: true);
                            File.Delete(tmp);
                        }
                    }
                    catch
                    {
                        // swallow - leave temp file for diagnostics
                    }
                }
            }
        }

        /// <summary>
        /// Returns true and loaded profiles when file exists and is valid.
        /// </summary>
        public static bool TryLoadFromPath(string filePath, out List<ExportProfile> profiles)
        {
            profiles = new List<ExportProfile>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

            try
            {
                var doc = XDocument.Load(filePath);
                var root = doc.Root;
                if (root == null || !string.Equals(root.Name.LocalName, "ExportProfiles", StringComparison.OrdinalIgnoreCase))
                    return false;

                foreach (var p in root.Elements("Profile"))
                {
                    var name = (string?)p.Attribute("name") ?? "Default";
                    var type = (string?)p.Attribute("type") ?? "TCP";
                    var modemEl = p.Element("Modem");
                    string? modemRaw = modemEl?.Attribute("raw")?.Value;
                    int? modemDec = TryParseInt(modemEl?.Attribute("dec")?.Value);
                    string? modemHex = modemEl?.Attribute("hex")?.Value;

                    var settings = new ExportSettings();

                    if (string.Equals(type, "TCP", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = p.Element("TCP");
                        var host = t?.Attribute("host")?.Value;
                        int? port = TryParseInt(t?.Attribute("port")?.Value);

                        settings = new ExportSettingsBuilder()
                            .WithTcp(host, port)
                            .WithModem(modemRaw, modemDec, modemHex)
                            .Build();
                    }
                    else if (string.Equals(type, "Tunnel", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = p.Element("Tunnel");
                        var host = t?.Attribute("host")?.Value;
                        int? port = TryParseInt(t?.Attribute("port")?.Value);

                        settings = new ExportSettingsBuilder()
                            .WithTcp(host, port)
                            .WithModem(modemRaw, modemDec, modemHex)
                            .Build();

                        typeof(ExportSettings).GetProperty(nameof(ExportSettings.TunnelRemoteHost))?.SetValue(settings, string.IsNullOrWhiteSpace(host) ? null : host.Trim());
                        typeof(ExportSettings).GetProperty(nameof(ExportSettings.TunnelRemotePort))?.SetValue(settings, port);
                    }
                    else
                    {
                        var s = p.Element("Serial");
                        var portName = s?.Attribute("port")?.Value;
                        int? baud = TryParseInt(s?.Attribute("baudrate")?.Value);
                        int? db = TryParseInt(s?.Attribute("databits")?.Value);
                        var parity = s?.Attribute("parity")?.Value;
                        var stopbits = s?.Attribute("stopbits")?.Value;
                        var handshake = s?.Attribute("handshake")?.Value ?? "None";

                        settings = new ExportSettingsBuilder()
                            .WithSerial(portName, baud, db, parity, stopbits, handshake)
                            .WithModem(modemRaw, modemDec, modemHex)
                            .Build();
                    }

                    profiles.Add(new ExportProfile { Name = name, Settings = settings });
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Creates a timestamped backup of the profiles file (if present).
        /// Returns path to backup or null when no file was backed up.
        /// </summary>
        public static string? BackupProfiles(string? destinationDirectory = null)
        {
            var source = GetDefaultFilePath();
            if (!File.Exists(source)) return null;

            var destDir = string.IsNullOrWhiteSpace(destinationDirectory) ? Path.Combine(AppContext.BaseDirectory, "ExportBackups") : destinationDirectory;
            Directory.CreateDirectory(destDir);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var dest = Path.Combine(destDir, $"export_profiles_{stamp}.xml");

            try
            {
                File.Copy(source, dest, overwrite: false);
                return dest;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Restores a backup file by copying it to the default location (overwrites current profiles).
        /// Returns true on success.
        /// </summary>
        public static bool RestoreFromBackup(string backupFilePath)
        {
            if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath)) return false;
            var target = GetDefaultFilePath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? GetDefaultDirectory());
                File.Copy(backupFilePath, target, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Export profiles to JSON string for easy sharing.
        /// </summary>
        public static string ExportToJson(IEnumerable<ExportProfile> profiles, bool indented = true)
        {
            var opts = new JsonSerializerOptions { WriteIndented = indented, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            return JsonSerializer.Serialize(profiles, opts);
        }

        /// <summary>
        /// Tries to import profiles from a JSON string. Returns true when import looks valid.
        /// Note: this will not automatically save the imported profiles.
        /// </summary>
        public static bool TryImportFromJson(string json, out List<ExportProfile> profiles)
        {
            profiles = new List<ExportProfile>();
            if (string.IsNullOrWhiteSpace(json)) return false;

            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var parsed = JsonSerializer.Deserialize<List<ExportProfile>>(json, opts);
                if (parsed == null || parsed.Count == 0) return false;
                profiles = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns all available serial ports on the system (wrapper).
        /// </summary>
        public static IEnumerable<string> GetAvailableSerialPorts()
        {
            try
            {
                return System.IO.Ports.SerialPort.GetPortNames().OrderBy(n => n).ToList();
            }
            catch
            {
                return Enumerable.Empty<string>();
            }
        }

        /// <summary>
        /// Sanitizes a string so it can be safely used as a file name.
        /// </summary>
        public static string SanitizeFileName(string input, string fallback = "profile")
        {
            if (string.IsNullOrWhiteSpace(input)) return fallback;
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                if (invalid.Contains(ch)) sb.Append('_');
                else sb.Append(ch);
            }

            var result = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(result) ? fallback : result;
        }

        /// <summary>
        /// Creates a new default profile with a reasonable default name and settings.
        /// Useful for UI "New profile" action.
        /// </summary>
        public static ExportProfile CreateDefaultProfile(string? name = null)
        {
            var idx = DateTime.UtcNow.Ticks % 10000;
            var profileName = string.IsNullOrWhiteSpace(name) ? $"Profile_{idx:D4}" : name!;
            var settings = new ExportSettings
            {
                // best-effort defaults; property names assumed to exist on ExportSettings
            };
            return new ExportProfile { Name = profileName, Settings = settings };
        }

        /// <summary>
        /// Validates a profile for basic consistency. Returns true when profile looks usable.
        /// Does not attempt to correct data; only checks presence and basic ranges.
        /// </summary>
        public static bool ValidateProfile(ExportProfile profile, out string? error)
        {
            error = null;
            if (profile == null) { error = "Profile is null"; return false; }
            if (string.IsNullOrWhiteSpace(profile.Name)) { error = "Profile name is empty"; return false; }
            var s = profile.Settings;
            if (s == null) { error = "Settings are missing"; return false; }

            if (s.IsTcpSelected)
            {
                if (string.IsNullOrWhiteSpace(s.TcpHost)) { error = "TCP host is empty"; return false; }
                if (s.TcpPort.HasValue && (s.TcpPort < 1 || s.TcpPort > 65535)) { error = "TCP port out of range"; return false; }
            }
            else
            {
                // serial validation
                if (string.IsNullOrWhiteSpace(s.SerialPortName)) { error = "Serial port name is empty"; return false; }
                if (s.SerialBaudrate.HasValue && s.SerialBaudrate <= 0) { error = "Invalid baudrate"; return false; }
            }

            return true;
        }

        // ------------------------------------
        // New convenience utilities (general)
        // ------------------------------------

        /// <summary>
        /// Returns profile names in the saved file in order.
        /// </summary>
        public static IReadOnlyList<string> GetProfileNames()
        {
            var profiles = Load();
            return profiles.Select(p => p.Name).ToList();
        }

        /// <summary>
        /// Ensures a profile name is unique among given collection; if not, appends suffix.
        /// </summary>
        public static string EnsureUniqueProfileName(IEnumerable<ExportProfile> existing, string desired)
        {
            var baseName = string.IsNullOrWhiteSpace(desired) ? "Profile" : desired.Trim();
            var taken = new HashSet<string>(existing.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(baseName)) return baseName;

            for (int i = 1; i < 10000; i++)
            {
                var candidate = $"{baseName}_{i:D3}";
                if (!taken.Contains(candidate)) return candidate;
            }

            return $"{baseName}_{Guid.NewGuid():N}".Substring(0, Math.Min(40, baseName.Length + 8));
        }

        /// <summary>
        /// Merge profiles: existing profiles are preserved; incoming profiles with same name are skipped unless overwrite==true.
        /// Returns merged list (does not auto-save).
        /// </summary>
        public static List<ExportProfile> MergeProfiles(IEnumerable<ExportProfile> existing, IEnumerable<ExportProfile> incoming, bool overwrite = false)
        {
            var map = existing.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var ip in incoming)
            {
                if (map.ContainsKey(ip.Name))
                {
                    if (overwrite) map[ip.Name] = ip;
                }
                else
                {
                    // ensure unique
                    var unique = EnsureUniqueProfileName(map.Values, ip.Name);
                    if (!string.Equals(unique, ip.Name, StringComparison.Ordinal))
                        ip.Name = unique;
                    map[ip.Name] = ip;
                }
            }

            return map.Values.ToList();
        }

        /// <summary>
        /// Remove profile by name. Returns true when removed and saves new list.
        /// </summary>
        public static bool RemoveProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var profiles = Load();
            var idx = profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) return false;
            profiles.RemoveAt(idx);
            SaveAtomic(profiles);
            return true;
        }

        /// <summary>
        /// Rename a profile. Returns true when rename succeeded and file saved.
        /// </summary>
        public static bool RenameProfile(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) return false;
            var profiles = Load();
            var existing = profiles.FirstOrDefault(p => string.Equals(p.Name, oldName, StringComparison.OrdinalIgnoreCase));
            if (existing == null) return false;
            var desired = newName.Trim();
            var unique = EnsureUniqueProfileName(profiles, desired);
            existing.Name = unique;
            SaveAtomic(profiles);
            return true;
        }

        /// <summary>
        /// Clone a profile under a new name. Returns true when clone created and saved.
        /// </summary>
        public static bool CloneProfile(string sourceName, string targetName)
        {
            if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(targetName)) return false;
            var profiles = Load();
            var src = profiles.FirstOrDefault(p => string.Equals(p.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            if (src == null) return false;

            var clone = new ExportProfile
            {
                Name = EnsureUniqueProfileName(profiles, targetName.Trim()),
                Settings = DeepCloneSettings(src.Settings)
            };

            profiles.Add(clone);
            SaveAtomic(profiles);
            return true;
        }

        /// <summary>
        /// Deep-clone ExportSettings using JSON roundtrip (safe generic approach).
        /// </summary>
        private static ExportSettings DeepCloneSettings(ExportSettings s)
        {
            if (s == null) return new ExportSettings();
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var json = JsonSerializer.Serialize(s, opts);
            try
            {
                var cloned = JsonSerializer.Deserialize<ExportSettings>(json, opts);
                return cloned ?? new ExportSettings();
            }
            catch
            {
                return new ExportSettings();
            }
        }

        /// <summary>
        /// Export profiles to a file (XML or JSON based on extension). Returns path on success.
        /// </summary>
        public static string? ExportProfilesToFile(IEnumerable<ExportProfile> profiles, string filePath)
        {
            if (profiles == null) return null;
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            try
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".json")
                {
                    var json = ExportToJson(profiles, indented: true);
                    File.WriteAllText(filePath, json, Encoding.UTF8);
                    return filePath;
                }
                else
                {
                    // default XML
                    var root = new XElement("ExportProfiles");
                    foreach (var p in profiles)
                    {
                        var s = p.Settings;
                        string typeAttr = !string.IsNullOrWhiteSpace(s.TunnelRemoteHost) ? "Tunnel" : s.IsTcpSelected ? "TCP" : "Serial";
                        var profEl = new XElement("Profile",
                            new XAttribute("name", p.Name),
                            new XAttribute("type", typeAttr));

                        profEl.Add(new XElement("Modem",
                            new XAttribute("raw", s.ModemRaw ?? ""),
                            new XAttribute("dec", s.ModemDec?.ToString(CultureInfo.InvariantCulture) ?? ""),
                            new XAttribute("hex", s.ModemHex ?? "")
                        ));

                        if (string.Equals(typeAttr, "Tunnel", StringComparison.OrdinalIgnoreCase))
                        {
                            profEl.Add(new XElement("Tunnel",
                                new XAttribute("host", s.TunnelRemoteHost ?? ""),
                                new XAttribute("port", s.TunnelRemotePort?.ToString(CultureInfo.InvariantCulture) ?? "")
                            ));
                        }
                        else if (s.IsTcpSelected)
                        {
                            profEl.Add(new XElement("TCP",
                                new XAttribute("host", s.TcpHost ?? ""),
                                new XAttribute("port", s.TcpPort?.ToString(CultureInfo.InvariantCulture) ?? "")
                            ));
                        }
                        else
                        {
                            profEl.Add(new XElement("Serial",
                                new XAttribute("port", s.SerialPortName ?? ""),
                                new XAttribute("baudrate", s.SerialBaudrate?.ToString(CultureInfo.InvariantCulture) ?? ""),
                                new XAttribute("databits", s.SerialDataBits?.ToString(CultureInfo.InvariantCulture) ?? ""),
                                new XAttribute("parity", s.SerialParity ?? ""),
                                new XAttribute("stopbits", s.SerialStopBits ?? ""),
                                new XAttribute("handshake", string.IsNullOrWhiteSpace(s.SerialHandshake) ? "None" : s.SerialHandshake)
                            ));
                        }

                        root.Add(profEl);
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? GetDefaultDirectory());
                    root.Save(filePath);
                    return filePath;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Import profiles from a file (XML or JSON). If saveAfterImport==true, imported profiles replace current file.
        /// Returns imported profiles or null on error.
        /// </summary>
        public static List<ExportProfile>? ImportProfilesFromFile(string filePath, bool saveAfterImport = false)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            try
            {
                List<ExportProfile> imported;
                if (ext == ".json")
                {
                    var json = File.ReadAllText(filePath, Encoding.UTF8);
                    if (!TryImportFromJson(json, out imported)) return null;
                }
                else
                {
                    if (!TryLoadFromPath(filePath, out imported)) return null;
                }

                // normalize imported names
                var existing = Load();
                var merged = MergeProfiles(existing, imported, overwrite: false);

                if (saveAfterImport)
                {
                    SaveAtomic(merged);
                }

                return imported;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Normalize serial port names across all profiles (e.g. trim, uppercase 'COM' prefix).
        /// Returns number of modified profiles.
        /// </summary>
        public static int NormalizeSerialPortNames()
        {
            var profiles = Load();
            var changed = 0;
            foreach (var p in profiles)
            {
                var s = p.Settings;
                if (s == null) continue;
                if (!s.IsTcpSelected && !string.IsNullOrWhiteSpace(s.SerialPortName))
                {
                    var old = s.SerialPortName!;
                    var normalized = old.Trim();
                    // common normalization: "com3" -> "COM3"
                    if (normalized.StartsWith("com", StringComparison.OrdinalIgnoreCase))
                        normalized = "COM" + normalized.Substring(3).TrimStart();
                    if (!string.Equals(old, normalized, StringComparison.Ordinal))
                    {
                        typeof(ExportSettings).GetProperty(nameof(ExportSettings.SerialPortName))?.SetValue(s, normalized);
                        changed++;
                    }
                }
            }

            if (changed > 0)
            {
                SaveAtomic(profiles);
            }

            return changed;
        }

        /// <summary>
        /// Simple bulk validation for saved profiles. Returns list of invalid names with error messages.
        /// </summary>
        public static IReadOnlyList<(string profileName, string error)> ValidateAllProfiles()
        {
            var profiles = Load();
            var result = new List<(string, string)>();
            foreach (var p in profiles)
            {
                if (!ValidateProfile(p, out var err))
                {
                    result.Add((p.Name ?? "?", err ?? "Unknown"));
                }
            }

            return result;
        }
    }
}