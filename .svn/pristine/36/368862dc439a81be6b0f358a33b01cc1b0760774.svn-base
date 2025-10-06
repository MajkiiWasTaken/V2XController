using System;
using System.Globalization;

namespace V2XController
{
    public class ExportSettings
    {
        public bool IsTcpSelected { get; set; }
        public string? TcpHost { get; set; }
        public int? TcpPort { get; set; }
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
            if (win is null) throw new ArgumentNullException(nameof(win));

            var selected = (win.ConnectionComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString();
            bool isTcp = string.Equals(selected, "TCP/IP", StringComparison.OrdinalIgnoreCase);

            var s = new ExportSettings
            {
                IsTcpSelected = isTcp,
                // modem
                ModemRaw = Normalize(win.ModemTextBox?.Text)
            };
            if (TryParseDecOrHex(s.ModemRaw, out var modemVal))
            {
                s.ModemDec = modemVal;
                s.ModemHex = "0x" + modemVal.ToString("X");
            }

            if (isTcp)
            {
                s.TcpHost = Normalize(win.TcpHostTextBox?.Text);
                s.TcpPort = TryParseInt(Normalize(win.TcpPortTextBox?.Text), out var port) ? port : null;

                // clear serial
                s.SerialPortName = null;
                s.SerialBaudrate = null;
                s.SerialDataBits = null;
                s.SerialParity = null;
                s.SerialStopBits = null;
                s.SerialHandshake = null;
            }
            else
            {
                s.SerialPortName = Normalize(win.SerialPortTextBox?.Text);
                s.SerialBaudrate = TryParseInt(Normalize(win.SerialBaudTextBox?.Text), out var baud) ? baud : null;
                s.SerialDataBits = TryParseInt(Normalize(win.SerialDataBitsTextBox?.Text), out var db) ? db : null;
                s.SerialParity = Normalize(win.SerialParityTextBox?.Text);
                s.SerialStopBits = Normalize(win.SerialStopBitsTextBox?.Text);
                s.SerialHandshake = "None";             // not in UI

                // clear tcp
                s.TcpHost = null;
                s.TcpPort = null;
            }

            return s;
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