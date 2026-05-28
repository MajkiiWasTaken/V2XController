using System.Globalization;
using System.IO;
using System.Xml.Linq;

/**********************************************************************************************************
 * V2X Controller - ExportSettingsStorage.cs
 * Author: Michal Švrček
 * Version: 1.2.4
 * Description: Storage logic for export settings in the V2X Controller application. Handles loading and saving
 *              export profiles, including TCP and tunnel configurations, as well as settings for profiles.
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
    }
}