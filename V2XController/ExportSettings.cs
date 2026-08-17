using System.Globalization;

/**********************************************************************************************************
 * V2X Controller - ExportSettings.cs
 * Author: Michal Švrček
 * Version: 1.2.4
 * Description: Defines the ExportSettings class, which encapsulates the settings for exporting Modbus data, 
 *              including TCP, serial, and tunnel configurations. Provides methods to capture settings from 
 *              the UI and to clone settings instances.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/


namespace V2XController
{
    public class ExportSettings
    {
        public bool IsTcpSelected { get; set; }
        public string? TcpHost { get; set; }
        public int? TcpPort { get; set; }

        // Tunnel support (remote endpoint for "Serial tunnel" option)
        public string? TunnelRemoteHost { get; set; }
        public int? TunnelRemotePort { get; set; }

        public string? SerialPortName { get; set; }
        public int? SerialBaudrate { get; set; }
        public int? SerialDataBits { get; set; }
        public string? SerialParity { get; set; }
        public string? SerialStopBits { get; set; }
        public string? SerialHandshake { get; set; }
        public string? ModemRaw { get; set; }
        public int? ModemDec { get; set; }
        public string? ModemHex { get; set; }

        // Capture values directly from the window controls
        public static ExportSettings FromWindow(ExportWindow win)
        {
            if (win is null)
                throw new ArgumentNullException(nameof(win));

            var selected =
                (win.ConnectionComboBox.SelectedItem
                    as System.Windows.Controls.ComboBoxItem)
                ?.Content
                ?.ToString();

            bool isTcp =
                string.Equals(
                    selected,
                    "Modbus TCP",
                    StringComparison.OrdinalIgnoreCase);

            bool isTunnel =
                string.Equals(
                    selected,
                    "Serial tunnel",
                    StringComparison.OrdinalIgnoreCase);

            var s = new ExportSettings
            {
                IsTcpSelected = isTcp || isTunnel,
                ModemRaw = Normalize(win.ModemTextBox?.Text)
            };

            if (TryParseDecOrHex(
                s.ModemRaw,
                out var modemVal))
            {
                s.ModemDec = modemVal;
                s.ModemHex =
                    "0x" + modemVal.ToString("X");
            }

            if (isTunnel)
            {
                s.TunnelRemoteHost =
                    Normalize(
                        win.TunnelRemoteHostTextBox?.Text);

                s.TunnelRemotePort =
                    TryParseInt(
                        Normalize(
                            win.TunnelRemotePortTextBox?.Text),
                        out var tport)
                            ? tport
                            : null;

                s.TcpHost =
                    s.TunnelRemoteHost;

                s.TcpPort =
                    s.TunnelRemotePort;

                s.SerialPortName = null;
                s.SerialBaudrate = null;
                s.SerialDataBits = null;
                s.SerialParity = null;
                s.SerialStopBits = null;
                s.SerialHandshake = null;
            }
            else if (isTcp)
            {
                s.TcpHost =
                    Normalize(
                        win.TcpHostTextBox?.Text);

                s.TcpPort =
                    TryParseInt(
                        Normalize(
                            win.TcpPortTextBox?.Text),
                        out var port)
                            ? port
                            : null;

                s.SerialPortName = null;
                s.SerialBaudrate = null;
                s.SerialDataBits = null;
                s.SerialParity = null;
                s.SerialStopBits = null;
                s.SerialHandshake = null;
            }
            else
            {
                s.SerialPortName =
                    Normalize(
                        win.SerialPortComboBox
                            ?.SelectedItem
                            ?.ToString());

                string? baudText =
                    GetComboBoxValue(
                        win.SerialBaudComboBox);

                if (string.Equals(
                    baudText,
                    "Custom",
                    StringComparison.OrdinalIgnoreCase))
                {
                    baudText =
                        Normalize(
                            win.SerialCustomBaudTextBox
                                ?.Text);
                }

                s.SerialBaudrate =
                    TryParseInt(
                        baudText,
                        out var baud)
                            ? baud
                            : null;

                string? dataBitsText =
                    GetComboBoxValue(
                        win.SerialDataBitsComboBox);

                s.SerialDataBits =
                    TryParseInt(
                        dataBitsText,
                        out var db)
                            ? db
                            : null;

                s.SerialParity =
                    Normalize(
                        GetComboBoxValue(
                            win.SerialParityComboBox));

                s.SerialStopBits =
                    Normalize(
                        GetComboBoxValue(
                            win.SerialStopBitsComboBox));

                s.SerialHandshake = "None";

                s.TcpHost = null;
                s.TcpPort = null;
                s.TunnelRemoteHost = null;
                s.TunnelRemotePort = null;
            }

            return s;
        }

        private static string? GetComboBoxValue(
    System.Windows.Controls.ComboBox? comboBox)
        {
            if (comboBox == null)
                return null;

            if (comboBox.SelectedItem is
                System.Windows.Controls.ComboBoxItem item)
            {
                return Normalize(
                    item.Content?.ToString());
            }

            return Normalize(
                comboBox.SelectedItem?.ToString());
        }

        private static string? Normalize(string? s)
        {
            var t = (s ?? string.Empty).Trim();
            return t.Length == 0 ? null : t;
        }

        private static bool TryParseInt(string? s, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        // Accepts decimal or hex forms: 0x1A or 1Ah or 26
        private static bool TryParseDecOrHex(string? s, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim();

            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

            if (t.EndsWith("h", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(t[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

            return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public static ExportSettings CloneFrom(ExportSettings s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s));
            var copy = new ExportSettings
            {
                IsTcpSelected = s.IsTcpSelected,
                TcpHost = s.TcpHost,
                TcpPort = s.TcpPort,
                TunnelRemoteHost = s.TunnelRemoteHost,
                TunnelRemotePort = s.TunnelRemotePort,
                SerialPortName = s.SerialPortName,
                SerialBaudrate = s.SerialBaudrate,
                SerialDataBits = s.SerialDataBits,
                SerialParity = s.SerialParity,
                SerialStopBits = s.SerialStopBits,
                SerialHandshake = s.SerialHandshake,
                ModemRaw = s.ModemRaw,
                ModemDec = s.ModemDec,
                ModemHex = s.ModemHex
            };
            return copy;
        }
    }
}