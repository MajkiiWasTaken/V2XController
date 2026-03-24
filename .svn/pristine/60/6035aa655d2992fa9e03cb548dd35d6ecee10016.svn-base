using ComCommon;
using Logger;
using Microsoft.VisualBasic; // Interaction.InputBox
using ModbusNewLib;
using System.CodeDom;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace V2XController
{
    //Todo:
    //
    //IDK NIC ME NENAPADA UZ AAAAAAAAAAAAAAAAAAAAAAAAA

    public partial class ExportWindow : Window
    {

        // Header:
        //   [0]: zone count
        // Each zone record (in order: MainZone, SubZone, Lat(32b), Lon(32b), WidthCm, HeightCm, Azimuth):
        //   1 + 1 + 2 + 2 + 1 + 1 + 1 = 9 registers per zone
        private const ushort MPC_BASE_ADDR = 0x0300; // starting holding register address to write into

        private const int MAX_REGS_PER_REQUEST = 50;

        // Writable V2X map starts at 0x0300 + 44 (0x032C) on MPCv3 WLC and at 0x0300 + 176 (0x03B0) on MPCv3 RTV, 10 regs for a zone without spaces.
        private const ushort MPCv3WLC_WRITE_OFFSET = 44; // Offset for WLC
        private const ushort MPCv3RTV_WRITE_OFFSET = 176; // Offset for RTV
        private const int MPC_ZONE_STRIDE = 10;      // registers per zone "slot" (X/Y=2+2, len=1, width=1, az=1 with gaps)

        // add near other helpers inside ExportWindow
        private static bool IsOpAborted(IOException ex) => ex.HResult == unchecked((int)0x800703E3);

        private readonly List<ExportProfile> _profiles = new();

        private bool _isDirty;
        private bool _suppressFormEvents;
        private bool _suppressProfileChange;
        private string? _loadedProfileName;
        private ExportSettings? _loadedSettings;

        public ExportSettings? Settings { get; private set; }

        //public Button Start { get; private set; }  // Reference to the export button
        //public Button ReadButton { get; private set; }  // Reference to the read button

        private static ushort Off1(ushort zeroBased) => (ushort)(zeroBased + 1);

        private static bool ApproximatelyZero(double v, double eps = 1e-8) => Math.Abs(v) <= eps;

        private TcpListener? _tunnelListener;
        private CancellationTokenSource? _tunnelCts;
        private Task? _tunnelTask;
        private SerialPort? _tunnelSerial;
        private readonly object _tunnelLock = new();

        private string? _stateFilePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "V2XController",
            "ExportWindowState.json");

        private class ExportWindowState
        {
            public int? ConnectionIndex { get; set; }
            public string? TunnelRemoteHost { get; set; }
            public string? TunnelRemotePort { get; set; }
        }

        public bool IsTunnelSelected { get; set; }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set { _progressValue = value; OnPropertyChanged(); }
        }

        private double _progressMaximum = 100;
        public double ProgressMaximum
        {
            get => _progressMaximum;
            set { _progressMaximum = value; OnPropertyChanged(); }
        }

        private string _progressText = "";
        public string ProgressText
        {
            get => _progressText;
            set { _progressText = value; OnPropertyChanged(); }
        }

        private Visibility _progressTextVisibility = Visibility.Collapsed;
        public Visibility ProgressTextVisibility
        {
            get => _progressTextVisibility;
            set { _progressTextVisibility = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public ExportWindow()
        {
            InitializeComponent();
            DataContext = this;

            // Wire up UI lifecycle handlers
            Loaded += ExportWindow_Loaded;
            Closing += ExportWindow_Closing;

            // Initialize tunnel indicator to inactive visually
            UpdateTunnelIndicator(false);
        }

        public void SetReadMode(bool isReadMode)
        {
            if (Start != null)
                Start.IsEnabled = !isReadMode;

            if (ReadButton != null)
                ReadButton.IsEnabled = isReadMode;

            Title = isReadMode ? "Read Activation Zones" : "Export Activation Zones";
        }

        private void ShowBusy(string message, bool showProgress = false, int maxProgress = 100)
        {
            try
            {
                BusyText.Text = string.IsNullOrWhiteSpace(message) ? "Working..." : message;
                BusyOverlay.Visibility = Visibility.Visible;

                if (showProgress)
                {
                    BusyProgressBar.IsIndeterminate = false;
                    ProgressMaximum = maxProgress;
                    ProgressValue = 0;
                    ProgressTextVisibility = Visibility.Visible;
                    ProgressText = "0%";
                }
                else
                {
                    BusyProgressBar.IsIndeterminate = true;
                    ProgressTextVisibility = Visibility.Collapsed;
                }

                foreach (var b in new[] { Start, ReadButton, SaveButton, NewButton, RenameButton, DeleteButton, Exit, ReinitMPC })
                    if (b != null) b.IsEnabled = false;

                Mouse.OverrideCursor = Cursors.Wait;
            }
            catch { /* ignore */ }
        }

        private void UpdateProgress(int current, int total, string statusMessage = "")
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    double oldValue = ProgressValue;
                    double newValue = current;

                    ProgressMaximum = total;

                    // Animace pokroku pouze pokud se hodnota změnila
                    if (Math.Abs(oldValue - newValue) > 0.01)
                    {
                        var animation = new System.Windows.Media.Animation.DoubleAnimation
                        {
                            From = oldValue,
                            To = newValue,
                            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                            {
                                EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut
                            }
                        };

                        // Aplikovat animaci na ProgressBar
                        BusyProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, animation);
                    }

                    ProgressValue = newValue;

                    double percentage = total > 0 ? (current * 100.0 / total) : 0;
                    ProgressText = string.IsNullOrWhiteSpace(statusMessage)
                        ? $"{percentage:F0}% ({current}/{total})"
                        : $"{percentage:F0}% - {statusMessage}";
                }, DispatcherPriority.Send);
            }
            catch { /* ignore */ }
        }

        private void HideBusy()
        {
            try
            {
                BusyOverlay.Visibility = Visibility.Collapsed;
                BusyProgressBar.IsIndeterminate = false;
                ProgressValue = 0;
                ProgressTextVisibility = Visibility.Collapsed;

                foreach (var b in new[] { Start, ReadButton, SaveButton, NewButton, RenameButton, DeleteButton, Exit, ReinitMPC })
                    if (b != null) b.IsEnabled = true;

                Mouse.OverrideCursor = null;
            }
            catch { /* ignore */ }
        }

        private async Task<MessageBoxResult> ShowMessageAfterBusyAsync(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon)
        {
            // Hide overlay first, then show the dialog on the UI thread
            await Dispatcher.InvokeAsync(HideBusy, DispatcherPriority.Send);
            return await Dispatcher.InvokeAsync(() => MessageBox.Show(this, text, caption, buttons, icon), DispatcherPriority.Background);
        }

        private void LoadWindowState()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir) && System.IO.File.Exists(_stateFilePath))
                {
                    var json = System.IO.File.ReadAllText(_stateFilePath);
                    var st = JsonSerializer.Deserialize<ExportWindowState>(json);
                    if (st != null)
                    {
                        if (st.ConnectionIndex.HasValue && st.ConnectionIndex.Value >= 0 && st.ConnectionIndex.Value < ConnectionComboBox.Items.Count)
                            ConnectionComboBox.SelectedIndex = st.ConnectionIndex.Value;

                        if (!string.IsNullOrWhiteSpace(st.TunnelRemoteHost))
                            TunnelRemoteHostTextBox.Text = st.TunnelRemoteHost;
                        if (!string.IsNullOrWhiteSpace(st.TunnelRemotePort))
                            TunnelRemotePortTextBox.Text = st.TunnelRemotePort;
                    }
                }
            }
            catch
            {
                // ignore loading errors
            }
        }

        private void SaveWindowState()
        {
            try
            {
                var st = new ExportWindowState
                {
                    ConnectionIndex = ConnectionComboBox.SelectedIndex,
                    TunnelRemoteHost = TunnelRemoteHostTextBox?.Text,
                    TunnelRemotePort = TunnelRemotePortTextBox?.Text
                };

                var dir = System.IO.Path.GetDirectoryName(_stateFilePath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(_stateFilePath, json);
            }
            catch
            {
                // ignore save errors
            }
        }


        private void ExportWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveWindowState();
        }


        private void ExportWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadWindowState();

            if (ConnectionComboBox.SelectedIndex < 0)
                ConnectionComboBox.SelectedIndex = 0;
            ApplyConnectionLayout();

            _profiles.Clear();
            _profiles.AddRange(ExportSettingsStorage.Load());

            RefreshProfilesList();

            if (_profiles.Count > 0)
            {
                _suppressProfileChange = true;
                SelectComboBox.SelectedIndex = 0;
                _suppressProfileChange = false;

                PopulateForm(_profiles[0].Settings);
                CaptureLoadedSnapshot(_profiles[0].Settings, _profiles[0].Name);
            }
            else
            {
                _isDirty = false;
                _loadedProfileName = null;
                _loadedSettings = null;
            }

            AttachChangeTracking();

            // Set the appropriate mode based on the title
            if (Title == "Read Activation Zones") SetReadMode(true);
            else SetReadMode(false);  // Default to export mode
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            Settings = ExportSettings.FromWindow(this);
            if (Settings == null)
            {
                MessageBox.Show("Missing export settings.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool usingTunnel = string.Equals(
                (ConnectionComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim(),
                "Serial tunnel",
                StringComparison.OrdinalIgnoreCase);

            bool isTcp = Settings.IsTcpSelected || usingTunnel;

            if (usingTunnel)
            {
                var remoteHost = TunnelRemoteHostTextBox?.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(remoteHost))
                {
                    MessageBox.Show("Remote host is required for the tunnel.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!int.TryParse(TunnelRemotePortTextBox?.Text?.Trim(), out var remotePort) || remotePort <= 0 || remotePort > 65535)
                {
                    MessageBox.Show("Remote port is invalid (1-65535).", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Settings.IsTcpSelected = true;
                Settings.TcpHost = remoteHost;
                Settings.TcpPort = remotePort;
            }

            if (!TryValidateSettings(Settings, out var validationError))
            {
                MessageBox.Show(validationError, "Export - validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (Owner is not MainWindow mw)
            {
                MessageBox.Show("No main window owner.", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Určit, jaké zóny exportujeme (podle IsSwitchZone)
            var zonesAct = mw.ActivationZonesCollection.Where(z => !z.IsSwitchZone).ToList(); // WLC zóny
            var zonesSw = mw.ActivationZonesCollection.Where(z => z.IsSwitchZone).ToList();   // RTV zóny

            if (zonesAct.Count == 0 && zonesSw.Count == 0)
            {
                MessageBox.Show("No activation zones or switch zones to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // === PRE-EXPORT VALIDATION: Read Register 0x0000 and check device type ===
            System.Diagnostics.Debug.WriteLine("\n=== PRE-EXPORT VALIDATION ===");
            string? deviceType = null;
            bool isDeviceWLC = false;
            bool isDeviceRTV = false;

            try
            {
                var (success, reg0Value, error) = await ReadRegister0x0000AsStringAsync();
                if (success && !string.IsNullOrWhiteSpace(reg0Value))
                {
                    deviceType = reg0Value;
                    isDeviceWLC = reg0Value.Contains("WLC", StringComparison.OrdinalIgnoreCase);
                    isDeviceRTV = reg0Value.Contains("RTV", StringComparison.OrdinalIgnoreCase);

                    System.Diagnostics.Debug.WriteLine($"Device type detected: {deviceType}");
                    System.Diagnostics.Debug.WriteLine($"Is WLC: {isDeviceWLC}, Is RTV: {isDeviceRTV}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: Could not read device type from register 0x0000: {error}");
                }
            }
            catch (Exception diagEx)
            {
                System.Diagnostics.Debug.WriteLine($"WARNING: Exception during device type detection: {diagEx.Message}");
            }

            bool exportingWLC = zonesAct.Count > 0;
            bool exportingRTV = zonesSw.Count > 0;

            if (deviceType != null) 
            {
                string zonesType = exportingWLC ? "WLC (Activation Zones)" : "RTV (Switches)";
                string deviceTypeStr = isDeviceWLC ? "WLC" : (isDeviceRTV ? "RTV" : "Unknown");

                // Kontrola nesouladu - NELZE POKRAČOVAT
                if ((exportingWLC && isDeviceRTV) || (exportingRTV && isDeviceWLC))
                {
                    System.Diagnostics.Debug.WriteLine("DEVICE TYPE MISMATCH DETECTED!");

                    MessageBox.Show(
                        $"DEVICE FIRMWARE TYPE MISMATCH!\n\n" +
                        $"Device type: MPC-{deviceTypeStr}\n" +
                        $"Zones in table: {zonesType}\n\n" +
                        $"You are trying to export {zonesType} zones to an MPC-{deviceTypeStr}!\n\n" +
                        $"Switch to the correct mode ({deviceTypeStr})\n" +
                        $"Please try exporting again after switching to correct mode.",
                        "Export Type Mismatch",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($" Type match OK: Exporting {zonesType} to MPC-{deviceTypeStr}");
                }
            }
            System.Diagnostics.Debug.WriteLine("=== END PRE-EXPORT VALIDATION ===\n");

            // Určit správný offset podle typu zón
            ushort writeOffset = exportingWLC ? MPCv3WLC_WRITE_OFFSET : MPCv3RTV_WRITE_OFFSET;
            var zonesToExport = exportingWLC ? zonesAct : zonesSw;
            string zoneTypeName = exportingWLC ? "activation zones (WLC)" : "switch zones (RTV)";

            // Validace obsahu zón
            if (zonesToExport.Count > 0)
            {
                var report = BuildZonesDebugReport(zonesToExport, 8, $"{zoneTypeName} (preview)");
                System.Diagnostics.Debug.WriteLine(report);

                bool allLatZero = zonesToExport.All(z => ApproximatelyZero(z.Latitude));
                bool allLonZero = zonesToExport.All(z => ApproximatelyZero(z.Longitude));
                bool anySize = zonesToExport.Any(z => z.Width > 0 && z.Height > 0);

                if ((allLatZero && allLonZero) || !anySize)
                {
                    var shortPreview = string.Join(Environment.NewLine, report.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Take(15));
                    var msg = $"{zoneTypeName} look empty (all coordinates are zero or sizes are zero).\n\nContinue anyway?\n\n" + shortPreview;
                    if (MessageBox.Show(msg, "Export preflight", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        return;
                }
            }

            // Spočítat celkový počet registrů pro progress
            int totalRegisters = zonesToExport.Count * 10; // 10 registrů na zónu
            ShowBusy($"Exporting {zoneTypeName}...", showProgress: true, maxProgress: totalRegisters);
            await Task.Delay(50);

            try
            {
                byte unitId = (byte)Math.Clamp(Settings.ModemDec ?? 1, 1, 247);

                // === MODBUS TCP ===
                if (isTcp)
                {
                    var host = Settings.TcpHost?.Trim();
                    var port = Settings.TcpPort ?? 502;
                    if (string.IsNullOrWhiteSpace(host))
                    {
                        await ShowMessageAfterBusyAsync("Modbus TCP host is required.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    string hostOnly = host.Split(new[] { ':', ';', ',' }, 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    string displayEndpoint = $"{hostOnly}:{port}";
                    var cfg = ResolveProtocolCfg();

                    // Probes
                    UpdateProgress(0, totalRegisters, "Connecting...");
                    if (!TryModbusTcpPing(hostOnly, port, unitId, cfg.ConnectionTimeout, Math.Max(cfg.ReceiveTimeout, 3000), out var pingDiag))
                    {
                        if (TryProbeRtuOverTcp(hostOnly, port, unitId, Math.Max(cfg.ConnectionTimeout, 3000), out var rtuDiag) && string.IsNullOrWhiteSpace(rtuDiag) == false)
                        {
                            await ShowMessageAfterBusyAsync(
                                $"Remote {displayEndpoint} responds as raw serial (no MBAP). This is a serial tunnel.\n\n" +
                                "Current code expects Modbus/TCP (MBAP). Either run a Modbus/TCP gateway, or enable Tunnel fallback in the app (not enabled by default).",
                                "Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        await ShowMessageAfterBusyAsync($"No Modbus reply from {displayEndpoint} (UnitId {unitId}).\nDiag: {pingDiag}", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // === ZÁPIS ZÓN (TCP) ===
                    UpdateProgress(0, totalRegisters, "Writing zones...");

                    var progress = new Progress<(int current, int total, string message)>(p =>
                    {
                        UpdateProgress(p.current, p.total, p.message);
                    });

                    var actRes = await Task.Run(() =>
                        WriteMpcZonesBatchedWithProgress(
                            hostOnly, port, unitId, zonesToExport, writeOffset, isTcp: true, null,
                            connectTimeoutMs: Math.Max(cfg.ConnectionTimeout, 3000),
                            sendTimeoutMs: Math.Max(cfg.SendTimeout, 2000),
                            receiveTimeoutMs: Math.Max(cfg.ReceiveTimeout, 6000),
                            progress,
                            out ModbusStateCode st, out string? err));

                    if (!actRes)
                    {
                        await ShowMessageAfterBusyAsync($"Export ({zoneTypeName}) failed.\nTarget={displayEndpoint}, UnitId={unitId}",
                            "Export", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    await ShowMessageAfterBusyAsync(
                        $"Exported {zonesToExport.Count} {zoneTypeName} to {displayEndpoint} (UnitId {unitId}).",
                        "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                // === SERIAL PORT (RS485) ===
                else
                {
                    byte slave = (byte)Math.Clamp(Settings.ModemDec ?? 1, 1, 247);

                    var progress = new Progress<(int current, int total, string message)>(p =>
                    {
                        UpdateProgress(p.current, p.total, p.message);
                    });

                    var (ok, st, err) = await WriteMpcZonesBatchedAsyncWithProgress(Settings, slave, zonesToExport, writeOffset, timeoutMs: 3000, progress);
                    if (!ok || st != ModbusStateCode.Success)
                    {
                        var extra = string.IsNullOrWhiteSpace(err) ? "" : $" ({err})";
                        await ShowMessageAfterBusyAsync($"Export {zoneTypeName} over RS485 failed (state={st}){extra}.", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    await ShowMessageAfterBusyAsync(
                        $"Exported {zonesToExport.Count} {zoneTypeName} over RS485 on {Settings.SerialPortName} (slave {slave}).",
                        "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                bool tcpLike = Settings!.IsTcpSelected || usingTunnel;
                var target = tcpLike ? $"{Settings.TcpHost}:{Settings.TcpPort ?? 502}" : (Settings.SerialPortName ?? "-");
                byte uid = (byte)Math.Clamp(Settings.ModemDec ?? 1, 1, 247);

                var path = SaveExportError(ex, tcpLike ? (usingTunnel ? "Tunnel" : "TCP") : "RTU", target, uid, MPC_BASE_ADDR, 0, null);
                await ShowMessageAfterBusyAsync($"Export error. Details were copied to clipboard and saved:\n{path}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                HideBusy();
                if (usingTunnel)
                {
                    try
                    {
                        UpdateTunnelIndicator(false);
                        TunnelRemoteHostTextBox.IsEnabled = true;
                        TunnelRemotePortTextBox.IsEnabled = true;
                    }
                    catch { }
                }
            }
        }

        private static bool WriteMpcZonesBatchedWithProgress(
    string host, int port, byte unitId,
    IReadOnlyList<ActivationZone> zones,
    ushort writeOffset, // PŘIDÁNO: offset pro WLC (44) nebo RTV (176)
    bool isTcp, ExportSettings? settingsForRtu,
    int connectTimeoutMs, int sendTimeoutMs, int receiveTimeoutMs,
    IProgress<(int, int, string)>? progress,
    out ModbusStateCode state, out string? error)
        {
            state = ModbusStateCode.Success;
            error = null;

            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;
            const int MAX_REGS_PER_CHUNK = 50;

            try
            {
                System.Diagnostics.Debug.WriteLine($"=== Writing MPC zones via {(isTcp ? "TCP" : "RS485")} (BATCHED) ===");
                System.Diagnostics.Debug.WriteLine($"Write offset: 0x{writeOffset:X4} ({writeOffset})");

                progress?.Report((0, zones.Count * 10, "Unlocking device..."));

                // UNLOCK PŘED ZÁPISEM
                if (isTcp)
                {
                    System.Diagnostics.Debug.WriteLine($"Unlocking at 0x{UNLOCK_REGISTER:X4}...");
                    if (!TryWriteUnlockTcp(host, port, unitId, UNLOCK_REGISTER, UNLOCK_VALUE,
                        connectTimeoutMs, sendTimeoutMs, receiveTimeoutMs, out state, out error))
                    {
                        error = $"Failed to unlock: {error}";
                        System.Diagnostics.Debug.WriteLine($"   FAILED: {error}");
                        return false;
                    }
                    System.Diagnostics.Debug.WriteLine($"   OK");
                    System.Threading.Thread.Sleep(800);
                }

                const ushort BASE_ADDR = MPC_BASE_ADDR;       // 0x0300
                const int ZONE_STRIDE = MPC_ZONE_STRIDE;      // 10
                ushort FIRST_ZONE_BASE = (ushort)(BASE_ADDR + writeOffset); // Použít předaný offset

                // Sestavit všechny registry
                var allRegisters = new List<(ushort addr, ushort value)>();

                for (int idx = 0; idx < zones.Count; idx++)
                {
                    var z = zones[idx];
                    int mainZone = z.MainZone + 1;
                    int subZone = z.SubZone + 1;

                    // Pro WLC: 4 hlavní × 5 sub = 20 zón
                    // Pro RTV: 5 hlavních × 7 sub = 35 zón
                    int subsPerMain = (writeOffset == MPCv3WLC_WRITE_OFFSET) ? 5 : 7;
                    int zoneIndex = ((mainZone - 1) * subsPerMain) + (subZone - 1);
                    ushort zoneBase = (ushort)(FIRST_ZONE_BASE + (zoneIndex * ZONE_STRIDE));

                    var (latLo, latHi) = FloatToWordsWS((float)z.Latitude);
                    var (lonLo, lonHi) = FloatToWordsWS((float)z.Longitude);
                    var (heightLo, heightHi) = FloatToWordsWS((float)z.Height);
                    var (widthLo, widthHi) = FloatToWordsWS((float)z.Width);
                    var (azLo, azHi) = FloatToWordsWS((float)z.Azimuth);

                    // OPRAVA: Zapsat VŠECH 10 registrů pro každou zónu
                    allRegisters.Add((zoneBase, lonLo));
                    allRegisters.Add(((ushort)(zoneBase + 1), lonHi));
                    allRegisters.Add(((ushort)(zoneBase + 2), latLo));
                    allRegisters.Add(((ushort)(zoneBase + 3), latHi));
                    allRegisters.Add(((ushort)(zoneBase + 4), heightLo));
                    allRegisters.Add(((ushort)(zoneBase + 5), heightHi));
                    allRegisters.Add(((ushort)(zoneBase + 6), widthLo));
                    allRegisters.Add(((ushort)(zoneBase + 7), widthHi));
                    allRegisters.Add(((ushort)(zoneBase + 8), azLo));
                    allRegisters.Add(((ushort)(zoneBase + 9), azHi));
                

                System.Diagnostics.Debug.WriteLine($"Zone {mainZone}-{subZone}: base=0x{zoneBase:X4}, 10 regs");
                }

                System.Diagnostics.Debug.WriteLine($"Total registers to write: {allRegisters.Count}");

                // Připojení pro TCP
                ModbusTcpIp? modbusTcp = null;
                if (isTcp)
                {
                    var protocolParams = BuildProtocolParams(false);
                    protocolParams.ConnectionTimeout = connectTimeoutMs;
                    protocolParams.SendTimeout = sendTimeoutMs;
                    protocolParams.ReceiveTimeout = receiveTimeoutMs;
                    modbusTcp = new ModbusTcpIp($"{host}:{port}", protocolParams);
                }

                try
                {
                    // Rozdělit do dávek po 50
                    int offset = 0;
                    while (offset < allRegisters.Count)
                    {
                        // Re-unlock před každou dávkou
                        if (isTcp)
                        {
                            modbusTcp!.WriteToHoldingRegister(unitId, UNLOCK_REGISTER - 1, UNLOCK_VALUE, out _, "Re-unlock");
                            System.Threading.Thread.Sleep(250);
                        }

                        // Zjistit velikost dávky
                        int chunkSize = Math.Min(MAX_REGS_PER_CHUNK, allRegisters.Count - offset);
                        ushort startAddr = allRegisters[offset].addr;
                        int actualChunkSize = 1;

                        for (int i = 1; i < chunkSize; i++)
                        {
                            if (allRegisters[offset + i].addr != startAddr + i)
                                break;
                            actualChunkSize++;
                        }

                        var chunkData = new ushort[actualChunkSize];
                        for (int i = 0; i < actualChunkSize; i++)
                        {
                            chunkData[i] = allRegisters[offset + i].value;
                        }

                        System.Diagnostics.Debug.WriteLine($"Writing {(isTcp ? "TCP" : "RS485")} batch: {actualChunkSize} regs from 0x{startAddr:X4}");

                        // Report progress
                        progress?.Report((offset, allRegisters.Count, $"Writing registers {offset}/{allRegisters.Count}..."));

                        // Zápis
                        if (isTcp)
                        {
                            if (!modbusTcp!.WriteDataToHoldingRegisters(unitId, Off1(startAddr), (ushort)actualChunkSize, chunkData, out state, $"Batch {offset}"))
                            {
                                error = $"TCP write failed at 0x{startAddr:X4}";
                                return false;
                            }
                        }

                        System.Diagnostics.Debug.WriteLine($"   OK");
                        offset += actualChunkSize;
                        System.Threading.Thread.Sleep(150);
                    }

                    // Lock
                    progress?.Report((allRegisters.Count, allRegisters.Count, "Locking device..."));
                    if (isTcp)
                    {
                        modbusTcp!.WriteToHoldingRegister(unitId, UNLOCK_REGISTER - 1, 0, out _, "Lock");
                    }

                    System.Diagnostics.Debug.WriteLine("\n=== ALL ZONES WRITTEN (BATCHED) ===");
                    return true;
                }
                finally
                {
                    modbusTcp?.Dispose();
                }
            }
            catch (Exception ex)
            {
                state = ModbusStateCode.UndefinedError;
                error = ex.Message;
                return false;
            }
        }

        private static async Task<(bool ok, ModbusStateCode state, string? error)> WriteMpcZonesBatchedAsync(
    ExportSettings s, byte unitId, IReadOnlyList<ActivationZone> zones, int timeoutMs)
        {
            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;
            const int MAX_REGS_PER_CHUNK = 50;

            try
            {
                System.Diagnostics.Debug.WriteLine("=== Writing MPC zones via RS485 (BATCHED) ===");

                // Odemknout
                var (uok, ust, uerr) = await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs);
                if (!uok || ust != ModbusStateCode.Success)
                    return (false, ust, $"Unlock failed: {uerr}");
                await Task.Delay(800);

                const ushort BASE_ADDR = MPC_BASE_ADDR;
                const ushort WRITE_OFFSET = MPCv3RTV_WRITE_OFFSET;
                const int ZONE_STRIDE = MPC_ZONE_STRIDE;
                ushort FIRST_ZONE_BASE = (ushort)(BASE_ADDR + WRITE_OFFSET);

                // Sestavit všechny registry
                var allRegisters = new List<(ushort addr, ushort value)>();

                for (int idx = 0; idx < zones.Count; idx++)
                {
                    var z = zones[idx];
                    int mainZone = z.MainZone + 1;
                    int subZone = z.SubZone + 1;
                    int zoneIndex = ((mainZone - 1) * 5) + (subZone - 1);
                    ushort zoneBase = (ushort)(FIRST_ZONE_BASE + (zoneIndex * ZONE_STRIDE));

                    float lonF = (float)z.Longitude;
                    float latF = (float)z.Latitude;
                    float heightF = (float)z.Height;
                    float widthF = (float)z.Width;
                    float azF = (float)z.Azimuth;

                    var (lonLo, lonHi) = FloatToWordsWS(lonF);
                    var (latLo, latHi) = FloatToWordsWS(latF);
                    var (heightLo, heightHi) = FloatToWordsWS(heightF);
                    var (widthLo, widthHi) = FloatToWordsWS(widthF);
                    var (azLo, azHi) = FloatToWordsWS(azF);

                    allRegisters.Add((zoneBase, lonLo));
                    allRegisters.Add(((ushort)(zoneBase + 1), lonHi));
                    allRegisters.Add(((ushort)(zoneBase + 2), latLo));
                    allRegisters.Add(((ushort)(zoneBase + 3), latHi));
                    allRegisters.Add(((ushort)(zoneBase + 4), heightLo));
                    allRegisters.Add(((ushort)(zoneBase + 5), heightHi));
                    allRegisters.Add(((ushort)(zoneBase + 6), widthLo));
                    allRegisters.Add(((ushort)(zoneBase + 7), widthHi));
                    allRegisters.Add(((ushort)(zoneBase + 8), azLo));
                    allRegisters.Add(((ushort)(zoneBase + 9), azHi));
                }

                System.Diagnostics.Debug.WriteLine($"Total registers to write: {allRegisters.Count}");

                // Rozdělit do dávek po 50
                int offset = 0;
                while (offset < allRegisters.Count)
                {
                    // Re-unlock
                    var (ruOk, ruSt, ruErr) = await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs);
                    if (!ruOk || ruSt != ModbusStateCode.Success)
                        return (false, ruSt, $"Re-unlock failed: {ruErr}");
                    await Task.Delay(250);

                    // Zjistit velikost dávky
                    int chunkSize = Math.Min(MAX_REGS_PER_CHUNK, allRegisters.Count - offset);
                    ushort startAddr = allRegisters[offset].addr;
                    int actualChunkSize = 1;

                    for (int i = 1; i < chunkSize; i++)
                    {
                        if (allRegisters[offset + i].addr != startAddr + i)
                            break;
                        actualChunkSize++;
                    }

                    var chunkData = new ushort[actualChunkSize];
                    for (int i = 0; i < actualChunkSize; i++)
                    {
                        chunkData[i] = allRegisters[offset + i].value;
                    }

                    System.Diagnostics.Debug.WriteLine($"Writing RS485 batch: {actualChunkSize} regs from 0x{startAddr:X4}");

                    // Zápis
                    var (wok, wst, werr) = await WriteHoldingRegistersRtuAsync(s, unitId, startAddr, chunkData, timeoutMs);
                    if (!wok || wst != ModbusStateCode.Success)
                    {
                        System.Diagnostics.Debug.WriteLine($"   FAILED: {werr}");
                        return (false, wst, $"Batch write failed: {werr}");
                    }

                    System.Diagnostics.Debug.WriteLine($"   OK");
                    offset += actualChunkSize;
                    await Task.Delay(150);
                }

                // Lock
                await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { (ushort)0 }, timeoutMs);

                System.Diagnostics.Debug.WriteLine("\n=== ALL ZONES WRITTEN (BATCHED RS485) ===");
                return (true, ModbusStateCode.Success, null);
            }
            catch (Exception ex)
            {
                return (false, ModbusStateCode.UndefinedError, ex.Message);
            }
        }

        private static async Task<(bool ok, ModbusStateCode state, string? error)> WriteMpcZonesBatchedAsyncWithProgress(
    ExportSettings s, byte unitId,
    IReadOnlyList<ActivationZone> zones,
    ushort writeOffset, // PŘIDÁNO: offset pro WLC (44) nebo RTV (176)
    int timeoutMs,
    IProgress<(int current, int total, string message)>? progress)
        {
            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;
            const int MAX_REGS_PER_CHUNK = 50;

            try
            {
                System.Diagnostics.Debug.WriteLine("=== Writing MPC zones via Modbus ASCII (BATCHED FLOAT32) ===");
                System.Diagnostics.Debug.WriteLine($"Write offset: 0x{writeOffset:X4} ({writeOffset})");

                const ushort BASE_ADDR = MPC_BASE_ADDR;
                const int ZONE_STRIDE = MPC_ZONE_STRIDE;
                ushort FIRST_ZONE_BASE = (ushort)(BASE_ADDR + writeOffset); // Použít předaný offset

                var allRegisters = new List<(ushort addr, ushort value)>();

                for (int idx = 0; idx < zones.Count; idx++)
                {
                    var z = zones[idx];
                    int mainZone = z.MainZone + 1;
                    int subZone = z.SubZone + 1;

                    // Pro WLC: 4 hlavní × 5 sub = 20 zón
                    // Pro RTV: 5 hlavních × 7 sub = 35 zón
                    int subsPerMain = (writeOffset == MPCv3WLC_WRITE_OFFSET) ? 5 : 7;
                    int zoneIndex = ((mainZone - 1) * subsPerMain) + (subZone - 1);
                    ushort zoneBase = (ushort)(FIRST_ZONE_BASE + (zoneIndex * ZONE_STRIDE));

                    float lonF = (float)z.Longitude;
                    float latF = (float)z.Latitude;
                    float heightF = (float)z.Height;
                    float widthF = (float)z.Width;
                    float azF = (float)z.Azimuth;

                    var (lonLo, lonHi) = FloatToWordsWS(lonF);
                    var (latLo, latHi) = FloatToWordsWS(latF);
                    var (heightLo, heightHi) = FloatToWordsWS(heightF);
                    var (widthLo, widthHi) = FloatToWordsWS(widthF);
                    var (azLo, azHi) = FloatToWordsWS(azF);

                    // Přidat registry do seznamu (adresa musí být souvislá!)
                    allRegisters.Add((zoneBase, lonLo));
                    allRegisters.Add(((ushort)(zoneBase + 1), lonHi));
                    allRegisters.Add(((ushort)(zoneBase + 2), latLo));
                    allRegisters.Add(((ushort)(zoneBase + 3), latHi));
                    allRegisters.Add(((ushort)(zoneBase + 4), heightLo));
                    allRegisters.Add(((ushort)(zoneBase + 5), heightHi));
                    allRegisters.Add(((ushort)(zoneBase + 6), widthLo));
                    allRegisters.Add(((ushort)(zoneBase + 7), widthHi));
                    allRegisters.Add(((ushort)(zoneBase + 8), azLo));
                    allRegisters.Add(((ushort)(zoneBase + 9), azHi));
                }

                System.Diagnostics.Debug.WriteLine($"Total registers to write: {allRegisters.Count}");

                // Rozdělit do dávek po 50
                int offset = 0;
                while (offset < allRegisters.Count)
                {
                    // Re-unlock
                    var (ruOk, ruSt, ruErr) = await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs);
                    if (!ruOk || ruSt != ModbusStateCode.Success)
                        return (false, ruSt, $"Re-unlock failed: {ruErr}");
                    await Task.Delay(250);

                    // Zjistit velikost dávky
                    int chunkSize = Math.Min(MAX_REGS_PER_CHUNK, allRegisters.Count - offset);
                    ushort startAddr = allRegisters[offset].addr;
                    int actualChunkSize = 1;

                    for (int i = 1; i < chunkSize; i++)
                    {
                        if (allRegisters[offset + i].addr != startAddr + i)
                            break;
                        actualChunkSize++;
                    }

                    var chunkData = new ushort[actualChunkSize];
                    for (int i = 0; i < actualChunkSize; i++)
                    {
                        chunkData[i] = allRegisters[offset + i].value;
                    }

                    System.Diagnostics.Debug.WriteLine($"Writing RS485 batch: {actualChunkSize} regs from 0x{startAddr:X4}");

                    // Report progress
                    progress?.Report((offset, allRegisters.Count, $"Writing registers {offset}/{allRegisters.Count}..."));

                    // Zápis
                    var (wok, wst, werr) = await WriteHoldingRegistersRtuAsync(s, unitId, startAddr, chunkData, timeoutMs);
                    if (!wok || wst != ModbusStateCode.Success)
                    {
                        System.Diagnostics.Debug.WriteLine($"   FAILED: {werr}");
                        return (false, wst, $"Batch write failed: {werr}");
                    }

                    System.Diagnostics.Debug.WriteLine($"   OK");
                    offset += actualChunkSize;
                    await Task.Delay(150);
                }

                // Lock
                progress?.Report((allRegisters.Count, allRegisters.Count, "Locking device..."));
                await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { (ushort)0 }, timeoutMs);

                System.Diagnostics.Debug.WriteLine("\n=== ALL ZONES WRITTEN (BATCHED RS485) ===");
                return (true, ModbusStateCode.Success, null);
            }
            catch (Exception ex)
            {
                return (false, ModbusStateCode.UndefinedError, ex.Message);
            }
        }

        private static bool WriteMpcZonesBatched(
        string host, int port, byte unitId,
        IReadOnlyList<ActivationZone> zones,
        bool isTcp, ExportSettings? settingsForRtu,
        int connectTimeoutMs, int sendTimeoutMs, int receiveTimeoutMs,
        out ModbusStateCode state, out string? error)
        {
            state = ModbusStateCode.Success;
            error = null;

            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;
            const int MAX_REGS_PER_CHUNK = 50;

            try
            {
                System.Diagnostics.Debug.WriteLine($"=== Writing MPC zones via {(isTcp ? "TCP" : "RS485")} (BATCHED) ===");

                // UNLOCK PŘED ZÁPISEM
                if (isTcp)
                {
                    System.Diagnostics.Debug.WriteLine($"Unlocking at 0x{UNLOCK_REGISTER:X4}...");
                    if (!TryWriteUnlockTcp(host, port, unitId, UNLOCK_REGISTER, UNLOCK_VALUE,
                        connectTimeoutMs, sendTimeoutMs, receiveTimeoutMs, out state, out error))
                    {
                        error = $"Failed to unlock: {error}";
                        System.Diagnostics.Debug.WriteLine($"   FAILED: {error}");
                        return false;
                    }
                    System.Diagnostics.Debug.WriteLine($"   OK");
                    System.Threading.Thread.Sleep(800);
                }

                const ushort BASE_ADDR = MPC_BASE_ADDR;       // 0x0300
                const ushort WRITE_OFFSET = MPCv3RTV_WRITE_OFFSET; // 44
                const int ZONE_STRIDE = MPC_ZONE_STRIDE;      // 10
                ushort FIRST_ZONE_BASE = (ushort)(BASE_ADDR + WRITE_OFFSET);

                // Sestavit všechny registry
                var allRegisters = new List<(ushort addr, ushort value)>();

                for (int idx = 0; idx < zones.Count; idx++)
                {
                    var z = zones[idx];
                    int mainZone = z.MainZone + 1;
                    int subZone = z.SubZone + 1;
                    int zoneIndex = ((mainZone - 1) * 5) + (subZone - 1);
                    ushort zoneBase = (ushort)(FIRST_ZONE_BASE + (zoneIndex * ZONE_STRIDE));

                    var (latLo, latHi) = FloatToWordsWS((float)z.Latitude);
                    var (lonLo, lonHi) = FloatToWordsWS((float)z.Longitude);
                    var (heightLo, heightHi) = FloatToWordsWS((float)z.Height);
                    var (widthLo, widthHi) = FloatToWordsWS((float)z.Width);
                    var (azLo, azHi) = FloatToWordsWS((float)z.Azimuth);

                    // OPRAVA: Zapsat VŠECH 10 registrů pro každou zónu
                    allRegisters.Add((zoneBase, lonLo));
                    allRegisters.Add(((ushort)(zoneBase + 1), lonHi));
                    allRegisters.Add(((ushort)(zoneBase + 2), latLo));
                    allRegisters.Add(((ushort)(zoneBase + 3), latHi));
                    allRegisters.Add(((ushort)(zoneBase + 4), heightLo));
                    allRegisters.Add(((ushort)(zoneBase + 5), heightHi));
                    allRegisters.Add(((ushort)(zoneBase + 6), widthLo));
                    allRegisters.Add(((ushort)(zoneBase + 7), widthHi));
                    allRegisters.Add(((ushort)(zoneBase + 8), azLo));
                    allRegisters.Add(((ushort)(zoneBase + 9), azHi));

                    System.Diagnostics.Debug.WriteLine($"Zone {mainZone}-{subZone}: base=0x{zoneBase:X4}, 10 regs");
                }

                System.Diagnostics.Debug.WriteLine($"Total registers to write: {allRegisters.Count}");

                // Připojení pro TCP
                ModbusTcpIp? modbusTcp = null;
                if (isTcp)
                {
                    var protocolParams = BuildProtocolParams(false);
                    protocolParams.ConnectionTimeout = connectTimeoutMs;
                    protocolParams.SendTimeout = sendTimeoutMs;
                    protocolParams.ReceiveTimeout = receiveTimeoutMs;
                    modbusTcp = new ModbusTcpIp($"{host}:{port}", protocolParams);
                }

                try
                {
                    // Rozdělit do dávek po 50
                    int offset = 0;
                    while (offset < allRegisters.Count)
                    {
                        // Re-unlock před každou dávkou
                        if (isTcp)
                        {
                            modbusTcp!.WriteToHoldingRegister(unitId, UNLOCK_REGISTER - 1, UNLOCK_VALUE, out _, "Re-unlock");
                            System.Threading.Thread.Sleep(250);
                        }

                        // Zjistit velikost dávky
                        int chunkSize = Math.Min(MAX_REGS_PER_CHUNK, allRegisters.Count - offset);
                        ushort startAddr = allRegisters[offset].addr;
                        int actualChunkSize = 1;

                        for (int i = 1; i < chunkSize; i++)
                        {
                            if (allRegisters[offset + i].addr != startAddr + i)
                                break;
                            actualChunkSize++;
                        }

                        var chunkData = new ushort[actualChunkSize];
                        for (int i = 0; i < actualChunkSize; i++)
                        {
                            chunkData[i] = allRegisters[offset + i].value;
                        }

                        System.Diagnostics.Debug.WriteLine($"Writing batch: {actualChunkSize} regs from 0x{startAddr:X4}");

                        // Zápis
                        if (isTcp)
                        {
                            // OPRAVA: Použít přímou adresu bez Off1()
                            if (!modbusTcp!.WriteDataToHoldingRegisters(unitId, startAddr, (ushort)actualChunkSize,
                                chunkData, out state, null))
                            {
                                error = $"Batch write failed at 0x{startAddr:X4}, state={state}";
                                System.Diagnostics.Debug.WriteLine($"   FAILED: {error}");
                                return false;
                            }
                        }

                        System.Diagnostics.Debug.WriteLine($"   OK");
                        offset += actualChunkSize;
                        System.Threading.Thread.Sleep(100);
                    }
                }
                finally
                {
                    modbusTcp?.Dispose();
                }

                // Lock zpět
                if (isTcp)
                {
                    System.Diagnostics.Debug.WriteLine($"Locking back...");
                    TryWriteUnlockTcp(host, port, unitId, UNLOCK_REGISTER, 0,
                        connectTimeoutMs, sendTimeoutMs, receiveTimeoutMs, out _, out _);
                }

                System.Diagnostics.Debug.WriteLine("\n=== ALL ZONES WRITTEN (BATCHED) ===");
                return true;
            }
            catch (Exception ex)
            {
                state = ModbusStateCode.UndefinedError;
                error = ex.Message;
                System.Diagnostics.Debug.WriteLine($"EXCEPTION: {ex}");
                return false;
            }
        }


        private static bool WriteMpcByExactRegistersOnlyRtu(
            string comPort, int baudrate, byte unitId,
            IReadOnlyList<ActivationZone> zones,
            int timeoutMs,
            out ModbusStateCode state, out string? error)
        {
            // NOTE: despite the legacy name "...Rtu", this implementation uses Modbus ASCII over RS485 (SerialPort).
            // User requirement: ASCII only, no RTU frames.
            state = ModbusStateCode.Success;
            error = null;

            const int interWriteDelayMs = 80;
            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;

            try
            {
                using var sp = new System.IO.Ports.SerialPort(comPort, baudrate, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = Math.Max(timeoutMs, 3000),
                    WriteTimeout = Math.Max(timeoutMs, 3000),
                    Encoding = Encoding.ASCII
                };

                sp.Open();
                try { sp.DiscardInBuffer(); sp.DiscardOutBuffer(); } catch { }

                static void WriteAsciiHolding(SerialPort sp2, byte slave, ushort addr, ushort[] data, int timeoutMs, string label)
                {
                    string req = data.Length == 1
                        ? BuildAsciiWriteSingle(slave, addr, data[0])
                        : BuildAsciiWriteMultiple(slave, addr, data);

                    var tx = Encoding.ASCII.GetBytes(req);
                    sp2.BaseStream.Write(tx, 0, tx.Length);
                    sp2.BaseStream.Flush();

                    string respAscii = ReadAsciiFrameAsync(sp2, Math.Max(timeoutMs, 3500)).GetAwaiter().GetResult();
                    var parsed = ParseAsciiResponse(respAscii);

                    if (!parsed.ok)
                        throw new InvalidOperationException($"{label}: Bad ASCII response: {parsed.error}");

                    if (parsed.slave != slave)
                        throw new InvalidOperationException($"{label}: Wrong slave in response ({parsed.slave} != {slave})");

                    if ((parsed.func & 0x80) != 0)
                        throw new InvalidOperationException($"{label}: Modbus exception {(parsed.payload.Length > 0 ? parsed.payload[0] : (byte)0):X2}");

                    if (data.Length == 1)
                    {
                        if (parsed.func != 0x06)
                            throw new InvalidOperationException($"{label}: Wrong function (expected 06, got {parsed.func:X2})");

                        if (parsed.payload.Length != 4)
                            throw new InvalidOperationException($"{label}: Bad echo length");

                        ushort echoAddr = (ushort)((parsed.payload[0] << 8) | parsed.payload[1]);
                        ushort echoVal = (ushort)((parsed.payload[2] << 8) | parsed.payload[3]);
                        if (echoAddr != addr || echoVal != data[0])
                            throw new InvalidOperationException($"{label}: Echo mismatch");
                    }
                    else
                    {
                        if (parsed.func != 0x10)
                            throw new InvalidOperationException($"{label}: Wrong function (expected 10, got {parsed.func:X2})");

                        if (parsed.payload.Length != 4)
                            throw new InvalidOperationException($"{label}: Bad echo length");

                        ushort echoAddr = (ushort)((parsed.payload[0] << 8) | parsed.payload[1]);
                        ushort echoQty = (ushort)((parsed.payload[2] << 8) | parsed.payload[3]);
                        if (echoAddr != addr || echoQty != (ushort)data.Length)
                            throw new InvalidOperationException($"{label}: Echo mismatch");
                    }
                }

                // 1) UNLOCK (ASCII)
                WriteAsciiHolding(sp, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs, "UNLOCK");
                System.Threading.Thread.Sleep(250);

                const ushort BASE_ADDR = MPC_BASE_ADDR;       // 0x0300
                const ushort WRITE_OFFSET = MPCv3RTV_WRITE_OFFSET; // 44 -> 0x032C
                const int ZONE_STRIDE = MPC_ZONE_STRIDE;      // 10
                ushort FIRST_ZONE_BASE = (ushort)(BASE_ADDR + WRITE_OFFSET);

                for (int idx = 0; idx < zones.Count; idx++)
                {
                    var z = zones[idx];
                    int mainZone = z.MainZone + 1;
                    int subZone = z.SubZone + 1;

                    // 0..N mapping used throughout this file
                    int zoneIndex = ((mainZone - 1) * 5) + (subZone - 1);
                    ushort zoneBase = (ushort)(FIRST_ZONE_BASE + (zoneIndex * ZONE_STRIDE));
                    string zoneName = $"{mainZone}-{subZone}";

                    float lonF = (float)z.Longitude;
                    float latF = (float)z.Latitude;

                    var (yLo, yHi) = FloatToWordsWS(latF);
                    var (xLo, xHi) = FloatToWordsWS(lonF);

                    ushort lengthCm = ToUInt16(z.Height * 100.0);
                    ushort widthCm = ToUInt16(z.Width * 100.0);
                    ushort az = (ushort)Math.Clamp(z.Azimuth, 0, 359);

                    // Re-unlock per zone (ASCII)
                    WriteAsciiHolding(sp, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs, $"UNLOCK {zoneName}");
                    System.Threading.Thread.Sleep(120);

                    // Lat [0-1]
                    WriteAsciiHolding(sp, unitId, zoneBase, new[] { yLo }, timeoutMs, $"V2X {zoneName} Lat-lo");
                    if (interWriteDelayMs > 0) System.Threading.Thread.Sleep(interWriteDelayMs);

                    WriteAsciiHolding(sp, unitId, (ushort)(zoneBase + 1), new[] { yHi }, timeoutMs, $"V2X {zoneName} Lat-hi");
                    if (interWriteDelayMs > 0) System.Threading.Thread.Sleep(interWriteDelayMs);

                    // Lon [2-3]
                    WriteAsciiHolding(sp, unitId, (ushort)(zoneBase + 2), new[] { xLo }, timeoutMs, $"V2X {zoneName} Lon-lo");
                    if (interWriteDelayMs > 0) System.Threading.Thread.Sleep(interWriteDelayMs);

                    WriteAsciiHolding(sp, unitId, (ushort)(zoneBase + 3), new[] { xHi }, timeoutMs, $"V2X {zoneName} Lon-hi");
                    if (interWriteDelayMs > 0) System.Threading.Thread.Sleep(interWriteDelayMs);

                    // Length [4]
                    WriteAsciiHolding(sp, unitId, (ushort)(zoneBase + 4), new[] { lengthCm }, timeoutMs, $"V2X {zoneName} Len");
                    if (interWriteDelayMs > 0) System.Threading.Thread.Sleep(interWriteDelayMs);

                    // Width [6]
                    WriteAsciiHolding(sp, unitId, (ushort)(zoneBase + 6), new[] { widthCm }, timeoutMs, $"V2X {zoneName} Width");
                    if (interWriteDelayMs > 0) System.Threading.Thread.Sleep(interWriteDelayMs);

                    // Azimuth [8]
                    WriteAsciiHolding(sp, unitId, (ushort)(zoneBase + 8), new[] { az }, timeoutMs, $"V2X {zoneName} Az");
                    if (interWriteDelayMs > 0) System.Threading.Thread.Sleep(interWriteDelayMs);
                }

                // Lock back (0)
                try
                {
                    WriteAsciiHolding(sp, unitId, UNLOCK_REGISTER, new ushort[] { 0 }, timeoutMs, "LOCK");
                }
                catch
                {
                    // ignore
                }

                return true;
            }
            catch (TimeoutException)
            {
                state = ModbusStateCode.Timeout;
                error = "ASCII timeout";
                return false;
            }
            catch (Exception ex)
            {
                state = ModbusStateCode.UndefinedError;
                error = $"Exception during ASCII export: {ex.Message}";
                return false;
            }
        }



        private static async Task<bool> EnsureRemoteEndpointAvailableAsync(string host, int port, int timeoutMs)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                var delayTask = Task.Delay(timeoutMs);
                var finished = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);
                if (finished != connectTask || !client.Connected)
                    return false;

                try { client.Close(); } catch { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<(bool ok, ModbusStateCode state, string? error)> WriteHoldingRegistersRtuAsync(
    ExportSettings s, byte slave, ushort regAddr, ushort[] data, int timeoutMs)
        {
            try
            {
                var parity = ParseParity(s.SerialParity);
                var stop = ParseStopBits(s.SerialStopBits);
                int dataBits = s.SerialDataBits ?? 8;

                using var sp = new SerialPort(
                    s.SerialPortName!,
                    s.SerialBaudrate ?? 19200,
                    parity,
                    dataBits,
                    stop)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = Math.Max(timeoutMs, 3000),
                    WriteTimeout = Math.Max(timeoutMs, 3000),
                    Encoding = Encoding.ASCII
                };
                sp.Open();
                try { sp.DiscardInBuffer(); sp.DiscardOutBuffer(); } catch { }

                // RAW zero-based address for ASCII
                ushort addr = regAddr;

                string req = data.Length == 1
                    ? BuildAsciiWriteSingle(slave, addr, data[0])
                    : BuildAsciiWriteMultiple(slave, addr, data);

                var tx = Encoding.ASCII.GetBytes(req);
                await sp.BaseStream.WriteAsync(tx, 0, tx.Length, CancellationToken.None).ConfigureAwait(false);
                await sp.BaseStream.FlushAsync().ConfigureAwait(false);

                string respAscii = await ReadAsciiFrameAsync(sp, Math.Max(timeoutMs, 3500)).ConfigureAwait(false);
                var parsed = ParseAsciiResponse(respAscii);
                if (!parsed.ok) return (false, ModbusStateCode.CRC, parsed.error);

                if (parsed.slave != slave) return (false, ModbusStateCode.WrongResponse, "Wrong slave");
                if ((parsed.func & 0x80) != 0)
                {
                    byte ex = parsed.payload.FirstOrDefault();
                    string exText = ex switch
                    {
                        0x01 => "Illegal Function",
                        0x02 => "Illegal Data Address",
                        0x03 => "Illegal Data Value",
                        0x04 => "Slave Device Failure",
                        0x05 => "Acknowledge",
                        0x06 => "Slave Device Busy",
                        0x08 => "Memory Parity Error",
                        0x0A => "Gateway Path Unavailable",
                        0x0B => "Gateway Target Failed to Respond",
                        _ => "Unknown"
                    };
                    var stEx = ex == 0x03 ? ModbusStateCode.IllegalDataValue : ModbusStateCode.WrongResponse;
                    return (false, stEx, $"Exception {ex:X2} ({exText})");
                }

                if (data.Length == 1)
                {
                    if (parsed.func != 0x06) return (false, ModbusStateCode.WrongResponse, "Wrong function");
                    if (parsed.payload.Length != 4) return (false, ModbusStateCode.IllegalResponseLength, "Bad echo len");
                    ushort echoAddr = (ushort)((parsed.payload[0] << 8) | parsed.payload[1]);
                    ushort echoVal = (ushort)((parsed.payload[2] << 8) | parsed.payload[3]);
                    if (echoAddr != addr || echoVal != data[0]) return (false, ModbusStateCode.WrongResponse, "Echo mismatch");
                }
                else
                {
                    if (parsed.func != 0x10) return (false, ModbusStateCode.WrongResponse, "Wrong function");
                    if (parsed.payload.Length != 4) return (false, ModbusStateCode.IllegalResponseLength, "Bad echo len");
                    ushort echoAddr = (ushort)((parsed.payload[0] << 8) | parsed.payload[1]);
                    ushort echoQty = (ushort)((parsed.payload[2] << 8) | parsed.payload[3]);
                    if (echoAddr != addr || echoQty != (ushort)data.Length) return (false, ModbusStateCode.WrongResponse, "Echo mismatch");
                }

                return (true, ModbusStateCode.Success, null);
            }
            catch (TimeoutException)
            {
                return (false, ModbusStateCode.Timeout, "ASCII timeout");
            }
            catch (Exception ex)
            {
                return (false, ModbusStateCode.UndefinedError, ex.Message);
            }
        }

        private static byte[] BuildRtuWriteSingleRegisterRequest(byte slave, ushort addr, ushort value)
        {
            // [slave][0x06][addr hi][addr lo][val hi][val lo][crc lo][crc hi]
            var buf = new byte[8];
            int i = 0;

            buf[i++] = slave;
            buf[i++] = 0x06;
            buf[i++] = (byte)(addr >> 8);
            buf[i++] = (byte)(addr & 0xFF);
            buf[i++] = (byte)(value >> 8);
            buf[i++] = (byte)(value & 0xFF);

            ushort crc = Crc16Modbus(buf, 0, 6);
            buf[i++] = (byte)(crc & 0xFF);
            buf[i++] = (byte)(crc >> 8);

            return buf;
        }


        private static byte[] BuildRtuWriteMultipleRegistersRequest(byte slave, ushort startAddr, ushort[] regs)
        {
            int byteCount = regs.Length * 2;
            var payloadLen = 1 + 1 + 2 + 2 + 1 + byteCount; // slave + fn + addr + qty + cnt + data
            var buf = new byte[payloadLen + 2];             // + CRC
            int i = 0;

            buf[i++] = slave;
            buf[i++] = 0x10; // function 16
            buf[i++] = (byte)(startAddr >> 8);
            buf[i++] = (byte)(startAddr & 0xFF);
            buf[i++] = (byte)(regs.Length >> 8);
            buf[i++] = (byte)(regs.Length & 0xFF);
            buf[i++] = (byte)byteCount;

            foreach (var r in regs)
            {
                buf[i++] = (byte)(r >> 8);   // high byte first
                buf[i++] = (byte)(r & 0xFF); // low byte
            }

            ushort crc = Crc16Modbus(buf, 0, payloadLen);
            buf[i++] = (byte)(crc & 0xFF);  // CRC lo
            buf[i++] = (byte)(crc >> 8);    // CRC hi

            return buf;
        }

        private static ushort Crc16Modbus(ReadOnlySpan<byte> data, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[offset + i];
                for (int b = 0; b < 8; b++)
                {
                    bool lsb = (crc & 0x0001) != 0;
                    crc >>= 1;
                    if (lsb) crc ^= 0xA001;
                }
            }
            return crc;
        }

        private static Parity ParseParity(string? s) =>
            string.Equals(s, "Even", StringComparison.OrdinalIgnoreCase) ? Parity.Even :
            string.Equals(s, "Odd", StringComparison.OrdinalIgnoreCase) ? Parity.Odd :
            string.Equals(s, "Mark", StringComparison.OrdinalIgnoreCase) ? Parity.Mark :
            string.Equals(s, "Space", StringComparison.OrdinalIgnoreCase) ? Parity.Space :
            Parity.None;

        private static StopBits ParseStopBits(string? s) =>
            string.Equals(s, "1.5", StringComparison.OrdinalIgnoreCase) ? StopBits.OnePointFive :
            string.Equals(s, "2", StringComparison.OrdinalIgnoreCase) ? StopBits.Two :
            StopBits.One;

        private static ushort[] BuildMpcRegisterBlock(IReadOnlyList<ActivationZone> zones)
        {
            var list = new List<ushort>(zones.Count * 9);

            foreach (var z in zones)
            {
                int main = Math.Clamp(z.MainZone, 0, 3);
                int sub = Math.Clamp(z.SubZone, 0, 4);

                // Float32 WS (LO first, then HI)
                var (latLo, latHi) = FloatToWordsWS((float)z.Latitude);
                var (lonLo, lonHi) = FloatToWordsWS((float)z.Longitude);

                ushort widthCm = ToUInt16(z.Width * 100.0);
                ushort heightCm = ToUInt16(z.Height * 100.0);
                ushort az = (ushort)Math.Clamp(z.Azimuth, 0, 359);

                list.Add((ushort)main);   // 1
                list.Add((ushort)sub);    // 2
                list.Add(latLo);          // 3 (WS)
                list.Add(latHi);          // 4
                list.Add(lonLo);          // 5
                list.Add(lonHi);          // 6
                list.Add(widthCm);        // 7
                list.Add(heightCm);       // 8
                list.Add(az);             // 9
            }

            return list.ToArray();
        }

        // Helpers for packing values into registers

        // Big-endian word order: HI word first, then LO word (adjust if your gateway expects swapped order)
        private static void PutInt32(List<ushort> regs, int value)
        {
            unchecked
            {
                ushort hi = (ushort)((value >> 16) & 0xFFFF);
                ushort lo = (ushort)(value & 0xFFFF);
                regs.Add(hi);
                regs.Add(lo);
            }
        }

        private static ushort ToUInt16(double value)
        {
            if (value <= 0) return 0;
            if (value >= 65535) return 65535;
            return (ushort)Math.Round(value, MidpointRounding.AwayFromZero);
        }


        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            if (!TryOfferSaveChangesIfDirty())
                return;
            Close();
        }

        private void SelectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProfileChange) return;

            var name = SelectComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(name)) return;
            var prof = _profiles.FirstOrDefault(p => p.Name == name);
            if (prof == null) return;

            if (!TryOfferSaveChangesIfDirty())
            {
                // Revert selection
                _suppressProfileChange = true;
                if (!string.IsNullOrWhiteSpace(_loadedProfileName) && _profiles.Any(p => p.Name == _loadedProfileName))
                    SelectComboBox.SelectedItem = _loadedProfileName;
                else if (_profiles.Count > 0)
                    SelectComboBox.SelectedIndex = 0;
                else
                    SelectComboBox.SelectedItem = null;
                _suppressProfileChange = false;
                return;
            }

            PopulateForm(prof.Settings);
            CaptureLoadedSnapshot(prof.Settings, prof.Name);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveChangesToCurrentProfile())
                return;

            MessageBox.Show("Profile updated.", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            // Ask for new unique profile name
            string proposed = string.IsNullOrWhiteSpace(_loadedProfileName) ? "New Profile" : $"{_loadedProfileName} (copy)";
            string name;
            while (true)
            {
                name = Interaction.InputBox("Enter new profile name:", "New profile", proposed).Trim();
                if (string.IsNullOrWhiteSpace(name))
                    return; // cancelled
                if (_profiles.Any(p => string.Equals(p.Name, name)))
                {
                    MessageBox.Show("Profile with this name already exists.", "New profile", MessageBoxButton.OK, MessageBoxImage.Warning);
                    proposed = name;
                    continue;
                }
                break;
            }

            // Clear UI fields (sets TCP by default and empties all textboxes)
            ClearForm();

            // Capture cleared/default settings from the UI
            var emptySettings = ExportSettings.FromWindow(this);
            // Ensure sane defaults for things not in UI
            emptySettings.SerialHandshake ??= "None";

            // Add new profile with cleared settings and persist
            var profile = new ExportProfile { Name = name, Settings = emptySettings };
            _profiles.Add(profile);
            ExportSettingsStorage.Save(_profiles);

            // Select the new profile and set snapshot to the cleared state
            RefreshProfilesList(selectName: name);
            // UI is already cleared; just capture the snapshot for dirty tracking
            CaptureLoadedSnapshot(emptySettings, name);

            MessageBox.Show("New profile created.", "New", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            var oldName = SelectComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(oldName))
            {
                MessageBox.Show("Select a profile to rename.", "Rename", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var prof = _profiles.FirstOrDefault(p => p.Name == oldName);
            if (prof == null) return;

            var newName = Interaction.InputBox("New profile name:", "Rename profile", oldName).Trim();
            if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, oldName)) return;

            if (_profiles.Any(p => string.Equals(p.Name, newName)))
            {
                MessageBox.Show("Profile already exists.", "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            prof.Name = newName;
            ExportSettingsStorage.Save(_profiles);
            RefreshProfilesList(selectName: newName);

            _loadedProfileName = newName;
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var name = SelectComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Select a profile for deletion.", "Delete", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var prof = _profiles.FirstOrDefault(p => p.Name == name);
            if (prof == null) return;

            if (MessageBox.Show($"Remove profile '{name}'?", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _profiles.Remove(prof);
            ExportSettingsStorage.Save(_profiles);

            if (_profiles.Count > 0)
            {
                var next = _profiles[0].Name;
                RefreshProfilesList(selectName: next);
                PopulateForm(_profiles[0].Settings);
                CaptureLoadedSnapshot(_profiles[0].Settings, _profiles[0].Name);
            }
            else
            {
                RefreshProfilesList(selectName: null);
                ClearForm();
                _isDirty = false;
                _loadedProfileName = null;
                _loadedSettings = null;
            }
        }

        private void ConnectionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyConnectionLayout();

        private void ApplyConnectionLayout()
        {
            var selected = (ConnectionComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim();
            bool isTcp = string.Equals(selected, "Modbus TCP", StringComparison.OrdinalIgnoreCase);
            bool isSerial = string.Equals(selected, "Serial port", StringComparison.OrdinalIgnoreCase);
            bool isTunnel = string.Equals(selected, "Serial tunnel", StringComparison.OrdinalIgnoreCase);

            // Chytré doplnění hodnot při přepínání
            try
            {
                if (isTcp)
                {
                    // Přechod na TCP – pokud TCP pole jsou prázdná, ale tunnel něco má, zkopíruj je
                    if (TcpHostTextBox != null && string.IsNullOrWhiteSpace(TcpHostTextBox.Text) &&
                        TunnelRemoteHostTextBox != null && !string.IsNullOrWhiteSpace(TunnelRemoteHostTextBox.Text))
                    {
                        TcpHostTextBox.Text = TunnelRemoteHostTextBox.Text;
                    }

                    if (TcpPortTextBox != null && string.IsNullOrWhiteSpace(TcpPortTextBox.Text) &&
                        TunnelRemotePortTextBox != null && !string.IsNullOrWhiteSpace(TunnelRemotePortTextBox.Text))
                    {
                        TcpPortTextBox.Text = TunnelRemotePortTextBox.Text;
                    }
                }
                else if (isTunnel)
                {
                    // Přechod na tunnel – pokud tunnel pole jsou prázdná, ale TCP něco má, zkopíruj je
                    if (TunnelRemoteHostTextBox != null && string.IsNullOrWhiteSpace(TunnelRemoteHostTextBox.Text) &&
                        TcpHostTextBox != null && !string.IsNullOrWhiteSpace(TcpHostTextBox.Text))
                    {
                        TunnelRemoteHostTextBox.Text = TcpHostTextBox.Text;
                    }

                    if (TunnelRemotePortTextBox != null && string.IsNullOrWhiteSpace(TunnelRemotePortTextBox.Text) &&
                        TcpPortTextBox != null && !string.IsNullOrWhiteSpace(TcpPortTextBox.Text))
                    {
                        TunnelRemotePortTextBox.Text = TcpPortTextBox.Text;
                    }
                }
            }
            catch
            {
                // best-effort, nechci shodit okno kvůli UI chybě
            }

            TcpFields.Visibility = isTcp ? Visibility.Visible : Visibility.Collapsed;
            SerialFields.Visibility = isSerial ? Visibility.Visible : Visibility.Collapsed;

            if (TunnelFields != null)
                TunnelFields.Visibility = isTunnel ? Visibility.Visible : Visibility.Collapsed;

            MarkDirtyIfActive();
        }




        private void PopulateForm(ExportSettings s)
        {
            if (s == null) return;

            // Connection selection
            try
            {
                // Prefer explicit tunnel flag when present, otherwise fall back to IsTcpSelected
                if (!string.IsNullOrWhiteSpace(s.TunnelRemoteHost))
                {
                    // choose the "Serial tunnel" item if present
                    foreach (var it in ConnectionComboBox.Items.OfType<ComboBoxItem>())
                    {
                        if (string.Equals(it.Content?.ToString(), "Serial tunnel", StringComparison.OrdinalIgnoreCase))
                        {
                            ConnectionComboBox.SelectedItem = it;
                            break;
                        }
                    }
                }
                else if (s.IsTcpSelected)
                {
                    foreach (var it in ConnectionComboBox.Items.OfType<ComboBoxItem>())
                    {
                        if (string.Equals(it.Content?.ToString(), "Modbus TCP", StringComparison.OrdinalIgnoreCase))
                        {
                            ConnectionComboBox.SelectedItem = it;
                            break;
                        }
                    }
                }
                else
                {
                    // serial
                    foreach (var it in ConnectionComboBox.Items.OfType<ComboBoxItem>())
                    {
                        if (string.Equals(it.Content?.ToString(), "Serial port", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(it.Content?.ToString(), "Serial", StringComparison.OrdinalIgnoreCase))
                        {
                            ConnectionComboBox.SelectedItem = it;
                            break;
                        }
                    }
                }
            }
            catch { /* best-effort */ }

            // TCP fields
            try
            {
                TcpHostTextBox.Text = s.TcpHost ?? string.Empty;
                TcpPortTextBox.Text = s.TcpPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch { }

            // Serial fields
            try
            {
                SerialPortTextBox.Text = s.SerialPortName ?? string.Empty;
                SerialBaudTextBox.Text = s.SerialBaudrate?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                SerialDataBitsTextBox.Text = s.SerialDataBits?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                SerialParityTextBox.Text = s.SerialParity ?? string.Empty;
                SerialStopBitsTextBox.Text = s.SerialStopBits ?? string.Empty;
            }
            catch { }

            // Modem
            try
            {
                ModemTextBox.Text = s.ModemRaw ?? string.Empty;
            }
            catch { }

            // Tunnel fields (restore into UI if any)
            try
            {
                TunnelRemoteHostTextBox.Text = s.TunnelRemoteHost ?? string.Empty;
                TunnelRemotePortTextBox.Text = s.TunnelRemotePort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch { }

        }

        private void ClearForm()
        {
            _suppressFormEvents = true;

            ConnectionComboBox.SelectedIndex = 0;
            ApplyConnectionLayout();

            TcpHostTextBox.Text = "";
            TcpPortTextBox.Text = "";

            SerialPortTextBox.Text = "";
            SerialBaudTextBox.Text = "";
            SerialDataBitsTextBox.Text = "";
            SerialParityTextBox.Text = "";
            SerialStopBitsTextBox.Text = "";

            ModemTextBox.Text = "";

            _suppressFormEvents = false;
            _isDirty = false;
        }

        private void RefreshProfilesList(string? selectName = null)
        {
            var names = _profiles.Select(p => p.Name).ToList();
            _suppressProfileChange = true;
            SelectComboBox.ItemsSource = names;

            if (!string.IsNullOrWhiteSpace(selectName) && names.Contains(selectName))
            {
                SelectComboBox.SelectedItem = selectName;
            }
            else if (names.Count > 0)
            {
                SelectComboBox.SelectedIndex = 0;
            }
            else
            {
                SelectComboBox.SelectedItem = null;
            }
            _suppressProfileChange = false;
        }

        // ===== Change tracking =====

        private void AttachChangeTracking()
        {
            AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnAnyTextChanged));
        }

        private void OnAnyTextChanged(object sender, TextChangedEventArgs e) => MarkDirtyIfActive();

        private void MarkDirtyIfActive()
        {
            if (_suppressFormEvents) return;
            _isDirty = true;
        }

        private void CaptureLoadedSnapshot(ExportSettings s, string name)
        {
            _loadedProfileName = name;
            _loadedSettings = CloneSettings(s);
            _isDirty = false;
        }

        private static ExportSettings CloneSettings(ExportSettings s) => ExportSettings.CloneFrom(s);

        private static bool AreSettingsEqual(ExportSettings a, ExportSettings b)
        {
            if (a == null || b == null) return false;
            return
                a.IsTcpSelected == b.IsTcpSelected &&
                (a.TcpHost ?? "") == (b.TcpHost ?? "") &&
                a.TcpPort == b.TcpPort &&
                (a.SerialPortName ?? "") == (b.SerialPortName ?? "") &&
                a.SerialBaudrate == b.SerialBaudrate &&
                a.SerialDataBits == b.SerialDataBits &&
                (a.SerialParity ?? "") == (b.SerialParity ?? "") &&
                (a.SerialStopBits ?? "") == (b.SerialStopBits ?? "") &&
                (a.SerialHandshake ?? "") == (b.SerialHandshake ?? "") &&
                (a.ModemRaw ?? "") == (b.ModemRaw ?? "") &&
                a.ModemDec == b.ModemDec &&
                (a.ModemHex ?? "") == (b.ModemHex ?? "");
        }

        // New helper: update current profile
        private bool SaveChangesToCurrentProfile()
        {
            var selectedName = SelectComboBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selectedName))
            {
                MessageBox.Show("Select a profile first (or use New to create one).", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var prof = _profiles.FirstOrDefault(p => p.Name == selectedName);
            if (prof == null)
            {
                MessageBox.Show("Selected profile not found.", "Save", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var current = ExportSettings.FromWindow(this);
            prof.Settings = current;
            ExportSettingsStorage.Save(_profiles);

            RefreshProfilesList(selectName: selectedName);
            CaptureLoadedSnapshot(current, selectedName);
            return true;
        }

        // Offer to save changes to current profile (no "save as new" here)
        private bool TryOfferSaveChangesIfDirty()
        {
            if (!_isDirty)
                return true;

            var current = ExportSettings.FromWindow(this);
            if (_loadedSettings != null && AreSettingsEqual(current, _loadedSettings))
            {
                _isDirty = false;
                return true;
            }

            var selectedName = SelectComboBox.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(selectedName))
            {
                var res = MessageBox.Show(
                    $"Save changes to profile '{selectedName}'?",
                    "Unsaved changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.Cancel)
                    return false;

                if (res == MessageBoxResult.Yes)
                    return SaveChangesToCurrentProfile();

                // No = discard
                _isDirty = false;
                return true;
            }
            else
            {
                // No profile selected – cannot save; offer to discard or cancel
                var res = MessageBox.Show(
                    "There is no selected profile. Use New to create one.\nDiscard changes?",
                    "Unsaved changes",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Question);

                if (res != MessageBoxResult.OK)
                    return false;

                _isDirty = false;
                return true;
            }
        }

        private bool TryBeginBusy(string message)
        {
            // If already busy, do not override (nested read/export).
            if (BusyOverlay.Visibility == Visibility.Visible)
                return false;
            ShowBusy(message);
            return true;
        }

        private static bool TryValidateSettings(ExportSettings s, out string message)
        {
            if (s == null)
            {
                message = "Settings object is null.";
                return false;
            }

            var errors = new List<string>();

            // Slave / unit id
            if (!s.ModemDec.HasValue)
                errors.Add("UnitId (ModemDec) is required.");
            else if (s.ModemDec < 1 || s.ModemDec > 247)
                errors.Add("UnitId must be in range 1..247.");

            // Determine selected connection mode from UI selection text, if available
            // Fallback to s.IsTcpSelected / tunnel fields if UI is not accessible here.
            string mode = "";
            try
            {
                // Attempt to infer from current window controls (optional best-effort)
                var selected = (Application.Current?.Windows
                    .OfType<ExportWindow>()
                    .FirstOrDefault()?.ConnectionComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim();

                mode = (selected ?? "").ToLowerInvariant();
            }
            catch { /* best effort */ }

            bool isTcp = mode == "modbus tcp" || (string.IsNullOrWhiteSpace(mode) && s.IsTcpSelected);
            bool isTunnel = mode == "serial tunnel" || (!string.IsNullOrWhiteSpace(s.TunnelRemoteHost) || s.TunnelRemotePort.HasValue);
            bool isSerial = mode == "serial port" || (!isTcp && !isTunnel);

            if (isTunnel)
            {
                // Validate tunnel endpoint only (RTU-over-TCP), do NOT require serial COM fields here
                if (string.IsNullOrWhiteSpace(s.TunnelRemoteHost))
                    errors.Add("Tunnel host is required.");
                if (!s.TunnelRemotePort.HasValue)
                    errors.Add("Tunnel port is required.");
                else if (s.TunnelRemotePort < 1 || s.TunnelRemotePort > 65535)
                    errors.Add("Tunnel port must be 1..65535.");
            }
            else if (isTcp)
            {
                // Standard Modbus TCP validation
                if (string.IsNullOrWhiteSpace(s.TcpHost))
                    errors.Add("Modbus TCP host is required.");
                if (!s.TcpPort.HasValue)
                    errors.Add("Modbus TCP port is required.");
                else if (s.TcpPort < 1 || s.TcpPort > 65535)
                    errors.Add("Modbus TCP port must be 1..65535.");
            }
            else
            {
                // Pure serial (RTU) validation — no host/port required
                if (string.IsNullOrWhiteSpace(s.SerialPortName))
                    errors.Add("Serial port is required.");
                if (!s.SerialBaudrate.HasValue || s.SerialBaudrate <= 0)
                    errors.Add("Baudrate must be a positive number.");
                if (!s.SerialDataBits.HasValue || s.SerialDataBits < 5 || s.SerialDataBits > 8)
                    errors.Add("Data bits must be 5..8.");
                if (string.IsNullOrWhiteSpace(s.SerialParity) || !IsValidParity(s.SerialParity))
                    errors.Add("Parity must be one of: None, Even, Odd, Mark, Space.");
                if (string.IsNullOrWhiteSpace(s.SerialStopBits) || !IsValidStopBits(s.SerialStopBits))
                    errors.Add("Stop bits must be one of: 1, 1.5, 2.");
            }

            if (errors.Count > 0)
            {
                message = string.Join("\n", errors);
                return false;
            }

            message = "";
            return true;
        }

        private static bool IsValidParity(string value) =>
            value.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Even", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Odd", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Mark", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Space", StringComparison.OrdinalIgnoreCase);

        private static bool IsValidStopBits(string value) =>
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1.5", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("2", StringComparison.OrdinalIgnoreCase);

        private static bool WriteHoldingRegistersTcpChunked(
            ModbusTcpIp modbus,
            byte slave,
            ushort startAddr,
            ushort[] regs,
            out ModbusStateCode state)
        {
            // Backward-compatible wrapper uses default size/delay
            return WriteHoldingRegistersTcpChunkedSized(modbus, slave, startAddr, regs, MAX_REGS_PER_REQUEST, interChunkDelayMs: 100, out state);
        }

        private static bool WriteHoldingRegistersTcpChunkedSized(
        ModbusTcpIp modbus,
        byte slave,
        ushort startAddr,
        ushort[] regs,
        int maxRegsPerRequest,
        int interChunkDelayMs,
        out ModbusStateCode state)
        {
            state = ModbusStateCode.Success;
            int offset = 0;
            while (offset < regs.Length)
            {
                int count = Math.Min(maxRegsPerRequest, regs.Length - offset);
                var slice = new ushort[count];
                Array.Copy(regs, offset, slice, 0, count);

                ushort addr = (ushort)(startAddr + offset);
                bool ok = modbus.WriteDataToHoldingRegisters(
                    slaveAddr: slave,
                    regAddr: addr,
                    regCount: (ushort)count,
                    data: slice,
                    state: out state,
                    moduleRefName: null);

                if (!ok || state != ModbusStateCode.Success)
                    return false;

                if (interChunkDelayMs > 0)
                    System.Threading.Thread.Sleep(interChunkDelayMs);

                offset += count;
            }
            return true;
        }

        private static async Task<(bool ok, ModbusStateCode state, string? error)> WriteHoldingRegistersRtuChunkedAsync(
        ExportSettings s, byte slave, ushort startAddr, ushort[] regs, int timeoutMs)
        {
            return await WriteHoldingRegistersRtuAsync(s, slave, startAddr, regs, timeoutMs);
        }

        private static string SaveExportError(Exception ex, string transport, string target, byte unitId, ushort baseAddr, int regsLen, ModbusStateCode? state = null)
        {
            var ts = DateTime.Now;
            var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"V2X_Export_{ts:yyyyMMdd_HHmmss}.log");
            var details =
                    $@"[{ts:O}] Export error
                    Transport   : {transport}
                    Target      : {target}
                    UnitId      : {unitId}
                    BaseAddr    : 0x{baseAddr:X4}
                    RegsLength  : {regsLen}
                    State       : {(state?.ToString() ?? "-")}
                    Exception   :
                    {ex}";

            try { System.IO.File.WriteAllText(logPath, details); } catch { /* ignore */ }
            try { System.Windows.Clipboard.SetText(details); } catch { /* ignore non-STA/permissions */ }
            return logPath;
        }


        // TCP retry wrapper (base, base-1, base+1)
        private static bool TryWriteTcpWithFallback(ModbusTcpIp modbus, byte unitId, ushort baseAddr, ushort[] regs, out ModbusStateCode state, out ushort usedBase)
        {
            usedBase = baseAddr;
            if (WriteHoldingRegistersTcpChunked(modbus, unitId, baseAddr, regs, out state))
                return state == ModbusStateCode.Success;

            if (baseAddr > 0)
            {
                ushort altMinus = (ushort)(baseAddr - 1);
                if (WriteHoldingRegistersTcpChunked(modbus, unitId, altMinus, regs, out state) && state == ModbusStateCode.Success)
                {
                    usedBase = altMinus;
                    return true;
                }
            }

            if (baseAddr < ushort.MaxValue)
            {
                ushort altPlus = (ushort)(baseAddr + 1);
                if (WriteHoldingRegistersTcpChunked(modbus, unitId, altPlus, regs, out state) && state == ModbusStateCode.Success)
                {
                    usedBase = altPlus;
                    return true;
                }
            }

            return false;
        }

        private static bool TryWriteTcpWithFallback(
            ModbusTcpIp modbus, byte unitId, ushort baseAddr, ushort[] regs,
            int maxRegsPerRequest, int interChunkDelayMs,
            out ModbusStateCode state, out ushort usedBase)
        {
            usedBase = baseAddr;
            if (WriteHoldingRegistersTcpChunkedSized(modbus, unitId, baseAddr, regs, maxRegsPerRequest, interChunkDelayMs, out state))
                return state == ModbusStateCode.Success;

            if (baseAddr > 0)
            {
                ushort altMinus = (ushort)(baseAddr - 1);
                if (WriteHoldingRegistersTcpChunkedSized(modbus, unitId, altMinus, regs, maxRegsPerRequest, interChunkDelayMs, out state) && state == ModbusStateCode.Success)
                {
                    usedBase = altMinus;
                    return true;
                }
            }

            if (baseAddr < ushort.MaxValue)
            {
                ushort altPlus = (ushort)(baseAddr + 1);
                if (WriteHoldingRegistersTcpChunkedSized(modbus, unitId, altPlus, regs, maxRegsPerRequest, interChunkDelayMs, out state) && state == ModbusStateCode.Success)
                {
                    usedBase = altPlus;
                    return true;
                }
            }

            return false;
        }

        private static async Task<(bool ok, ModbusStateCode state, ushort usedBase, string? error)> TryWriteRtuWithFallbackAsync(
            ExportSettings s, byte unitId, ushort baseAddr, ushort[] regs, int timeoutMs)
        {
            var first = await WriteHoldingRegistersRtuChunkedAsync(s, unitId, baseAddr, regs, timeoutMs);
            if (first.ok && first.state == ModbusStateCode.Success)
                return (true, first.state, baseAddr, null);

            if (baseAddr > 0)
            {
                ushort altMinus = (ushort)(baseAddr - 1);
                var second = await WriteHoldingRegistersRtuChunkedAsync(s, unitId, altMinus, regs, timeoutMs);
                if (second.ok && second.state == ModbusStateCode.Success)
                    return (true, second.state, altMinus, null);
            }

            if (baseAddr < ushort.MaxValue)
            {
                ushort altPlus = (ushort)(baseAddr + 1);
                var third = await WriteHoldingRegistersRtuChunkedAsync(s, unitId, altPlus, regs, timeoutMs);
                if (third.ok && third.state == ModbusStateCode.Success)
                    return (true, third.state, altPlus, null);
                return (false, third.state, altPlus, third.error);
            }

            return (false, first.state, baseAddr, first.error);
        }

        private static void TrySetProtocolTimeouts(object protocolParams, int connectMs = 3000, int sendMs = 3000, int receiveMs = 3000)
        {
            var t = protocolParams.GetType();
            void Set(string name, int val)
            {
                var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p != null && p.CanWrite && p.PropertyType == typeof(int))
                    p.SetValue(protocolParams, val);
            }


            Set("ConnectTimeoutMs", connectMs);
            Set("ConnectTimeout", connectMs);
            Set("TcpConnectTimeoutMs", connectMs);
            Set("TcpConnectTimeout", connectMs);

            Set("SendTimeoutMs", sendMs);
            Set("SendTimeout", sendMs);
            Set("TcpSendTimeoutMs", sendMs);
            Set("TcpSendTimeout", sendMs);

            Set("ReceiveTimeoutMs", receiveMs);
            Set("ReceiveTimeout", receiveMs);
            Set("TcpReceiveTimeoutMs", receiveMs);
            Set("TcpReceiveTimeout", receiveMs);
        }

        // Also set timeouts on instance objects where available
        private static void TrySetInstanceTimeouts(object modbus, int connectMs, int sendMs, int receiveMs)
        {
            const System.Reflection.BindingFlags BF =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;

            void SetInt(string name, int val)
            {
                var p = modbus.GetType().GetProperty(name, BF);
                if (p != null && p.CanWrite && p.PropertyType == typeof(int))
                {
                    p.SetValue(modbus, val);
                    return;
                }
                var f = modbus.GetType().GetField(name, BF);
                if (f != null && f.FieldType == typeof(int))
                    f.SetValue(modbus, val);
            }

            SetInt("ConnectTimeoutMs", connectMs);
            SetInt("ConnectTimeout", connectMs);
            SetInt("TcpConnectTimeoutMs", connectMs);
            SetInt("TcpConnectTimeout", connectMs);

            SetInt("SendTimeoutMs", sendMs);
            SetInt("SendTimeout", sendMs);
            SetInt("TcpSendTimeoutMs", sendMs);
            SetInt("TcpSendTimeout", sendMs);

            SetInt("ReceiveTimeoutMs", receiveMs);
            SetInt("ReceiveTimeout", receiveMs);
            SetInt("TcpReceiveTimeoutMs", receiveMs);
            SetInt("TcpReceiveTimeout", receiveMs);
        }

        private static void TryCallConnect(object modbus)
        {
            const System.Reflection.BindingFlags BF =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;

            var names = new[] { "Connect", "Open", "Initialize", "Init" };
            foreach (var n in names)
            {
                var m = modbus.GetType().GetMethod(n, BF, new Type[0]);
                if (m != null)
                {
                    try { m.Invoke(modbus, null); } catch { }
                    break;
                }
            }
        }

        // Update helper to optionally carry a full semicolon endpoint too
        private static void TrySetProtocolEndpoint(object protocolParams, string host, int port, string? fullEndpoint = null)
        {
            var t = protocolParams.GetType();
            void SetStr(string name, string val)
            {
                var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p != null && p.CanWrite && p.PropertyType == typeof(string))
                    p.SetValue(protocolParams, val);
            }
            void SetInt(string name, int val)
            {
                var p = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (p != null && p.CanWrite && p.PropertyType == typeof(int))
                    p.SetValue(protocolParams, val);
            }

            var hostOnly = host.Split(new[] { ':', ';', ',' }, 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            var ep = fullEndpoint ?? $"{hostOnly};{port};0";

            // Host/Port hints
            SetStr("TcpServerIp", hostOnly);
            SetStr("ServerIp", hostOnly);
            SetStr("IpAddress", hostOnly);
            SetStr("Host", hostOnly);
            SetStr("HostName", hostOnly);
            SetInt("TcpServerPort", port);
            SetInt("ServerPort", port);
            SetInt("Port", port);
            SetInt("TcpPort", port);

            // Full endpoint (semicolon-based) for builds that parse GetEndPointIp
            SetStr("EndPointIp", ep);
            SetStr("EndPoint", ep);
            SetStr("TcpEndPoint", ep);
            SetStr("ModbusTcpEndPoint", ep);
        }

        private static (int SuccessiveRequestDelay, int ConnectionTimeout, int SendTimeout, int ReceiveTimeout, int ReceiveAgainTimeout)
        ResolveProtocolCfg()
        {
            // Defaults if Config.CfgData is not available
            var defaults = (SuccessiveRequestDelay: 100, ConnectionTimeout: 2000, SendTimeout: 1500, ReceiveTimeout: 1500, ReceiveAgainTimeout: 1500);

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? cfgType = null;
                    try { cfgType = asm.GetTypes().FirstOrDefault(t => t.Name == "Config"); } catch { /* reflection-only or dynamic asm */ }
                    if (cfgType == null) continue;

                    var cfgDataProp = cfgType.GetProperty("CfgData", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (cfgDataProp == null) continue;

                    var cfgData = cfgDataProp.GetValue(null);
                    if (cfgData == null) continue;

                    int GetInt(string name, int def)
                    {
                        var p = cfgData.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (p != null && p.PropertyType == typeof(int))
                        {
                            var val = (int?)p.GetValue(cfgData);
                            if (val.HasValue) return val.Value;
                        }
                        return def;
                    }

                    var srd = GetInt("SuccessiveRequestDelay", defaults.SuccessiveRequestDelay);
                    var ct = GetInt("ConnectionTimeout", defaults.ConnectionTimeout);
                    var st = GetInt("SendTimeout", defaults.SendTimeout);
                    var rt = GetInt("ReceiveTimeout", defaults.ReceiveTimeout);
                    var rat = GetInt("ReceiveAgainTimeout", defaults.ReceiveAgainTimeout);

                    return (srd, ct, st, rt, rat);
                }
            }
            catch
            {
                // ignore and use defaults
            }

            return defaults;
        }

        // Add inside ExportWindow (near other helpers)
        private static void TrySetModbusEndpoint(object modbus, string fullEndpoint)
        {
            // Try common property/field names on the object and its base types
            var names = new[] { "EndPointIp", "EndPoint", "TcpEndPoint", "ModbusTcpEndPoint", "_endPointIp", "_endPoint" };

            static void SetStrMember(Type t, object target, string name, string val)
            {
                const System.Reflection.BindingFlags BF =
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;

                var p = t.GetProperty(name, BF);
                if (p != null && p.CanWrite && p.PropertyType == typeof(string))
                {
                    p.SetValue(target, val);
                    return;
                }
                var f = t.GetField(name, BF);
                if (f != null && f.FieldType == typeof(string))
                {
                    f.SetValue(target, val);
                }
            }

            for (var t = modbus.GetType(); t != null; t = t.BaseType!)
            {
                foreach (var n in names)
                    SetStrMember(t, modbus, n, fullEndpoint);
            }
        }

        // Add this helper inside ExportWindow (near other helpers)
        private static void TrySetGlobalModbusEndpoint(string fullEndpoint)
        {
            // Set likely static properties/fields on Modbus types (and base types) to a semicolon endpoint.
            var names = new[] { "EndPointIp", "EndPoint", "TcpEndPoint", "ModbusTcpEndPoint", "_endPointIp", "_endPoint" };

            static void SetStaticStrMember(Type t, string name, string val)
            {
                const System.Reflection.BindingFlags BF =
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static;

                var p = t.GetProperty(name, BF);
                if (p != null && p.CanWrite && p.PropertyType == typeof(string))
                {
                    p.SetValue(null, val);
                    return;
                }
                var f = t.GetField(name, BF);
                if (f != null && f.FieldType == typeof(string))
                {
                    f.SetValue(null, val);
                }
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    // Focus on Modbus* types in ModbusNewLib
                    if (t.Namespace?.Contains("ModbusNewLib", StringComparison.OrdinalIgnoreCase) != true)
                        continue;
                    if (!t.Name.StartsWith("Modbus", StringComparison.OrdinalIgnoreCase))
                        continue;

                    for (var cur = t; cur != null; cur = cur.BaseType)
                    {
                        foreach (var n in names)
                            SetStaticStrMember(cur, n, fullEndpoint);
                    }
                }
            }
        }

        // Add inside ExportWindow (near other helpers)
        private static ModbusTcpIp CreateModbusTcpIp(string hostOnly, int port, object protocolParams, out string endpointUsed)
        {
            var t = typeof(ModbusTcpIp);
            // Prefer ctor(string host, int port, ProtocolParams)
            var ctors = t.GetConstructors();
            foreach (var c in ctors)
            {
                var p = c.GetParameters();
                if (p.Length == 3 &&
                    p[0].ParameterType == typeof(string) &&
                    p[1].ParameterType == typeof(int) &&
                    p[2].ParameterType.IsAssignableFrom(protocolParams.GetType()))
                {
                    endpointUsed = $"{hostOnly}:{port}";
                    return (ModbusTcpIp)c.Invoke(new object[] { hostOnly, port, protocolParams });
                }
            }
            // Next: ctor(string host, int port)
            foreach (var c in ctors)
            {
                var p = c.GetParameters();
                if (p.Length == 2 &&
                    p[0].ParameterType == typeof(string) &&
                    p[1].ParameterType == typeof(int))
                {
                    endpointUsed = $"{hostOnly}:{port}";
                    var mod = (ModbusTcpIp)c.Invoke(new object[] { hostOnly, port });
                    // Try to apply protocol params, host/port directly on the instance as a best-effort
                    TrySetProtocolEndpoint(protocolParams, hostOnly, port);
                    TrySetProtocolTimeouts(protocolParams);
                    TrySetHostPortOnModbus(mod, hostOnly, port);
                    return mod;
                }
            }
            // Fallback: ctor(string endPoint, ProtocolParams) with a safe semicolon endpoint
            var safe = $"{hostOnly};{port};0";
            foreach (var c in ctors)
            {
                var p = c.GetParameters();
                if (p.Length == 2 &&
                    p[0].ParameterType == typeof(string) &&
                    p[1].ParameterType.IsAssignableFrom(protocolParams.GetType()))
                {
                    endpointUsed = safe;
                    return (ModbusTcpIp)c.Invoke(new object[] { safe, protocolParams });
                }
            }
            // Last resort: any ctor(string)
            foreach (var c in ctors)
            {
                var p = c.GetParameters();
                if (p.Length == 1 && p[0].ParameterType == typeof(string))
                {
                    endpointUsed = safe;
                    var mod = (ModbusTcpIp)c.Invoke(new object[] { safe });
                    TrySetHostPortOnModbus(mod, hostOnly, port);
                    return mod;
                }
            }
            throw new NotSupportedException("No suitable ModbusTcpIp constructor found.");
        }

        private static void TrySetHostPortOnModbus(object modbus, string host, int port)
        {
            var namesStr = new[] { "TcpServerIp", "ServerIp", "IpAddress", "Host", "HostName" };
            var namesInt = new[] { "TcpServerPort", "ServerPort", "Port", "TcpPort" };
            const System.Reflection.BindingFlags BF =
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;

            void SetStr(string n)
            {
                var p = modbus.GetType().GetProperty(n, BF);
                if (p != null && p.CanWrite && p.PropertyType == typeof(string))
                    p.SetValue(modbus, host);
                var f = modbus.GetType().GetField(n, BF);
                if (f != null && f.FieldType == typeof(string))
                    f.SetValue(modbus, host);
            }
            void SetInt(string n)
            {
                var p = modbus.GetType().GetProperty(n, BF);
                if (p != null && p.CanWrite && p.PropertyType == typeof(int))
                    p.SetValue(modbus, port);
                var f = modbus.GetType().GetField(n, BF);
                if (f != null && f.FieldType == typeof(int))
                    f.SetValue(modbus, port);
            }

            foreach (var n in namesStr) SetStr(n);
            foreach (var n in namesInt) SetInt(n);
        }


        // Minimal Modbus/TCP writer (function 16) that bypasses ModbusNewLib endpoint parsing
        private static bool WriteHoldingRegistersTcpDirectChunked(
            string host, int port,
            byte unitId,
            ushort startAddr,
            ushort[] regs,
            int maxRegsPerRequest,
            int connectTimeoutMs,
            int sendTimeoutMs,
            int receiveTimeoutMs,
            out ModbusStateCode state,
            out ushort usedBase)
        {
            usedBase = startAddr;
            state = ModbusStateCode.Success;

            bool TryOnce(ushort baseAddr, out ModbusStateCode st)
            {
                st = ModbusStateCode.Success;

                using var client = new System.Net.Sockets.TcpClient();
                try
                {
                    var connectTask = client.ConnectAsync(host, port);
                    if (!connectTask.Wait(connectTimeoutMs))
                    {
                        st = ModbusStateCode.Timeout;
                        return false;
                    }
                }
                catch
                {
                    st = ModbusStateCode.UndefinedError;
                    return false;
                }

                // Tweak socket options and timeouts
                client.NoDelay = true;
                client.SendTimeout = Math.Max(sendTimeoutMs, 3000);
                client.ReceiveTimeout = Math.Max(receiveTimeoutMs, 5000);

                using var stream = client.GetStream();

                ushort txId = 1;
                int offset = 0;
                while (offset < regs.Length)
                {
                    int count = Math.Min(maxRegsPerRequest, regs.Length - offset);
                    ushort addr = (ushort)(baseAddr + offset);

                    // Build MBAP + PDU for function 0x10
                    // MBAP: [tx_hi, tx_lo, 0x00, 0x00, len_hi, len_lo, unit]
                    // PDU:  [0x10, addr_hi, addr_lo, qty_hi, qty_lo, byteCnt, data...]
                    int byteCount = count * 2;
                    int pduLen = 1 + 2 + 2 + 1 + byteCount; // fc + addr + qty + cnt + data
                    int mbapLen = 7;
                    var buf = new byte[mbapLen + pduLen];

                    // MBAP
                    buf[0] = (byte)(txId >> 8);
                    buf[1] = (byte)(txId & 0xFF);
                    buf[2] = 0x00;
                    buf[3] = 0x00;
                    ushort lenField = (ushort)(pduLen + 1); // UnitId + PDU
                    buf[4] = (byte)(lenField >> 8);
                    buf[5] = (byte)(lenField & 0xFF);
                    buf[6] = unitId;

                    // PDU
                    int i = mbapLen;
                    buf[i++] = 0x10; // function 16
                    buf[i++] = (byte)(addr >> 8);
                    buf[i++] = (byte)(addr & 0xFF);
                    buf[i++] = (byte)(count >> 8);
                    buf[i++] = (byte)(count & 0xFF);
                    buf[i++] = (byte)byteCount;

                    for (int k = 0; k < count; k++)
                    {
                        ushort r = regs[offset + k];
                        buf[i++] = (byte)(r >> 8);
                        buf[i++] = (byte)(r & 0xFF);
                    }


                    // Send
                    stream.Write(buf, 0, buf.Length);

                    const int successiveDelayMs = 100; // match ProtocolParams.SuccessiveRequestDelay
                    if (successiveDelayMs > 0)
                        System.Threading.Thread.Sleep(successiveDelayMs);

                    // Read response MBAP (7 bytes)
                    if (!ReadExact(stream, 7, client.ReceiveTimeout, out var mbapResp))
                    {
                        st = ModbusStateCode.Timeout;
                        return false;
                    }

                    // Validate protocol id
                    if (mbapResp[2] != 0x00 || mbapResp[3] != 0x00)
                    {
                        st = ModbusStateCode.WrongResponse;
                        return false;
                    }

                    // Length field (unit + PDU)
                    int respLen = (mbapResp[4] << 8) | mbapResp[5];
                    if (!ReadExact(stream, respLen, client.ReceiveTimeout, out var rest))
                    {
                        st = ModbusStateCode.Timeout;
                        return false;
                    }

                    // Exception frame?
                    if ((rest[1] & 0x80) != 0)
                    {
                        st = ModbusStateCode.WrongResponse;
                        return false;
                    }

                    if (respLen < 6)
                    {
                        st = ModbusStateCode.IllegalResponseLength;
                        return false;
                    }

                    if (rest[0] != unitId || rest[1] != 0x10)
                    {
                        st = ModbusStateCode.WrongResponse;
                        return false;
                    }

                    ushort echoAddr = (ushort)((rest[2] << 8) | rest[3]);
                    ushort echoQty = (ushort)((rest[4] << 8) | rest[5]);
                    if (echoAddr != addr || echoQty != (ushort)count)
                    {
                        st = ModbusStateCode.WrongResponse;
                        return false;
                    }

                    txId++;
                    offset += count;
                }

                return true;
            }

            // First try base
            if (TryOnce(startAddr, out state))
                return state == ModbusStateCode.Success;

            // Try base-1 if possible
            if (startAddr > 0)
            {
                ushort alt = (ushort)(startAddr - 1);
                if (TryOnce(alt, out state))
                {
                    if (state == ModbusStateCode.Success)
                    {
                        usedBase = alt;
                        return true;
                    }
                }
            }

            return false;

            static bool ReadExact(System.IO.Stream stream, int needed, int timeoutMs, out byte[] data)
            {
                data = new byte[needed];
                int read = 0;
                var start = DateTime.UtcNow;

                while (read < needed)
                {
                    if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
                        return false;

                    try
                    {
                        int n = stream.Read(data, read, needed - read);
                        if (n <= 0)
                            return false;
                        read += n;
                    }
                    catch (System.IO.IOException)
                    {
                        return false;
                    }
                    catch (System.Net.Sockets.SocketException)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        private static bool TryModbusTcpPing(
        string host, int port, byte unitId,
        int connectTimeoutMs, int receiveTimeoutMs,
        out string? diag)
        {
            diag = null;
            try
            {
                // Create protocol parameters with reasonable timeouts
                var protocolParams = new ProtocolParams
                {
                    Flags = ProtocolFlags.OffsetFromOne,  // Standard settings as in TryReadFirmwareTcp
                    SuccessiveRequestDelay = 100,
                    ConnectionTimeout = Math.Min(connectTimeoutMs, 3000),
                    SendTimeout = 2000,
                    ReceiveTimeout = Math.Min(receiveTimeoutMs, 5000),
                    ReceiveAgainTimeout = 2000
                };

                var endpoint = $"{host}:{port}";
                System.Diagnostics.Debug.WriteLine($"Trying ModbusTcpIp ping to {endpoint} (UnitId {unitId})");

                using var modbus = new ModbusTcpIp(endpoint, protocolParams);

                // Attempt to read a single register from address 0
                ushort[]? result = modbus.ReadDataFrom16bitRegisters(
                    unitId,
                    0x0000,      // Start at address 0
                    0x0001,      // Read just 1 register
                    RegType16b.HoldingRegister,
                    out ModbusStateCode state,
                    null);  // No module ref name

                if (state == ModbusStateCode.Success)
                {
                    return true;
                }
                else
                {
                    diag = $"Modbus read failed: {state}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                diag = ex.Message;
                return false;
            }
        }

        // Helper for reading exact bytes with timeout control
        static bool ReadExactBytes(System.IO.Stream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;

            while (totalRead < count)
            {
                int bytesRead = stream.Read(buffer, offset + totalRead, count - totalRead);

                if (bytesRead <= 0)
                    return false; // Stream closed or no data available

                totalRead += bytesRead;
            }

            return true;
        }


        // Helper: Read with multiple retries and increasing timeouts
        static bool ReadWithRetry(System.IO.Stream stream, byte[] buffer, int offset, int count,
                                     int maxRetries, int baseTimeoutMs)
        {
            int totalRead = 0;
            int retryCount = 0;

            while (totalRead < count && retryCount < maxRetries)
            {
                try
                {
                    // Progressive timeouts
                    int timeoutMs = baseTimeoutMs * (retryCount + 1);
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

                    while (totalRead < count && DateTime.UtcNow < deadline)
                    {
                        // Check if data is available before attempting read
                        if (stream.CanRead && stream.CanTimeout)
                        {
                            int bytesRead = stream.Read(buffer, offset + totalRead, count - totalRead);
                            if (bytesRead > 0)
                            {
                                totalRead += bytesRead;
                            }
                            else
                            {
                                // No data available, small delay before retry
                                System.Threading.Thread.Sleep(50);
                            }
                        }
                        else
                        {
                            return false;
                        }
                    }

                    if (totalRead == count)
                        return true;

                    // Didn't get all data, retry with delay
                    System.Threading.Thread.Sleep(150 * (retryCount + 1));
                    retryCount++;
                }
                catch
                {
                    retryCount++;
                    if (retryCount < maxRetries)
                    {
                        System.Threading.Thread.Sleep(200 * retryCount);
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            return totalRead == count;
        }


        private static bool TryFindResponsiveUnitId(
        string host, int port, byte preferredUnitId,
        int connectTimeoutMs, int receiveTimeoutMs,
        out byte unitIdFound, out string? diag)
        {
            unitIdFound = preferredUnitId;
            diag = null;

            // Try preferred first, then common fallbacks (dedup to avoid repeats)
            var candidates = new List<byte> { preferredUnitId, 1, 255, 0 }.Distinct().ToArray();

            var protocolParams = new ProtocolParams
            {
                Flags = ProtocolFlags.OffsetFromOne,
                SuccessiveRequestDelay = 100,
                ConnectionTimeout = Math.Min(connectTimeoutMs, 3000),
                SendTimeout = 1500,
                ReceiveTimeout = Math.Min(receiveTimeoutMs, 5000),
                ReceiveAgainTimeout = 1500
            };

            var endpoint = $"{host}:{port}";
            ModbusStateCode lastState = ModbusStateCode.UndefinedError;

            foreach (var uid in candidates)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"Trying UnitId {uid}...");

                    using var modbus = new ModbusTcpIp(endpoint, protocolParams);

                    // Try to read a single register from address 0
                    var result = modbus.ReadDataFrom16bitRegisters(
                        uid,
                        0x0000,      // Start at address 0
                        0x0001,      // Read just 1 register
                        RegType16b.HoldingRegister,
                        out lastState,
                        null);

                    if (lastState == ModbusStateCode.Success)
                    {
                        unitIdFound = uid;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Exception testing UnitId {uid}: {ex.Message}");
                    diag = ex.Message;
                    continue;
                }
            }

            diag = $"No responsive UnitId found. Last state: {lastState}";
            return false;
        }

        // Build protocol params for export: no address offsetting on writes
        private static ProtocolParams BuildProtocolParams(bool asSerialTcp = false)
        {
            var cfg = ResolveProtocolCfg();

            var p = new ProtocolParams
            {
                Flags = ProtocolFlags.OffsetFromOne,
                SuccessiveRequestDelay = cfg.SuccessiveRequestDelay,
                ConnectionTimeout = cfg.ConnectionTimeout,
                SendTimeout = cfg.SendTimeout,
                ReceiveTimeout = cfg.ReceiveTimeout,
                ReceiveAgainTimeout = cfg.ReceiveAgainTimeout
            };

            // Force "modbusserialtcp" transport hint if requested.
            if (asSerialTcp)
            {
                TrySetStringProp(p, "TransportName", "modbusserialtcp");
                TrySetStringProp(p, "Transport", "modbusserialtcp");
                TrySetStringProp(p, "Mode", "modbusserialtcp");
                TrySetStringProp(p, "ProtocolVariant", "modbusserialtcp");
            }

            TrySetProtocolTimeouts(p, cfg.ConnectionTimeout, cfg.SendTimeout, cfg.ReceiveTimeout);
            return p;

            static void TrySetStringProp(object o, string name, string value)
            {
                var pi = o.GetType().GetProperty(name,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance);
                if (pi != null && pi.CanWrite && pi.PropertyType == typeof(string))
                    pi.SetValue(o, value);
            }
        }

        // FC03 probe for base, base-1, base+1 (1 register)

        private static bool TryProbeBaseTcp(
        string host, int port, byte unitId, ushort baseAddr,
        int connectTimeoutMs, int receiveTimeoutMs,
        out ushort usedBase, out string? diag)
        {
            usedBase = baseAddr;
            diag = null;

            var protocolParams = new ProtocolParams
            {
                Flags = ProtocolFlags.OffsetFromOne,
                SuccessiveRequestDelay = 100,
                ConnectionTimeout = Math.Min(connectTimeoutMs, 3000),
                SendTimeout = 1500,
                ReceiveTimeout = Math.Min(receiveTimeoutMs, 5000),
                ReceiveAgainTimeout = 1500
            };

            var endpoint = $"{host}:{port}";

            // Try the three addresses in order: base, base-1, base+1
            var addresses = new List<ushort> { baseAddr };
            if (baseAddr > 0) addresses.Add((ushort)(baseAddr - 1));
            if (baseAddr < ushort.MaxValue) addresses.Add((ushort)(baseAddr + 1));

            ModbusStateCode lastState = ModbusStateCode.UndefinedError;

            foreach (var addr in addresses)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"Probing address 0x{addr:X4}...");

                    using var modbus = new ModbusTcpIp(endpoint, protocolParams);

                    // Try to read a single register from this address
                    var result = modbus.ReadDataFrom16bitRegisters(
                        unitId,
                        addr,      // Try this address
                        0x0001,    // Read just 1 register
                        RegType16b.HoldingRegister,
                        out lastState,
                        null);

                    if (lastState == ModbusStateCode.Success)
                    {
                        usedBase = addr;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Exception probing address 0x{addr:X4}: {ex.Message}");
                    diag = ex.Message;
                    continue;
                }
            }

            diag = $"No responsive register address found. Last state: {lastState}";
            return false;
        }

        private static string FirmwareDecode(ushort[]? data)
        {
            if (data == null || data.Length == 0) return string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (ushort reg in data)
            {
                byte upper = (byte)(reg >> 8);
                if (upper == 0x00) break;
                sb.Append((char)upper);

                byte lower = (byte)(reg & 0xFF);
                if (lower == 0x00) break;
                sb.Append((char)lower);
            }
            return sb.ToString();
        }

        // Connect using the same ctor/signature as the sample and read 0x20 holding registers from 0x0000
        private static bool TryReadFirmwareTcp(string host, int port, byte unitId, bool asSerialTcp, out string firmware, out ModbusStateCode state, out string? error)
        {
            firmware = string.Empty;
            error = null;
            state = ModbusStateCode.Success;

            try
            {
                // Use ip:port format exactly like the sample
                var endpoint = $"{host}:{port}";
                using var modbus = new ModbusTcpIp(endpoint, BuildProtocolParams(asSerialTcp));

                // Read first 32 holding registers at 0x0000, pass null for moduleRefName (matches the sample)
                ushort[]? block = modbus.ReadDataFrom16bitRegisters(
                    unitId,
                    0x0000,
                    0x0020,
                    RegType16b.HoldingRegister,
                    out state,
                    null);

                if (state == ModbusStateCode.Success && block != null)
                {
                    firmware = FirmwareDecode(block);
                    return true;
                }

                error = $"Read failed: {state}";
                return false;
            }
            catch (Exception ex)
            {
                state = ModbusStateCode.UndefinedError;
                error = ex.Message;
                return false;
            }
        }


        // Raw Modbus/TCP write of exactly N holding registers at a given address (no fallback, no chunking).
        private static bool WriteHoldingRegistersTcpDirectExact(
            string host, int port,
            byte unitId,
            ushort addr,
            ushort[] regs,
            int connectTimeoutMs,
            int sendTimeoutMs,
            int receiveTimeoutMs,
            out ModbusStateCode state,
            out string? error)
        {
            state = ModbusStateCode.Success;
            error = null;

            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                if (!connectTask.Wait(connectTimeoutMs))
                {
                    state = ModbusStateCode.Timeout;
                    error = "Connect timeout";
                    return false;
                }

                client.NoDelay = true;
                client.SendTimeout = Math.Max(sendTimeoutMs, 3000);
                client.ReceiveTimeout = Math.Max(receiveTimeoutMs, 5000);

                using var stream = client.GetStream();

                ushort txId = 1;
                int count = regs.Length;
                int byteCount = count * 2;

                // MBAP (7) + PDU (fc16 payload)
                var buf = new byte[7 + (1 + 2 + 2 + 1 + byteCount)];
                // MBAP
                buf[0] = (byte)(txId >> 8);
                buf[1] = (byte)(txId & 0xFF);
                buf[2] = 0x00; // protocol id hi
                buf[3] = 0x00; // protocol id lo
                ushort lenField = (ushort)(1 + (1 + 2 + 2 + 1 + byteCount)); // UnitId + PDU
                buf[4] = (byte)(lenField >> 8);
                buf[5] = (byte)(lenField & 0xFF);
                buf[6] = unitId;

                // PDU
                int i = 7;
                buf[i++] = 0x10; // FC16
                buf[i++] = (byte)(addr >> 8);
                buf[i++] = (byte)(addr & 0xFF);
                buf[i++] = (byte)(count >> 8);
                buf[i++] = (byte)(count & 0xFF);
                buf[i++] = (byte)byteCount;
                for (int k = 0; k < count; k++)
                {
                    ushort r = regs[k];
                    buf[i++] = (byte)(r >> 8);
                    buf[i++] = (byte)(r & 0xFF);
                }

                stream.Write(buf, 0, buf.Length);

                // Read MBAP (7)
                if (!ReadExact(stream, 7, client.ReceiveTimeout, out var mbapResp))
                {
                    state = ModbusStateCode.Timeout;
                    error = "No MBAP response";
                    return false;
                }
                if (mbapResp[2] != 0x00 || mbapResp[3] != 0x00)
                {
                    state = ModbusStateCode.WrongResponse;
                    error = "Bad protocol id";
                    return false;
                }
                int respLen = (mbapResp[4] << 8) | mbapResp[5];
                if (!ReadExact(stream, respLen, client.ReceiveTimeout, out var rest))
                {
                    state = ModbusStateCode.Timeout;
                    error = "Response timeout";
                    return false;
                }

                // Exception?
                if ((rest[1] & 0x80) != 0)
                {
                    state = ModbusStateCode.WrongResponse;
                    error = $"Exception {rest[2]}";
                    return false;
                }

                // Validate echo
                if (respLen < 6 || rest[0] != unitId || rest[1] != 0x10)
                {
                    state = ModbusStateCode.WrongResponse;
                    error = "Unexpected response";
                    return false;
                }
                ushort echoAddr = (ushort)((rest[2] << 8) | rest[3]);
                ushort echoQty = (ushort)((rest[4] << 8) | rest[5]);
                if (echoAddr != addr || echoQty != (ushort)count)
                {
                    state = ModbusStateCode.WrongResponse;
                    error = "Address/qty echo mismatch";
                    return false;
                }

                return true;
            }
            catch (System.TimeoutException)
            {
                state = ModbusStateCode.Timeout;
                error = "Timeout";
                return false;
            }
            catch (Exception ex)
            {
                state = ModbusStateCode.UndefinedError;
                error = ex.Message;
                return false;
            }

            static bool ReadExact(System.IO.Stream s, int needed, int timeoutMs, out byte[] data)
            {
                data = new byte[needed];
                int read = 0;
                var start = DateTime.UtcNow;
                while (read < needed)
                {
                    if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
                        return false;
                    int n;
                    try { n = s.Read(data, read, needed - read); }
                    catch { return false; }
                    if (n <= 0) return false;
                    read += n;
                }
                return true;
            }
        }

        private static bool WriteMpcByExactRegistersOnly(
        string host, int port, byte unitId,
        IReadOnlyList<ActivationZone> zones,
        int connectTimeoutMs, int sendTimeoutMs, int receiveTimeoutMs,
        bool asSerialTcp,
        out ModbusStateCode state, out string? error)
        {
            state = ModbusStateCode.Success;
            error = null;

            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;
            const int MAX_REGS_PER_CHUNK = 50; // Dávka 50 registrů najednou

            try
            {
                // Odemknout zařízení
                if (!TryWriteUnlockTcp(host, port, unitId, UNLOCK_REGISTER, UNLOCK_VALUE,
                    connectTimeoutMs, sendTimeoutMs, receiveTimeoutMs, out state, out error))
                {
                    error = $"Failed to unlock device at 0x{UNLOCK_REGISTER:X4}: {error}";
                    return false;
                }
                System.Threading.Thread.Sleep(800);

                var protocolParams = BuildProtocolParams(asSerialTcp);
                protocolParams.ConnectionTimeout = Math.Min(connectTimeoutMs, 5000);
                protocolParams.SendTimeout = Math.Min(sendTimeoutMs, 3000);
                protocolParams.ReceiveTimeout = Math.Min(receiveTimeoutMs, 5000);
                protocolParams.ReceiveAgainTimeout = 3000;

                using var modbus = new ModbusTcpIp($"{host}:{port}", protocolParams);

                const ushort BASE_ADDR = MPC_BASE_ADDR;       // 0x0300
                const ushort WRITE_OFFSET = MPCv3RTV_WRITE_OFFSET; // 44 -> 0x032C
                const int ZONE_STRIDE = MPC_ZONE_STRIDE;      // 10
                ushort FIRST_ZONE_BASE = (ushort)(BASE_ADDR + WRITE_OFFSET);

                // Sestavit všechny registry do jednoho velkého pole
                var allRegisters = new List<(ushort addr, ushort value)>();

                for (int idx = 0; idx < zones.Count; idx++)
                {
                    var z = zones[idx];
                    int mainZone = z.MainZone + 1;
                    int subZone = z.SubZone + 1;
                    int zoneIndex = ((mainZone - 1) * 5) + (subZone - 1);
                    ushort zoneBase = (ushort)(FIRST_ZONE_BASE + (zoneIndex * ZONE_STRIDE));

                    var (latLo, latHi) = FloatToWordsWS((float)z.Latitude);
                    var (lonLo, lonHi) = FloatToWordsWS((float)z.Longitude);
                    ushort lengthCm = ToUInt16(z.Height * 100.0);
                    ushort widthCm = ToUInt16(z.Width * 100.0);
                    ushort az = (ushort)Math.Clamp(z.Azimuth, 0, 359);

                    // Přidat registry zóny do seznamu
                    allRegisters.Add((zoneBase, latLo));
                    allRegisters.Add(((ushort)(zoneBase + 1), latHi));
                    allRegisters.Add(((ushort)(zoneBase + 2), lonLo));
                    allRegisters.Add(((ushort)(zoneBase + 3), lonHi));
                    allRegisters.Add(((ushort)(zoneBase + 4), lengthCm));
                    allRegisters.Add(((ushort)(zoneBase + 6), widthCm));
                    allRegisters.Add(((ushort)(zoneBase + 8), az));
                }

                // Rozdělit do dávek po 50 registrech a zapsat
                for (int i = 0; i < allRegisters.Count; i += MAX_REGS_PER_CHUNK)
                {
                    // Odemknout před každou dávkou
                    modbus.WriteToHoldingRegister(unitId, Off1(UNLOCK_REGISTER), UNLOCK_VALUE, out _, "Re-unlock batch");
                    System.Threading.Thread.Sleep(250);

                    int chunkSize = Math.Min(MAX_REGS_PER_CHUNK, allRegisters.Count - i);
                    var chunk = allRegisters.Skip(i).Take(chunkSize).ToList();

                    // Najít počáteční adresu a vytvořit souvislé pole hodnot
                    ushort startAddr = chunk[0].addr;
                    var values = new ushort[chunkSize];

                    for (int j = 0; j < chunkSize; j++)
                    {
                        values[j] = chunk[j].value;
                    }

                    // Zapsat dávku najednou
                    if (!modbus.WriteDataToHoldingRegisters(unitId, Off1(startAddr), (ushort)chunkSize,
                        values, out state, $"Batch write {i}/{allRegisters.Count}"))
                    {
                        error = $"Failed to write batch at 0x{startAddr:X4}";
                        return false;
                    }

                    System.Threading.Thread.Sleep(150); // Krátká pauza mezi dávkami
                }

                // Ověřit zápis - čtení po zónách pro kontrolu
                for (int idx = 0; idx < zones.Count; idx++)
                {
                    var z = zones[idx];
                    int mainZone = z.MainZone + 1;
                    int subZone = z.SubZone + 1;
                    int zoneIndex = ((mainZone - 1) * 5) + (subZone - 1);
                    ushort zoneBase = (ushort)(FIRST_ZONE_BASE + (zoneIndex * ZONE_STRIDE));

                    var rd = modbus.ReadDataFrom16bitRegisters(unitId, Off1(zoneBase), 10, RegType16b.HoldingRegister,
                        out var stV, $"Verify {mainZone}-{subZone}");

                    if (stV != ModbusStateCode.Success || rd == null || rd.Length < 10)
                    {
                        state = stV;
                        error = "Verify read failed";
                        return false;
                    }

                    float rbLat = WordsToFloatWS(rd[0], rd[1]);
                    float rbLon = WordsToFloatWS(rd[2], rd[3]);
                    bool coordsOk = Math.Abs(rbLon - (float)z.Longitude) <= 1e-5f &&
                                   Math.Abs(rbLat - (float)z.Latitude) <= 1e-5f;

                    if (!coordsOk)
                    {
                        error = $"Verify mismatch {mainZone}-{subZone}";
                        return false;
                    }
                }

                // Uzamknout zařízení
                TryWriteUnlockTcp(host, port, unitId, UNLOCK_REGISTER, 0,
                    connectTimeoutMs, sendTimeoutMs, receiveTimeoutMs, out _, out _);

                return true;
            }
            catch (Exception ex)
            {
                state = ModbusStateCode.UndefinedError;
                error = $"Exception during export: {ex.Message}";
                return false;
            }
        }

        private async void ReadButton_Click(object sender, RoutedEventArgs e)
        {
            Settings = ExportSettings.FromWindow(this);
            if (Settings == null)
            {
                MessageBox.Show("Missing export settings.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool usingTunnel = string.Equals(
                (ConnectionComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim(),
                "Serial tunnel",
                StringComparison.OrdinalIgnoreCase);

            bool isTcp = Settings.IsTcpSelected || usingTunnel;

            string remoteHost = "";
            int remotePort = 0;

            if (usingTunnel)
            {
                remoteHost = TunnelRemoteHostTextBox?.Text?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(remoteHost))
                {
                    MessageBox.Show("Remote host is required for the tunnel.", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!int.TryParse(TunnelRemotePortTextBox?.Text?.Trim(), out remotePort) || remotePort <= 0 || remotePort > 65535)
                {
                    MessageBox.Show("Remote port is invalid (1-65535).", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Settings.IsTcpSelected = true;
                Settings.TcpHost = remoteHost;
                Settings.TcpPort = remotePort;
            }

            if (!TryValidateSettings(Settings, out var validationError))
            {
                MessageBox.Show(validationError, "Export - validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // === PRE-READ DIAGNOSTICS: Read Register 0x0000 ===
            System.Diagnostics.Debug.WriteLine("\n=== PRE-READ DIAGNOSTICS ===");
            bool shouldSwitchToRtvMode = false;
            bool shouldSwitchToWlcMode = false;
            try
            {
                var (success, reg0Value, error) = await ReadRegister0x0000AsStringAsync();
                if (success)
                {
                    System.Diagnostics.Debug.WriteLine($"Register 0x0000 (Device Info): '{reg0Value}'");
                    System.Diagnostics.Debug.WriteLine($"Register 0x0000 Length: {reg0Value.Length} chars");

                    // Parse version if format is known (e.g., "MPC-RTV v2.1.3" or "MPC-WLC v1.2.0")
                    if (reg0Value.Contains("MPC") || reg0Value.Contains("RTV") || reg0Value.Contains("WLC"))
                    {
                        System.Diagnostics.Debug.WriteLine($"Device Type: MPC");
                        System.Diagnostics.Debug.WriteLine($"Full String: {reg0Value}");

                        // Pokud detekujeme RTV, nastavíme příznak pro přepnutí na Switches mode
                        if (reg0Value.Contains("RTV"))
                        {
                            shouldSwitchToRtvMode = true;
                            System.Diagnostics.Debug.WriteLine("RTV - will switch MainWindow to Switches mode");
                        }
                        // Pokud detekujeme WLC, nastavíme příznak pro přepnutí na Zone mode
                        else if (reg0Value.Contains("WLC"))
                        {
                            shouldSwitchToWlcMode = true;
                            System.Diagnostics.Debug.WriteLine("WLC - will switch MainWindow to Zone mode (Activation Zones)");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Unknown device type: {reg0Value}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"WARNING: Failed to read register 0x0000: {error}");
                }
            }
            catch (Exception diagEx)
            {
                System.Diagnostics.Debug.WriteLine($"WARNING: Exception during pre-read diagnostics: {diagEx.Message}");
            }
            System.Diagnostics.Debug.WriteLine("=== END PRE-READ DIAGNOSTICS ===\n");

            // Pokud byl detekován RTV, přepneme RadioButton v MainWindow na Switches mode
            if (shouldSwitchToRtvMode && Owner is MainWindow mw)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (mw.SwitchRadio != null)
                    {
                        mw.SwitchRadio.IsChecked = true;
                        System.Diagnostics.Debug.WriteLine("MainWindow RadioButton switched to Switches mode");
                    }
                });
            }
            // Pokud byl detekován WLC, přepneme RadioButton v MainWindow na Zone mode (Activation Zones)
            else if (shouldSwitchToWlcMode && Owner is MainWindow mw2)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (mw2.ZoneRadio != null)
                    {
                        mw2.ZoneRadio.IsChecked = true;
                        System.Diagnostics.Debug.WriteLine("MainWindow RadioButton switched to Zone mode (Activation Zones)");
                    }
                });
            }

            // Předpokládáme max 35 zón (7 hlavních x 5 sub-zón), každá 10 registrů = 350 registrů
            const int estimatedTotalRegisters = 350;
            ShowBusy("Reading activation zones...", showProgress: true, maxProgress: estimatedTotalRegisters);

            await Task.Delay(50);

            try
            {
                byte unitId = (byte)Math.Clamp(Settings.ModemDec ?? 1, 1, 247);
                List<ActivationZone> zones;
                string? error;

                var progress = new Progress<(int current, int total, string message)>(p =>
                {
                    // DŮLEŽITÉ: Musí být async invoke, aby neblokoval
                    Dispatcher.InvokeAsync(() => UpdateProgress(p.current, p.total, p.message), DispatcherPriority.Background);
                });

                if (isTcp)
                {
                    var host = Settings.TcpHost?.Trim();
                    var port = Settings.TcpPort ?? 502;
                    if (string.IsNullOrWhiteSpace(host))
                    {
                        await ShowMessageAfterBusyAsync("Modbus TCP host is required.", "Read", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var result = await Task.Run(() => ReadZonesFromModbusTcpWorker(host, port, unitId, usingTunnel));
                    if (!result.ok)
                    {
                        await ShowMessageAfterBusyAsync($"Failed to read zones via TCP: {result.error}", "Read", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    zones = result.zones;
                }
                else
                {
                    // RS485 - už je async
                    var (ok, readZones, err) = await ReadZonesFromModbusRtuWorkerAsyncWithProgress(unitId, progress);
                    if (!ok)
                    {
                        await ShowMessageAfterBusyAsync($"Failed to read zones via RS485: {err}", "Read", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    zones = readZones;
                }

                if (Owner is MainWindow mw3)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        mw3.ActivationZonesCollection.Clear();
                        foreach (var z in zones)
                        {
                            mw3.ActivationZonesCollection.Add(z);
                        }
                    });

                    await ShowMessageAfterBusyAsync(
                        $"Successfully read {zones.Count} zone(s) from {(isTcp ? "TCP" : "RS485")}.",
                        "Read", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                await ShowMessageAfterBusyAsync($"Read error: {ex.Message}", "Read", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideBusy();
            }
        }

        private async Task<(bool success, string value, string? error)> ReadRegister0x0000AsStringAsync()
        {
            Settings = ExportSettings.FromWindow(this);
            if (Settings == null)
                return (false, "", "Settings are missing");

            // Determine connection type
            string conn = (ConnectionComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim()?.ToLower() ?? "";
            bool isTcp = conn == "modbus tcp";
            bool isRtu = conn == "serial port";
            bool isTunnel = conn == "serial tunnel";

            if (!isTcp && !isRtu && !isTunnel)
                return (false, "", "Please select a connection type");

            // Get unit ID
            byte unitId = (byte)Math.Clamp(Settings.ModemDec ?? 1, 1, 247);

            const ushort START_ADDR = 0x0000;
            const ushort REG_COUNT = 0x0020; // Read 32 registers (64 bytes)
            const int timeoutMs = 5000;

            try
            {
                if (isTcp || isTunnel)
                {
                    // === TCP Connection ===
                    string host = Settings.TcpHost?.Trim() ?? "";
                    int port = Settings.TcpPort ?? 502;

                    if (string.IsNullOrWhiteSpace(host))
                        return (false, "", "TCP host is required");

                    // Use Task.Run to avoid blocking UI
                    var result = await Task.Run(() =>
                    {
                        try
                        {
                            var endpoint = $"{host}:{port}";
                            using var modbus = new ModbusTcpIp(endpoint, BuildProtocolParams(isTunnel));

                            ushort[]? block = modbus.ReadDataFrom16bitRegisters(
                                unitId,
                                START_ADDR,
                                REG_COUNT,
                                RegType16b.HoldingRegister,
                                out ModbusStateCode state,
                                null);

                            if (state == ModbusStateCode.Success && block != null)
                            {
                                string decoded = DecodeRegistersToString(block);
                                return (true, decoded, null);
                            }

                            return (false, "", $"Read failed: {state}");
                        }
                        catch (Exception ex)
                        {
                            return (false, "", $"TCP error: {ex.Message}");
                        }
                    });

                    return result;
                }
                else if (isRtu)
                {
                    // === RS485/RTU Connection ===
                    var (ok, state, data, error) = await ReadHoldingRegistersRtuAsync(
                        Settings, unitId, START_ADDR, REG_COUNT, timeoutMs);

                    if (!ok || state != ModbusStateCode.Success || data == null)
                        return (false, "", $"RS485 read failed: {error ?? state.ToString()}");

                    string decoded = DecodeRegistersToString(data);
                    return (true, decoded, null);
                }

                return (false, "", "Unknown connection type");
            }
            catch (Exception ex)
            {
                return (false, "", $"Exception: {ex.Message}");
            }
        }

        private static string DecodeRegistersToString(ushort[] registers)
        {
            if (registers == null || registers.Length == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder();

            foreach (ushort reg in registers)
            {
                // High byte
                byte upper = (byte)(reg >> 8);
                if (upper == 0x00) break;

                // Filter printable ASCII characters (0x20-0x7E)
                if (upper >= 0x20 && upper <= 0x7E)
                    sb.Append((char)upper);

                // Low byte
                byte lower = (byte)(reg & 0xFF);
                if (lower == 0x00) break;

                // Filter printable ASCII characters (0x20-0x7E)
                if (lower >= 0x20 && lower <= 0x7E)
                    sb.Append((char)lower);
            }

            return sb.ToString().Trim();
        }

        private static bool TryReadRegister0x0000Tcp(
            string host, int port, byte unitId, bool asSerialTcp,
            out string value, out ModbusStateCode state, out string? error)
        {
            value = string.Empty;
            error = null;
            state = ModbusStateCode.Success;

            try
            {
                var endpoint = $"{host}:{port}";
                using var modbus = new ModbusTcpIp(endpoint, BuildProtocolParams(asSerialTcp));

                // Read 32 holding registers at 0x0000
                ushort[]? block = modbus.ReadDataFrom16bitRegisters(
                    unitId,
                    0x0000,
                    0x0020, // 32 registers
                    RegType16b.HoldingRegister,
                    out state,
                    null);

                if (state == ModbusStateCode.Success && block != null)
                {
                    value = DecodeRegistersToString(block);
                    return true;
                }

                error = $"Read failed: {state}";
                return false;
            }
            catch (Exception ex)
            {
                state = ModbusStateCode.UndefinedError;
                error = ex.Message;
                return false;
            }
        }

        private static (bool ok, List<ActivationZone> zones, bool isRtv, string? error)
            ReadZonesFromModbusTcpWorkerWithProgress(string host, int port, byte unitId, bool asSerialTcp, IProgress<(int, int, string)>? progress)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== Reading MPC zones via TCP (BATCHED) ===");

                progress?.Report((0, 100, "Connecting..."));

                var protocolParams = BuildProtocolParams(asSerialTcp);

                if (!TryWriteUnlockTcp(host, port, unitId, 0x103F, 4562,
                    connectTimeoutMs: 3000, sendTimeoutMs: 2000, receiveTimeoutMs: 5000,
                    out var unlockState, out var unlockErr))
                {
                    return (false, new List<ActivationZone>(), false,
                        $"Failed to unlock device: {unlockState}{(string.IsNullOrWhiteSpace(unlockErr) ? "" : $" ({unlockErr})")}");
                }
                System.Threading.Thread.Sleep(800);

                using var modbus = new ModbusTcpIp($"{host}:{port}", protocolParams);

                bool isRtv = false;
                if (TryReadFirmwareTcp(host, port, unitId, asSerialTcp, out var fw, out _, out _))
                    isRtv = IsRtvFirmware(fw);

                var zones = new List<ActivationZone>();

                const ushort FIRST_REGISTER = 176;
                const ushort LAST_REGISTER = 524;
                const int TOTAL_REGISTERS = LAST_REGISTER - FIRST_REGISTER + 1;
                const int CHUNK_SIZE = 50;

                System.Diagnostics.Debug.WriteLine($"Registers: {FIRST_REGISTER}-{LAST_REGISTER} (total {TOTAL_REGISTERS})");

                var allData = new ushort[TOTAL_REGISTERS];
                int registersRead = 0;

                // Čtení registrů - 0% až 95%
                for (int offset = 0; offset < TOTAL_REGISTERS; offset += CHUNK_SIZE)
                {
                    int toRead = Math.Min(CHUNK_SIZE, TOTAL_REGISTERS - offset);
                    ushort readAddr = (ushort)(MPC_BASE_ADDR + FIRST_REGISTER + offset);

                    System.Diagnostics.Debug.WriteLine($"Reading TCP chunk: {toRead} regs from 0x{readAddr:X4} (offset {offset})...");

                    // Progress: 0-95% pro čtení registrů
                    int progressPercent = (int)((offset * 95.0) / TOTAL_REGISTERS);
                    progress?.Report((progressPercent, 100, $"Reading {offset}/{TOTAL_REGISTERS} registers..."));

                    var data = modbus.ReadDataFrom16bitRegisters(
                        unitId, readAddr, (ushort)toRead, RegType16b.HoldingRegister,
                        out var state, $"Read batch offset {offset}");

                    if (state != ModbusStateCode.Success || data == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"   FAILED: {state}");
                        return (false, new List<ActivationZone>(), false,
                            $"Failed to read batch at offset {offset}: {state}");
                    }

                    Array.Copy(data, 0, allData, offset, data.Length);
                    registersRead += data.Length;
                    System.Diagnostics.Debug.WriteLine($"   OK - {data.Length} regs (total: {registersRead}/{TOTAL_REGISTERS})");

                    System.Threading.Thread.Sleep(30);
                }

                // Zpracování zón - 95% až 100%
                progress?.Report((95, 100, "Processing zones..."));

                System.Diagnostics.Debug.WriteLine($"\n=== Read complete: {registersRead} registers ===");

                const int ZONE_STRIDE = 10;
                int maxZones = TOTAL_REGISTERS / ZONE_STRIDE;

                for (int zoneIdx = 0; zoneIdx < maxZones; zoneIdx++)
                {
                    int baseIdx = zoneIdx * ZONE_STRIDE;
                    if (baseIdx + 9 >= allData.Length) break;

                    ushort lonLo = allData[baseIdx + 0];
                    ushort lonHi = allData[baseIdx + 1];
                    ushort latLo = allData[baseIdx + 2];
                    ushort latHi = allData[baseIdx + 3];
                    ushort heightLo = allData[baseIdx + 4];
                    ushort heightHi = allData[baseIdx + 5];
                    ushort widthLo = allData[baseIdx + 6];
                    ushort widthHi = allData[baseIdx + 7];
                    ushort azLo = allData[baseIdx + 8];
                    ushort azHi = allData[baseIdx + 9];

                    if (lonLo == 0 && lonHi == 0 && latLo == 0 && latHi == 0)
                        continue;

                    float lonF = WordsToFloatWS(lonLo, lonHi);
                    float latF = WordsToFloatWS(latLo, latHi);
                    float heightF = WordsToFloatWS(heightLo, heightHi);
                    float widthF = WordsToFloatWS(widthLo, widthHi);
                    float azF = WordsToFloatWS(azLo, azHi);

                    if (!IsValidZoneData(lonF, latF, heightF, widthF, azF))
                        continue;

                    int mainZone = zoneIdx / 7;
                    int subZone = zoneIdx % 7;

                    var zone = new ActivationZone
                    {
                        MainZone = mainZone,
                        SubZone = subZone,
                        Latitude = latF,
                        Longitude = lonF,
                        Height = heightF,
                        Width = widthF,
                        Azimuth = (int)Math.Round(azF),
                        Name = $"Zone {mainZone + 1}"
                    };

                    zone.UpdateName();
                    zones.Add(zone);

                    // Progress během zpracování: 95-98%
                    if (zoneIdx % 5 == 0)
                    {
                        int processProgress = 95 + (int)((zoneIdx * 3.0) / maxZones);
                        progress?.Report((processProgress, 100, $"Processing zone {zoneIdx}/{maxZones}..."));
                    }
                }

                progress?.Report((98, 100, "Reading switch zones..."));

                if (isRtv)
                {
                    var rtvSwitches = ReadRtvSwitchZonesTcp(modbus, unitId);
                    var validSwitches = rtvSwitches.Where(z => IsValidZoneData(
                        (float)z.Longitude, (float)z.Latitude,
                        (float)z.Height, (float)z.Width, (float)z.Azimuth)).ToList();

                    zones.AddRange(validSwitches);
                }

                progress?.Report((99, 100, "Locking device..."));

                TryWriteUnlockTcp(host, port, unitId, 0x103F, 0,
                    connectTimeoutMs: 3000, sendTimeoutMs: 2000, receiveTimeoutMs: 5000,
                    out _, out _);

                System.Diagnostics.Debug.WriteLine($"\n=== TCP READ COMPLETE: {zones.Count} valid zones ===");

                // Final progress: 100%
                progress?.Report((100, 100, "Complete"));

                // Malá pauza, aby uživatel viděl 100%
                System.Threading.Thread.Sleep(200);

                return (true, zones, isRtv, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EXCEPTION: {ex}");
                return (false, new List<ActivationZone>(), false, ex.Message);
            }
        }

        private async Task<(bool ok, List<ActivationZone> zones, string? error)> ReadZonesFromModbusRtuWorkerAsyncWithProgress(
            byte unitId, IProgress<(int, int, string)>? progress)
        {
            var s = Settings;
            if (s == null) return (false, null!, "Settings missing");

            const int timeoutMs = 5000;
            const ushort FIRST_REGISTER = 176;
            const ushort LAST_REGISTER = 524;
            const int TOTAL_REGISTERS = LAST_REGISTER - FIRST_REGISTER + 1;
            const int CHUNK_SIZE = 50;

            try
            {
                System.Diagnostics.Debug.WriteLine($"=== Reading MPC zones via Modbus ASCII (BATCHED) ===");
                System.Diagnostics.Debug.WriteLine($"Registers: {FIRST_REGISTER}-{LAST_REGISTER} (total {TOTAL_REGISTERS})");

                var allData = new ushort[TOTAL_REGISTERS];
                int readCount = 0;

                // Čtení registrů - 0% až 95%
                for (int offset = 0; offset < TOTAL_REGISTERS; offset += CHUNK_SIZE)
                {
                    int remaining = TOTAL_REGISTERS - offset;
                    int toRead = Math.Min(CHUNK_SIZE, remaining);
                    ushort addr = (ushort)(MPC_BASE_ADDR + FIRST_REGISTER + offset);

                    System.Diagnostics.Debug.WriteLine($"Reading chunk: {toRead} regs from 0x{addr:X4} (offset {offset})...");

                    // Progress: 0-95% pro čtení
                    int progressPercent = (int)((offset * 95.0) / TOTAL_REGISTERS);
                    progress?.Report((progressPercent, 100, $"Reading {offset}/{TOTAL_REGISTERS} registers..."));

                    var (rok, rst, rdata, rerr) = await ReadHoldingRegistersRtuAsync(s, unitId, addr, (ushort)toRead, timeoutMs);

                    if (!rok || rst != ModbusStateCode.Success || rdata == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"   FAILED: {rerr}");
                        return (false, null!, $"Failed at offset {FIRST_REGISTER + offset}: {rerr}");
                    }

                    Array.Copy(rdata, 0, allData, offset, rdata.Length);
                    readCount += rdata.Length;

                    System.Diagnostics.Debug.WriteLine($"   OK - {rdata.Length} regs (total: {readCount}/{TOTAL_REGISTERS})");
                    await Task.Delay(100);
                }

                // Zpracování zón - 95% až 100%
                progress?.Report((95, 100, "Processing zones..."));

                System.Diagnostics.Debug.WriteLine($"\n=== Read complete: {readCount} registers ===");

                var zones = new List<ActivationZone>();
                const int ZONE_STRIDE = 10;
                int maxZones = TOTAL_REGISTERS / ZONE_STRIDE;

                System.Diagnostics.Debug.WriteLine($"Scanning up to {maxZones} zone slots...\n");

                for (int zoneIdx = 0; zoneIdx < maxZones; zoneIdx++)
                {
                    int baseIdx = zoneIdx * ZONE_STRIDE;
                    if (baseIdx + 9 >= allData.Length) break;

                    ushort lonLo = allData[baseIdx + 0];
                    ushort lonHi = allData[baseIdx + 1];
                    ushort latLo = allData[baseIdx + 2];
                    ushort latHi = allData[baseIdx + 3];
                    ushort heightLo = allData[baseIdx + 4];
                    ushort heightHi = allData[baseIdx + 5];
                    ushort widthLo = allData[baseIdx + 6];
                    ushort widthHi = allData[baseIdx + 7];
                    ushort azLo = allData[baseIdx + 8];
                    ushort azHi = allData[baseIdx + 9];

                    if (lonLo == 0 && lonHi == 0 && latLo == 0 && latHi == 0)
                        continue;

                    float lonF = WordsToFloatWS(lonLo, lonHi);
                    float latF = WordsToFloatWS(latLo, latHi);
                    float heightF = WordsToFloatWS(heightLo, heightHi);
                    float widthF = WordsToFloatWS(widthLo, widthHi);
                    float azF = WordsToFloatWS(azLo, azHi);

                    // VALIDACE: Filtrovat invalidní data
                    if (!IsValidZoneData(lonF, latF, heightF, widthF, azF))
                    {
                        System.Diagnostics.Debug.WriteLine($"Zone slot {zoneIdx}: ✗ Invalid data - skipped");
                        continue;
                    }

                    int mainZone = zoneIdx / 7;
                    int subZone = zoneIdx % 7;

                    var zone = new ActivationZone
                    {
                        MainZone = mainZone,
                        SubZone = subZone,
                        Latitude = latF,
                        Longitude = lonF,
                        Height = heightF,
                        Width = widthF,
                        Azimuth = (int)Math.Round(azF),
                        Name = $"Zone {mainZone + 1}"
                    };

                    zone.UpdateName();
                    zones.Add(zone);
                    System.Diagnostics.Debug.WriteLine($"  ✓ Added Zone {mainZone + 1}-{subZone + 1}: Lon={lonF:F6}, Lat={latF:F6}");

                    // Progress během zpracování: 95-100%
                    if (zoneIdx % 3 == 0)
                    {
                        int processProgress = 95 + (int)((zoneIdx * 5.0) / maxZones);
                        progress?.Report((processProgress, 100, $"Processing zone {zoneIdx}/{maxZones}..."));
                    }
                }

                progress?.Report((100, 100, "Complete"));
                await Task.Delay(200); // Krátká pauza, aby uživatel viděl 100%

                System.Diagnostics.Debug.WriteLine($"\n=== RS485 READ COMPLETE: {zones.Count} valid zones ===");
                return (true, zones, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EXCEPTION: {ex}");
                return (false, null!, ex.Message);
            }
        }


        private static (bool ok, List<ActivationZone> zones, bool isRtv, string? error)
        ReadZonesFromModbusTcpWorker(string host, int port, byte unitId, bool asSerialTcp = false)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== Reading MPC zones via TCP (BATCHED) ===");

                var protocolParams = BuildProtocolParams(asSerialTcp);

                if (!TryWriteUnlockTcp(host, port, unitId, 0x103F, 4562,
                    connectTimeoutMs: 3000, sendTimeoutMs: 2000, receiveTimeoutMs: 5000,
                    out var unlockState, out var unlockErr))
                {
                    return (false, new List<ActivationZone>(), false,
                        $"Failed to unlock device: {unlockState}{(string.IsNullOrWhiteSpace(unlockErr) ? "" : $" ({unlockErr})")}");
                }
                System.Threading.Thread.Sleep(800);

                using var modbus = new ModbusTcpIp($"{host}:{port}", protocolParams);

                bool isRtv = false;
                if (TryReadFirmwareTcp(host, port, unitId, asSerialTcp, out var fw, out _, out _))
                    isRtv = IsRtvFirmware(fw);

                var zones = new List<ActivationZone>();

                const ushort FIRST_REGISTER = 176;
                const ushort LAST_REGISTER = 524;
                const int TOTAL_REGISTERS = LAST_REGISTER - FIRST_REGISTER + 1;
                const int CHUNK_SIZE = 50;

                System.Diagnostics.Debug.WriteLine($"Registers: {FIRST_REGISTER}-{LAST_REGISTER} (total {TOTAL_REGISTERS})");

                var allData = new ushort[TOTAL_REGISTERS];
                int registersRead = 0;

                for (int offset = 0; offset < TOTAL_REGISTERS; offset += CHUNK_SIZE)
                {
                    int toRead = Math.Min(CHUNK_SIZE, TOTAL_REGISTERS - offset);
                    ushort readAddr = (ushort)(MPC_BASE_ADDR + FIRST_REGISTER + offset);

                    System.Diagnostics.Debug.WriteLine($"Reading TCP chunk: {toRead} regs from 0x{readAddr:X4} (offset {offset})...");

                    var data = modbus.ReadDataFrom16bitRegisters(
                        unitId, readAddr, (ushort)toRead, RegType16b.HoldingRegister,
                        out var state, $"Read batch offset {offset}");

                    if (state != ModbusStateCode.Success || data == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"   FAILED: {state}");
                        return (false, new List<ActivationZone>(), false,
                            $"Failed to read batch at offset {offset}: {state}");
                    }

                    Array.Copy(data, 0, allData, offset, data.Length);
                    registersRead += data.Length;
                    System.Diagnostics.Debug.WriteLine($"   OK - {data.Length} regs (total: {registersRead}/{TOTAL_REGISTERS})");

                    System.Threading.Thread.Sleep(50); // Změněno z 100ms na 50ms
                }

                System.Diagnostics.Debug.WriteLine($"\n=== Read complete: {registersRead} registers ===");

                const int ZONE_STRIDE = 10;
                int maxZones = TOTAL_REGISTERS / ZONE_STRIDE;

                for (int zoneIdx = 0; zoneIdx < maxZones; zoneIdx++)
                {
                    int baseIdx = zoneIdx * ZONE_STRIDE;
                    if (baseIdx + 9 >= allData.Length) break;

                    ushort lonLo = allData[baseIdx + 0];
                    ushort lonHi = allData[baseIdx + 1];
                    ushort latLo = allData[baseIdx + 2];
                    ushort latHi = allData[baseIdx + 3];
                    ushort heightLo = allData[baseIdx + 4];
                    ushort heightHi = allData[baseIdx + 5];
                    ushort widthLo = allData[baseIdx + 6];
                    ushort widthHi = allData[baseIdx + 7];
                    ushort azLo = allData[baseIdx + 8];
                    ushort azHi = allData[baseIdx + 9];

                    if (lonLo == 0 && lonHi == 0 && latLo == 0 && latHi == 0)
                        continue;

                    float lonF = WordsToFloatWS(lonLo, lonHi);
                    float latF = WordsToFloatWS(latLo, latHi);
                    float heightF = WordsToFloatWS(heightLo, heightHi);
                    float widthF = WordsToFloatWS(widthLo, widthHi);
                    float azF = WordsToFloatWS(azLo, azHi);

                    if (!IsValidZoneData(lonF, latF, heightF, widthF, azF))
                        continue;

                    int mainZone = zoneIdx / 5;
                    int subZone = zoneIdx % 5;

                    var zone = new ActivationZone
                    {
                        MainZone = mainZone,
                        SubZone = subZone,
                        Latitude = latF,
                        Longitude = lonF,
                        Height = heightF,
                        Width = widthF,
                        Azimuth = (int)Math.Round(azF),
                        Name = $"Zone {mainZone + 1}"
                    };

                    zone.UpdateName();
                    zones.Add(zone);
                }

                if (isRtv)
                {
                    var rtvSwitches = ReadRtvSwitchZonesTcp(modbus, unitId);
                    var validSwitches = rtvSwitches.Where(z => IsValidZoneData(
                        (float)z.Longitude, (float)z.Latitude,
                        (float)z.Height, (float)z.Width, (float)z.Azimuth)).ToList();

                    zones.AddRange(validSwitches);
                }

                TryWriteUnlockTcp(host, port, unitId, 0x103F, 0,
                    connectTimeoutMs: 3000, sendTimeoutMs: 2000, receiveTimeoutMs: 5000,
                    out _, out _);

                System.Diagnostics.Debug.WriteLine($"\n=== TCP READ COMPLETE: {zones.Count} valid zones ===");
                return (true, zones, isRtv, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EXCEPTION: {ex}");
                return (false, new List<ActivationZone>(), false, ex.Message);
            }
        }

        private static bool IsValidZoneData(float lon, float lat, float height, float width, float azimuth)
        {
            // Kontrola souřadnic
            if (!double.IsFinite(lon) || !double.IsFinite(lat))
                return false;

            // Lat/Lon musí být v rozumném rozsahu
            if (lon < 5.0f || lon > 25.0f || lat < 40.0f || lat > 60.0f)
                return false;

            // Rozměry musí být platné
            if (height <= 0 || height > 500 || width <= 0 || width > 500)
                return false;

            // Azimuth musí být v rozsahu 0-359
            if (azimuth < 0 || azimuth >= 360)
                return false;

            // Dodatečná kontrola: filtrovat "téměř nulové" hodnoty
            if (Math.Abs(lon) < 1.0 || Math.Abs(lat) < 1.0)
                return false;

            return true;
        }

        private async Task<(bool ok, List<ActivationZone> zones, string? error)> ReadZonesFromModbusRtuWorkerAsync(byte unitId)
        {
            var s = Settings;
            if (s == null) return (false, null!, "Settings missing");

            const int timeoutMs = 5000;
            const ushort FIRST_REGISTER = 176;
            const ushort LAST_REGISTER = 524;
            const int TOTAL_REGISTERS = LAST_REGISTER - FIRST_REGISTER + 1;
            const int CHUNK_SIZE = 50;

            try
            {
                System.Diagnostics.Debug.WriteLine($"=== Reading MPC zones via Modbus ASCII (BATCHED) ===");
                System.Diagnostics.Debug.WriteLine($"Registers: {FIRST_REGISTER}-{LAST_REGISTER} (total {TOTAL_REGISTERS})");

                var allData = new ushort[TOTAL_REGISTERS];
                int readCount = 0;

                for (int offset = 0; offset < TOTAL_REGISTERS; offset += CHUNK_SIZE)
                {
                    int remaining = TOTAL_REGISTERS - offset;
                    int toRead = Math.Min(CHUNK_SIZE, remaining);
                    ushort addr = (ushort)(MPC_BASE_ADDR + FIRST_REGISTER + offset);

                    System.Diagnostics.Debug.WriteLine($"Reading chunk: {toRead} regs from 0x{addr:X4} (offset {offset})...");

                    var (rok, rst, rdata, rerr) = await ReadHoldingRegistersRtuAsync(s, unitId, addr, (ushort)toRead, timeoutMs);

                    if (!rok || rst != ModbusStateCode.Success || rdata == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"   FAILED: {rerr}");
                        return (false, null!, $"Failed at offset {FIRST_REGISTER + offset}: {rerr}");
                    }

                    Array.Copy(rdata, 0, allData, offset, rdata.Length);
                    readCount += rdata.Length;

                    System.Diagnostics.Debug.WriteLine($"   OK - {rdata.Length} regs (total: {readCount}/{TOTAL_REGISTERS})");
                    await Task.Delay(100);
                }

                System.Diagnostics.Debug.WriteLine($"\n=== Read complete: {readCount} registers ===");

                var zones = new List<ActivationZone>();
                const int ZONE_STRIDE = 10;
                int maxZones = TOTAL_REGISTERS / ZONE_STRIDE;

                System.Diagnostics.Debug.WriteLine($"Scanning up to {maxZones} zone slots...\n");

                for (int zoneIdx = 0; zoneIdx < maxZones; zoneIdx++)
                {
                    int baseIdx = zoneIdx * ZONE_STRIDE;
                    if (baseIdx + 9 >= allData.Length) break;

                    ushort lonLo = allData[baseIdx + 0];
                    ushort lonHi = allData[baseIdx + 1];
                    ushort latLo = allData[baseIdx + 2];
                    ushort latHi = allData[baseIdx + 3];
                    ushort heightLo = allData[baseIdx + 4];
                    ushort heightHi = allData[baseIdx + 5];
                    ushort widthLo = allData[baseIdx + 6];
                    ushort widthHi = allData[baseIdx + 7];
                    ushort azLo = allData[baseIdx + 8];
                    ushort azHi = allData[baseIdx + 9];

                    if (lonLo == 0 && lonHi == 0 && latLo == 0 && latHi == 0)
                        continue;

                    float lonF = WordsToFloatWS(lonLo, lonHi);
                    float latF = WordsToFloatWS(latLo, latHi);
                    float heightF = WordsToFloatWS(heightLo, heightHi);
                    float widthF = WordsToFloatWS(widthLo, widthHi);
                    float azF = WordsToFloatWS(azLo, azHi);

                    // VALIDACE: Filtrovat invalidní data
                    if (!IsValidZoneData(lonF, latF, heightF, widthF, azF))
                    {
                        System.Diagnostics.Debug.WriteLine($"Zone slot {zoneIdx}:  Invalid data - skipped");
                        continue;
                    }

                    int mainZone = zoneIdx / 5;
                    int subZone = zoneIdx % 5;

                    var zone = new ActivationZone
                    {
                        MainZone = mainZone,
                        SubZone = subZone,
                        Latitude = latF,
                        Longitude = lonF,
                        Height = heightF,
                        Width = widthF,
                        Azimuth = (int)Math.Round(azF),
                        Name = $"Zone {mainZone + 1}"
                    };

                    zone.UpdateName();
                    zones.Add(zone);
                    System.Diagnostics.Debug.WriteLine($"   Added Zone {mainZone + 1}-{subZone + 1}: Lon={lonF:F6}, Lat={latF:F6}");
                }

                System.Diagnostics.Debug.WriteLine($"\n=== RS485 READ COMPLETE: {zones.Count} valid zones ===");
                return (true, zones, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EXCEPTION: {ex}");
                return (false, null!, ex.Message);
            }
        }

        private static bool TryDecodeZoneRegs(ushort[] regs, out double lat, out double lon, out double width, out double height, out int az)
        {
            lat = lon = width = height = 0;
            az = 0;

            if (regs.Length < 10) return false;

            System.Diagnostics.Debug.WriteLine($"    Analyzing raw data...");
            System.Diagnostics.Debug.WriteLine($"      [0]={regs[0]:X4} ({regs[0]}), [1]={regs[1]:X4} ({regs[1]})");
            System.Diagnostics.Debug.WriteLine($"      [2]={regs[2]:X4} ({regs[2]}), [3]={regs[3]:X4} ({regs[3]})");
            System.Diagnostics.Debug.WriteLine($"      [4]={regs[4]:X4} ({regs[4]}), [5]={regs[5]:X4} ({regs[5]})");
            System.Diagnostics.Debug.WriteLine($"      [6]={regs[6]:X4} ({regs[6]}), [7]={regs[7]:X4} ({regs[7]})");
            System.Diagnostics.Debug.WriteLine($"      [8]={regs[8]:X4} ({regs[8]}), [9]={regs[9]:X4} ({regs[9]})");

            // Check if all zeros
            if (regs.All(r => r == 0)) return false;

            // Try NEW format: Scaled uint16 with offset
            // Pattern: [lat_offset, lat_scale, lon_offset, lon_scale, height, ?, width, ?, azimuth, ?]
            // Example from data: [0, 230, 290, 1000, 0, 220, 285, 1000, 0, 60]
            // Could be: lat = base + (230/1000), lon = base + (290/1000)?

            // OR: Maybe it's encoded as fractional parts?
            // lat = 49 + (230/1000) = 49.230?
            // lon = 18 + (290/1000) = 18.290?

            // Try interpretation: [lat_int, lat_frac_thousandths, lon_int, lon_frac_thousandths, height_cm, gap, width_cm, gap, az, gap]
            if (regs[0] >= 40 && regs[0] <= 60 && regs[2] >= 5 && regs[2] <= 25)
            {
                // This doesn't match our data (regs[0] is 0)
            }

            // Try interpretation 2: Fixed base + offset
            // Known coordinates from UI: Lat≈49.843, Lon≈18.275
            // Data: [0, 230, 290, 1000, 0, 220, 285, 1000, 0, 60]
            // Maybe: lat_frac=230, lat_scale=1000, lon_frac=290, lon_scale=1000?

            // Calculate with assumed base (Ostrava region)
            const double LAT_BASE = 49.0;  // Ostrava latitude base
            const double LON_BASE = 18.0;  // Ostrava longitude base

            try
            {
                // Format: [lat_frac_hi, lat_frac_lo_scale, lon_frac_hi, lon_frac_lo_scale, ...]
                if (regs[1] > 0 && regs[3] > 0)
                {
                    // Try: lat = LAT_BASE + (regs[0] * 1000 + regs[1]) / 1000000
                    double latOffset = (regs[0] * 1000.0 + regs[1]) / 1000.0;  // e.g., (0*1000 + 230)/1000 = 0.230
                    double lonOffset = (regs[2] * 1000.0 + regs[3]) / 1000.0;  // e.g., (290)/1000 = 0.290

                    // But wait - regs[3] = 1000, that's scale not value!
                    // Maybe: lat_frac = regs[1], lat_divisor = regs[3], lon_frac = regs[2], lon_divisor = regs[3]

                    double latTest = LAT_BASE + ((double)regs[1] / (double)regs[3]) + ((double)regs[2] / 1000.0);
                    double lonTest = LON_BASE + ((double)regs[5] / (double)regs[7]) + ((double)regs[6] / 1000.0);

                    System.Diagnostics.Debug.WriteLine($"    Attempt scaled format: Lat={latTest:F8}, Lon={lonTest:F8}");
                }
            }
            catch { }

            // Try all original formats...
            // (keep existing Float32 WS, BE, INT32 attempts)

            // Try Float32 Word-Swapped
            try
            {
                float latF = WordsToFloatWS(regs[0], regs[1]);
                float lonF = WordsToFloatWS(regs[2], regs[3]);

                if (IsValidCoord(latF, lonF))
                {
                    lat = latF;
                    lon = lonF;
                    height = regs[4] / 100.0;
                    width = regs[6] / 100.0;
                    az = regs[8];

                    if (IsValidDimensions(width, height, az))
                    {
                        System.Diagnostics.Debug.WriteLine($"    Format: Float32 WS");
                        return true;
                    }
                }
            }
            catch { }

            return false;

            static bool IsValidCoord(double lat, double lon) => lat >= 40 && lat <= 60 && lon >= 5 && lon <= 25;
            static bool IsValidDimensions(double w, double h, int a) => w > 0 && w < 500 && h > 0 && h < 500 && a >= 0 && a < 360;
        }

        private void UpdateButtonStates(bool isReadMode)
        {
            if (Start != null)
                Start.IsEnabled = !isReadMode;

            if (ReadButton != null)
                ReadButton.IsEnabled = isReadMode;
        }

        private void ReadZonesFromModbusTcp(string host, int port, byte unitId)
        {
            var ownBusy = TryBeginBusy("Reading registers (TCP)...");
            try
            {
                // (existing body unchanged below)
                var protocolParams = new ProtocolParams
                {
                    Flags = ProtocolFlags.OffsetFromOne,
                    SuccessiveRequestDelay = 250,
                    ConnectionTimeout = 5000,
                    SendTimeout = 3000,
                    ReceiveTimeout = 5000,
                    ReceiveAgainTimeout = 3000
                };

                if (!TryWriteUnlockTcp(host, port, unitId, 0x103F, 4562,
                    connectTimeoutMs: 3000, sendTimeoutMs: 2000, receiveTimeoutMs: 5000,
                    out var unlockState, out var unlockErr))
                {
                    MessageBox.Show($"Failed to unlock device: {unlockState}{(string.IsNullOrWhiteSpace(unlockErr) ? "" : $" ({unlockErr})")}",
                        "Read Registers", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                System.Threading.Thread.Sleep(800);

                using var modbus = new ModbusTcpIp($"{host}:{port}", protocolParams);

                bool isRtv = false;
                if (TryReadFirmwareTcp(host, port, unitId, false, out var fw, out var fwState, out var fwErr))
                    isRtv = IsRtvFirmware(fw);

                var zones = new List<ActivationZone>();

                const ushort BASE_ADDR = MPC_BASE_ADDR;
                const ushort WRITE_OFFSET = MPCv3RTV_WRITE_OFFSET;
                const int ZONE_STRIDE = MPC_ZONE_STRIDE;
                ushort FIRST_ZONE_BASE = (ushort)(BASE_ADDR + WRITE_OFFSET);

                for (int mainZone = 1; mainZone <= 5; mainZone++)
                {
                    for (int subZone = 1; subZone <= 7; subZone++)
                    {
                        if ((mainZone > 1 || subZone > 1) && subZone == 1)
                        {
                            modbus.WriteToHoldingRegister(unitId, 0x103F, 4562, out _, "Re-unlock for zone");
                            System.Threading.Thread.Sleep(300);
                        }

                        int zoneIndex = ((mainZone - 1) * 7) + (subZone - 1);
                        ushort zoneBase = (ushort)(FIRST_ZONE_BASE + (zoneIndex * ZONE_STRIDE));

                        var data = modbus.ReadDataFrom16bitRegisters(
                            unitId, zoneBase, 10, RegType16b.HoldingRegister, out var zoneState,
                            $"Read zone {mainZone}-{subZone}");

                        if (zoneState != ModbusStateCode.Success || data == null || data.Length < 10) continue;
                        if (data.All(v => v == 0)) continue;

                        double latitude = WordsToFloatWS(data[0], data[1]);
                        double longitude = WordsToFloatWS(data[2], data[3]);
                        double height = WordsToFloatWS(data[4], data[5]);
                        double width = WordsToFloatWS(data[6], data[7]);
                        int azimuth = (int)Math.Round(WordsToFloatWS(data[8], data[9]));

                        if (double.IsFinite(latitude) && double.IsFinite(longitude) && width > 0 && height > 0)
                        {
                            zones.Add(new ActivationZone
                            {
                                Name = $"Zone {mainZone}-{subZone}",
                                MainZone = mainZone - 1,
                                SubZone = subZone - 1,
                                Latitude = latitude,
                                Longitude = longitude,
                                Width = width,
                                Height = height,
                                Azimuth = Math.Clamp(azimuth, 0, 359)
                            });
                        }
                    }
                }

                if (isRtv)
                {
                    var rtvSwitches = ReadRtvSwitchZonesTcp(modbus, unitId);
                    zones.AddRange(rtvSwitches);
                }

                if (zones.Count > 0)
                {
                    if (Owner is not MainWindow mw)
                    {
                        MessageBox.Show("Cannot access main window.", "Read Registers",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    mw.ActivationZonesCollection.Clear();
                    foreach (var z in zones) mw.ActivationZonesCollection.Add(z);

                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try { mw.ReprojectActivationZonesOnMapChange(); await mw.BringAllOverlaysToFrontSafeAsync(); } catch { }
                    }));

                    MessageBox.Show($"Successfully read {zones.Count} zone(s) from device.",
                        "Read Registers", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("No valid zones found on the device.",
                        "Read Registers", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                TryWriteUnlockTcp(host, port, unitId, 0x103F, 0,
                   connectTimeoutMs: 3000, sendTimeoutMs: 2000, receiveTimeoutMs: 5000,
                   out _, out _);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading zones: {ex.Message}", "Read Registers", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (ownBusy) HideBusy();
            }
        }

        private static async Task<(bool ok, ModbusStateCode state, ushort[]? data, string? error)> ReadHoldingRegistersRtuAsync(
        ExportSettings s, byte slave, ushort regAddr, ushort count, int timeoutMs)
        {
            if (count <= 0) return (false, ModbusStateCode.IllegalResponseLength, null, "Count must be > 0");
            try
            {
                var parity = ParseParity(s.SerialParity);
                var stop = ParseStopBits(s.SerialStopBits);
                int dataBits = s.SerialDataBits ?? 8;

                using var sp = new SerialPort(
                    s.SerialPortName!,
                    s.SerialBaudrate ?? 19200,
                    parity,
                    dataBits,
                    stop)
                {
                    Handshake = Handshake.None,
                    ReadTimeout = Math.Max(timeoutMs, 3000),
                    WriteTimeout = Math.Max(timeoutMs, 3000),
                    Encoding = Encoding.ASCII
                };
                sp.Open();
                try { sp.DiscardInBuffer(); sp.DiscardOutBuffer(); } catch { }

                // RAW zero-based address for ASCII
                ushort addr = regAddr;

                string req = BuildAsciiReadHolding(slave, addr, count);
                var tx = Encoding.ASCII.GetBytes(req);
                await sp.BaseStream.WriteAsync(tx, 0, tx.Length, CancellationToken.None).ConfigureAwait(false);
                await sp.BaseStream.FlushAsync().ConfigureAwait(false);

                string respAscii = await ReadAsciiFrameAsync(sp, Math.Max(timeoutMs, 3500)).ConfigureAwait(false);
                var parsed = ParseAsciiResponse(respAscii);
                if (!parsed.ok) return (false, ModbusStateCode.CRC, null, parsed.error);
                if (parsed.slave != slave) return (false, ModbusStateCode.WrongResponse, null, "Wrong slave");
                if ((parsed.func & 0x80) != 0)
                {
                    byte ex = parsed.payload.FirstOrDefault();
                    string exText = ex switch
                    {
                        0x01 => "Illegal Function",
                        0x02 => "Illegal Data Address",
                        0x03 => "Illegal Data Value",
                        0x04 => "Slave Device Failure",
                        0x05 => "Acknowledge",
                        0x06 => "Slave Device Busy",
                        0x08 => "Memory Parity Error",
                        0x0A => "Gateway Path Unavailable",
                        0x0B => "Gateway Target Failed to Respond",
                        _ => "Unknown"
                    };
                    var stEx = ex == 0x03 ? ModbusStateCode.IllegalDataValue : ModbusStateCode.WrongResponse;
                    return (false, stEx, null, $"Exception {ex:X2} ({exText})");
                }
                if (parsed.func != 0x03) return (false, ModbusStateCode.WrongResponse, null, "Wrong function");

                if (parsed.payload.Length < 1) return (false, ModbusStateCode.IllegalResponseLength, null, "No byte count");
                int byteCount = parsed.payload[0];
                if (byteCount != count * 2) return (false, ModbusStateCode.IllegalResponseLength, null, "Byte count mismatch");

                if (parsed.payload.Length != 1 + byteCount)
                    return (false, ModbusStateCode.IllegalResponseLength, null, "Payload length mismatch");

                var regs = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    int off = 1 + i * 2;
                    regs[i] = (ushort)((parsed.payload[off] << 8) | parsed.payload[off + 1]);
                }

                return (true, ModbusStateCode.Success, regs, null);
            }
            catch (TimeoutException)
            {
                return (false, ModbusStateCode.Timeout, null, "ASCII timeout");
            }
            catch (Exception ex)
            {
                return (false, ModbusStateCode.UndefinedError, null, ex.Message);
            }
        }

        // ADD: RTU read zones like TCP, but using ReadHoldingRegistersRtuAsync and RTU unlock
        private async Task ReadZonesFromModbusRtuAsync(byte unitId)
        {
            var ownBusy = TryBeginBusy("Reading registers (RS485)...");
            try
            {
                var s = Settings ?? ExportSettings.FromWindow(this);
                if (s == null)
                {
                    MessageBox.Show("Missing serial settings.", "Read Registers", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                const ushort UNLOCK_REGISTER = 0x103F;
                const ushort UNLOCK_VALUE = 4562;

                var (uok, ustate, uerr) = await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs: 2000);
                if (!uok || ustate != ModbusStateCode.Success)
                {
                    MessageBox.Show($"Failed to unlock device over RS485: {ustate}{(string.IsNullOrWhiteSpace(uerr) ? "" : $" ({uerr})")}",
                        "Read Registers", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                await Task.Delay(500);

                bool isRtv = false;
                {
                    var (okFw, stFw, dataFw, errFw) = await ReadHoldingRegistersRtuAsync(s, unitId, 0x0000, 0x0020, timeoutMs: 2000);
                    if (okFw && stFw == ModbusStateCode.Success && dataFw != null)
                    {
                        var fw = DecodeAsciiFromRegs(dataFw);
                        isRtv = IsRtvFirmware(fw);
                    }
                }

                var zones = new List<ActivationZone>();
                const ushort BASE_ADDR = MPC_BASE_ADDR;
                const ushort WRITE_OFFSET = MPCv3RTV_WRITE_OFFSET;
                const int ZONE_STRIDE = MPC_ZONE_STRIDE;
                ushort FIRST_ZONE_BASE = (ushort)(BASE_ADDR + WRITE_OFFSET);

                for (int mainZone = 1; mainZone <= 4; mainZone++)
                {
                    for (int subZone = 1; subZone <= 5; subZone++)
                    {
                        if ((mainZone > 1 || subZone > 1) && subZone == 1)
                        {
                            await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs: 2000);
                            await Task.Delay(300);
                        }

                        int zoneIndex = ((mainZone - 1) * 5) + (subZone - 1);
                        ushort zoneBase = (ushort)(FIRST_ZONE_BASE + (zoneIndex * ZONE_STRIDE));

                        var (ok, st, data, err) = await ReadHoldingRegistersRtuAsync(s, unitId, zoneBase, 10, timeoutMs: 2000);
                        if (!ok || st != ModbusStateCode.Success || data == null || data.Length < 10)
                            continue;

                        if (data.All(v => v == 0))
                            continue;

                        double latitude = WordsToFloatWS(data[0], data[1]);
                        double longitude = WordsToFloatWS(data[2], data[3]);
                        double height = data[4] / 100.0;
                        double width = data[6] / 100.0;
                        int azimuth = Math.Clamp((int)data[8], 0, 359);

                        if (double.IsFinite(latitude) && double.IsFinite(longitude) && width > 0 && height > 0)
                        {
                            zones.Add(new ActivationZone
                            {
                                Name = $"Zone {mainZone}-{subZone}",
                                MainZone = mainZone - 1,
                                SubZone = subZone - 1,
                                Latitude = latitude,
                                Longitude = longitude,
                                Width = width,
                                Height = height,
                                Azimuth = azimuth
                            });
                        }
                    }
                }

                if (isRtv)
                {
                    var rtvSwitches = await ReadRtvSwitchZonesRtuAsync(s, unitId, timeoutMs: 2000);
                    zones.AddRange(rtvSwitches);
                }

                if (zones.Count == 0)
                {
                    MessageBox.Show("No valid zones found on the device (RS485).",
                        "Read Registers", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    if (Owner is not MainWindow mw || mw.ActivationZonesCollection == null)
                    {
                        MessageBox.Show("Cannot access main window.", "Read Registers",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    mw.ActivationZonesCollection.Clear();
                    foreach (var z in zones) mw.ActivationZonesCollection.Add(z);

                    await Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            mw.ReprojectActivationZonesOnMapChange();
                            await mw.BringAllOverlaysToFrontSafeAsync();
                        }
                        catch { }
                    }));

                    MessageBox.Show($"Successfully read {zones.Count} zone(s) over RS485.",
                        "Read Registers", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { (ushort)0 }, timeoutMs: 2000);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error reading zones over RS485: {ex.Message}", "Read Registers", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (ownBusy) HideBusy();
            }
        }



        // --- Helpers for switch-zone detection and RTV firmware ---
        private static bool IsSwitchRow(ActivationZone z) =>
        z != null &&
        (
            string.Equals(z?.Rectangle?.Tag as string, "SwitchZone", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(z?.Name) &&
                (
                    z.Name.StartsWith("Switch", StringComparison.OrdinalIgnoreCase)
                    || z.Name.StartsWith("Vyhyb", StringComparison.OrdinalIgnoreCase)   // Vyhybka
                    || z.Name.StartsWith("Výhyb", StringComparison.OrdinalIgnoreCase)   // Výhybka
                ))
        );

        private static bool IsRtvFirmware(string firmware) =>
            !string.IsNullOrWhiteSpace(firmware) &&
            firmware.IndexOf("MPCv3 RTV", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string DecodeAsciiFromRegs(ushort[]? data)
        {
            if (data == null || data.Length == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (ushort reg in data)
            {
                byte hi = (byte)(reg >> 8);
                if (hi == 0) break;
                sb.Append((char)hi);
                byte lo = (byte)(reg & 0xFF);
                if (lo == 0) break;
                sb.Append((char)lo);
            }
            return sb.ToString();
        }

        // RTV switch base: 0x03B0 + (main*7 + sub) * 0x0A (main/sub zero-based)
        private static ushort RtvSwitchBaseAddr(int mainZero, int subZero)
            => (ushort)(0x03B0 + ((mainZero * 7 + subZero) * 0x0A));

        // --- WRITE: MPC-RTV switch zones over TCP (same layout as activation zones) ---
        // replace the whole WriteRtvSwitchesByExactRegistersOnly with this version
        private static bool WriteRtvSwitchesByExactRegistersOnly(
            string host, int port, byte unitId,
            IReadOnlyList<ActivationZone> switchZones, bool asSerialTcp,
            int connectTimeoutMs, int sendTimeoutMs, int receiveTimeoutMs,
            out ModbusStateCode state, out string? error)
        {
            state = ModbusStateCode.Success;
            error = null;

            const int interWriteDelayMs = 350;
            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;

            try
            {
                var protocolParams = BuildProtocolParams(asSerialTcp);
                protocolParams.ConnectionTimeout = Math.Min(connectTimeoutMs, 5000);
                protocolParams.SendTimeout = Math.Min(sendTimeoutMs, 3000);
                protocolParams.ReceiveTimeout = Math.Min(receiveTimeoutMs, 5000);
                protocolParams.ReceiveAgainTimeout = 3000;

                using var modbus = new ModbusTcpIp($"{host}:{port}", protocolParams);

                if (!modbus.WriteToHoldingRegister(unitId, Off1(UNLOCK_REGISTER), UNLOCK_VALUE, out state, "Unlock device"))
                {
                    error = $"Failed to unlock device (state={state}).";
                    return false;
                }
                System.Threading.Thread.Sleep(600);

                foreach (var z in switchZones)
                {
                    int main = Math.Clamp(z.MainZone, 0, 4);
                    int sub = Math.Clamp(z.SubZone, 0, 6);
                    ushort zoneBase = RtvSwitchBaseAddr(main, sub);

                    int lat32 = (int)Math.Round(z.Latitude * 1_000_000.0, MidpointRounding.AwayFromZero);
                    int lon32 = (int)Math.Round(z.Longitude * 1_000_000.0, MidpointRounding.AwayFromZero);

                    ushort latHi = (ushort)((lat32 >> 16) & 0xFFFF);
                    ushort latLo = (ushort)(lat32 & 0xFFFF);
                    ushort lonHi = (ushort)((lon32 >> 16) & 0xFFFF);
                    ushort lonLo = (ushort)(lon32 & 0xFFFF);

                    ushort lengthCm = ToUInt16(z.Height * 100.0);
                    ushort widthCm = ToUInt16(z.Width * 100.0);
                    ushort az = (ushort)Math.Clamp(z.Azimuth, 0, 359);

                    // Re-unlock per zone
                    modbus.WriteToHoldingRegister(unitId, Off1(UNLOCK_REGISTER), UNLOCK_VALUE, out _, "Re-unlock RTV");
                    System.Threading.Thread.Sleep(250);

                    if (!modbus.WriteToHoldingRegister(unitId, Off1(zoneBase), latHi, out state, $"RTV {main + 1}-{sub + 1} lat0")) { error = $"W @0x{zoneBase:X4} st={state}"; return false; }
                    System.Threading.Thread.Sleep(interWriteDelayMs);
                    if (!modbus.WriteToHoldingRegister(unitId, Off1((ushort)(zoneBase + 1)), latLo, out state, $"RTV {main + 1}-{sub + 1} lat1")) { error = $"W @0x{zoneBase + 1:X4} st={state}"; return false; }
                    System.Threading.Thread.Sleep(interWriteDelayMs);
                    if (!modbus.WriteToHoldingRegister(unitId, Off1((ushort)(zoneBase + 2)), lonHi, out state, $"RTV {main + 1}-{sub + 1} lon0")) { error = $"W @0x{zoneBase + 2:X4} st={state}"; return false; }
                    System.Threading.Thread.Sleep(interWriteDelayMs);
                    if (!modbus.WriteToHoldingRegister(unitId, Off1((ushort)(zoneBase + 3)), lonLo, out state, $"RTV {main + 1}-{sub + 1} lon1")) { error = $"W @0x{zoneBase + 3:X4} st={state}"; return false; }
                    System.Threading.Thread.Sleep(interWriteDelayMs);
                    if (!modbus.WriteToHoldingRegister(unitId, Off1((ushort)(zoneBase + 4)), lengthCm, out state, $"RTV {main + 1}-{sub + 1} len")) { error = $"W @0x{zoneBase + 4:X4} st={state}"; return false; }
                    System.Threading.Thread.Sleep(interWriteDelayMs);
                    if (!modbus.WriteToHoldingRegister(unitId, Off1((ushort)(zoneBase + 6)), widthCm, out state, $"RTV {main + 1}-{sub + 1} wid")) { error = $"W @0x{zoneBase + 6:X4} st={state}"; return false; }
                    System.Threading.Thread.Sleep(interWriteDelayMs);
                    if (!modbus.WriteToHoldingRegister(unitId, Off1((ushort)(zoneBase + 8)), az, out state, $"RTV {main + 1}-{sub + 1} az")) { error = $"W @0x{zoneBase + 8:X4} st={state}"; return false; }
                    System.Threading.Thread.Sleep(interWriteDelayMs);

                    // Verify
                    ushort[]? rd = modbus.ReadDataFrom16bitRegisters(unitId, Off1(zoneBase), 10, RegType16b.HoldingRegister, out var stRd, $"Verify RTV {main + 1}-{sub + 1}");
                    if (stRd != ModbusStateCode.Success || rd == null || rd.Length < 9)
                    {
                        error = $"Readback failed at 0x{zoneBase:X4}, state={stRd}";
                        return false;
                    }
                    int rbLat = (rd[0] << 16) | rd[1];
                    int rbLon = (rd[2] << 16) | rd[3];
                    if (rbLat != lat32 || rbLon != lon32)
                    {
                        error = $"Mismatch after write at 0x{zoneBase:X4} (lat/lon differ).";
                        return false;
                    }
                }

                modbus.WriteToHoldingRegister(unitId, Off1(UNLOCK_REGISTER), 0, out _, "Lock device");
                return true;
            }
            catch (Exception ex)
            {
                state = ModbusStateCode.UndefinedError;
                error = ex.Message;
                return false;
            }
        }


        // --- READ: MPC-RTV switch zones over TCP ---
        private static List<ActivationZone> ReadRtvSwitchZonesTcp(ModbusTcpIp modbus, byte unitId)
        {
            var list = new List<ActivationZone>();

            // main: 0..4 (=> 1..5), sub: 0..6 (=> 1..7)
            for (int m = 0; m <= 4; m++)
            {
                for (int s = 0; s <= 6; s++)
                {
                    ushort baseAddr = RtvSwitchBaseAddr(m, s);
                    var data = modbus.ReadDataFrom16bitRegisters(
                        unitId, baseAddr, 10, RegType16b.HoldingRegister, out var st,
                        $"Read RTV {m + 1}-{s + 1}");

                    if (st != ModbusStateCode.Success || data == null || data.Length < 9)
                        continue;

                    // Empty slot?
                    if (data.All(v => v == 0)) continue;

                    int lat = (data[0] << 16) | data[1];
                    int lon = (data[2] << 16) | data[3];
                    if (lat == 0 && lon == 0) continue;

                    ushort lengthCm = data[4];
                    ushort widthCm = data[6];
                    ushort az = data[8];

                    list.Add(new ActivationZone
                    {
                        Name = $"Switch {m + 1}-{s + 1}",
                        MainZone = m,
                        SubZone = s,
                        Latitude = lat / 1_000_000.0,
                        Longitude = lon / 1_000_000.0,
                        Height = lengthCm / 100.0,
                        Width = widthCm / 100.0,
                        Azimuth = az
                    });
                }
            }

            return list;
        }

        // --- READ: MPC-RTV switch zones over RTU ---
        private static async Task<List<ActivationZone>> ReadRtvSwitchZonesRtuAsync(ExportSettings s, byte unitId, int timeoutMs)
        {
            var list = new List<ActivationZone>();

            for (int m = 0; m <= 4; m++)
            {
                for (int sIdx = 0; sIdx <= 6; sIdx++)
                {
                    ushort baseAddr = RtvSwitchBaseAddr(m, sIdx);
                    var (ok, st, data, err) = await ReadHoldingRegistersRtuAsync(s, unitId, baseAddr, 10, timeoutMs);
                    if (!ok || st != ModbusStateCode.Success || data == null || data.Length < 9)
                        continue;

                    if (data.All(v => v == 0)) continue;

                    int lat = (data[0] << 16) | data[1];
                    int lon = (data[2] << 16) | data[3];
                    if (lat == 0 && lon == 0) continue;

                    ushort lengthCm = data[4];
                    ushort widthCm = data[6];
                    ushort az = data[8];

                    list.Add(new ActivationZone
                    {
                        Name = $"Switch {m + 1}-{sIdx + 1}",
                        MainZone = m,
                        SubZone = sIdx,
                        Latitude = lat / 1_000_000.0,
                        Longitude = lon / 1_000_000.0,
                        Height = lengthCm / 100.0,
                        Width = widthCm / 100.0,
                        Azimuth = az
                    });
                }
            }

            return list;
        }

        // Helper: only export zones with sane geo + size
        private static bool HasValidGeoAndSize(ActivationZone z) =>
            z != null
            && double.IsFinite(z.Latitude) && double.IsFinite(z.Longitude)
            && z.Width > 0 && z.Height > 0;


        private static bool WriteActivationZoneCountTcp(
        string host, int port, byte unitId, int zoneCount,
        int connectTimeoutMs, int sendTimeoutMs, int receiveTimeoutMs,
        out ModbusStateCode state, out string? error)
        {
            state = ModbusStateCode.Success;
            error = null;
            return true;
        }


        private static async Task<(bool ok, ModbusStateCode state, string? error)> WriteActivationZoneCountRtuAsync(
        ExportSettings s, byte unitId, int zoneCount, int timeoutMs)
        {
            // Per request: do not touch 0x0300 at all. Header write disabled.
            return (true, ModbusStateCode.Success, null);
        }



        private static string DebugFloatConversion(float value, string name)
        {
            var bytes = BitConverter.GetBytes(value);
            var (lo, hi) = FloatToWordsWS(value);
            return $"{name}={value:F8} -> bytes=[{bytes[0]:X2},{bytes[1]:X2},{bytes[2]:X2},{bytes[3]:X2}] " +
                   $"-> WS=[0x{lo:X4},0x{hi:X4}] (decimal: [{lo},{hi}])";
        }

        // Update the write section to add detailed logging:
        private static async Task<(bool ok, ModbusStateCode state, string? error)> WriteMpcZonesByExactRegistersRtuAsync(
    ExportSettings s, byte unitId, IReadOnlyList<ActivationZone> zones, int timeoutMs)
        {
            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;
            const int MAX_REGS_PER_CHUNK = 50; // Dávka 50 registrů najednou

            try
            {
                System.Diagnostics.Debug.WriteLine("=== Writing MPC zones - FLOAT32 format, LON first (BATCHED) ===");

                // Odemknout
                System.Diagnostics.Debug.WriteLine($"Unlocking at 0x{UNLOCK_REGISTER:X4}...");
                var (uok, ust, uerr) = await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs);
                if (!uok || ust != ModbusStateCode.Success)
                    return (false, ust, $"Unlock failed: {uerr}");
                System.Diagnostics.Debug.WriteLine($"   OK");
                await Task.Delay(800);

                const ushort BASE_ADDR = MPC_BASE_ADDR;
                const ushort WRITE_OFFSET = MPCv3RTV_WRITE_OFFSET;
                const int ZONE_STRIDE = MPC_ZONE_STRIDE;
                ushort FIRST_ZONE_BASE = (ushort)(BASE_ADDR + WRITE_OFFSET);

                // Sestavit všechny registry do seřazeného seznamu (adresa, hodnota)
                var allRegisters = new List<(ushort addr, ushort value)>();

                for (int idx = 0; idx < zones.Count; idx++)
                {
                    var z = zones[idx];
                    int mainZone = z.MainZone + 1;
                    int subZone = z.SubZone + 1;
                    int zoneIndex = ((mainZone - 1) * 5) + (subZone - 1);
                    ushort zoneBase = (ushort)(FIRST_ZONE_BASE + (zoneIndex * ZONE_STRIDE));

                    float lonF = (float)z.Longitude;
                    float latF = (float)z.Latitude;
                    float heightF = (float)z.Height;
                    float widthF = (float)z.Width;
                    float azF = (float)z.Azimuth;

                    var (lonLo, lonHi) = FloatToWordsWS(lonF);
                    var (latLo, latHi) = FloatToWordsWS(latF);
                    var (heightLo, heightHi) = FloatToWordsWS(heightF);
                    var (widthLo, widthHi) = FloatToWordsWS(widthF);
                    var (azLo, azHi) = FloatToWordsWS(azF);

                    // Přidat registry do seznamu (adresa musí být souvislá!)
                    allRegisters.Add((zoneBase, lonLo));
                    allRegisters.Add(((ushort)(zoneBase + 1), lonHi));
                    allRegisters.Add(((ushort)(zoneBase + 2), latLo));
                    allRegisters.Add(((ushort)(zoneBase + 3), latHi));
                    allRegisters.Add(((ushort)(zoneBase + 4), heightLo));
                    allRegisters.Add(((ushort)(zoneBase + 5), heightHi));
                    allRegisters.Add(((ushort)(zoneBase + 6), widthLo));
                    allRegisters.Add(((ushort)(zoneBase + 7), widthHi));
                    allRegisters.Add(((ushort)(zoneBase + 8), azLo));
                    allRegisters.Add(((ushort)(zoneBase + 9), azHi));
                }

                System.Diagnostics.Debug.WriteLine($"\nTotal registers to write: {allRegisters.Count}");

                // Rozdělit do souvislých dávek po 50 registrech
                int offset = 0;
                while (offset < allRegisters.Count)
                {
                    // Odemknout před každou dávkou
                    var (ruOk, ruSt, ruErr) = await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs);
                    if (!ruOk || ruSt != ModbusStateCode.Success)
                        return (false, ruSt, $"Re-unlock failed: {ruErr}");
                    await Task.Delay(250);

                    // Určit velikost dávky (max 50 nebo méně, pokud registry nejsou souvislé)
                    int chunkSize = Math.Min(MAX_REGS_PER_CHUNK, allRegisters.Count - offset);

                    // Zkontrolovat souvislost adres
                    ushort startAddr = allRegisters[offset].addr;
                    int actualChunkSize = 1;

                    for (int i = 1; i < chunkSize; i++)
                    {
                        if (allRegisters[offset + i].addr != startAddr + i)
                        {
                            // Nalezena mezera v adresách, ukončit dávku zde
                            break;
                        }
                        actualChunkSize++;
                    }

                    // Připravit data pro zápis
                    var chunkData = new ushort[actualChunkSize];
                    for (int i = 0; i < actualChunkSize; i++)
                    {
                        chunkData[i] = allRegisters[offset + i].value;
                    }

                    System.Diagnostics.Debug.WriteLine($"\nWriting batch: {actualChunkSize} regs from 0x{startAddr:X4} (offset {offset}/{allRegisters.Count})");

                    // Zapsat dávku
                    var (wok, wst, werr) = await WriteHoldingRegistersRtuChunkedAsync(s, unitId, startAddr, chunkData, timeoutMs);
                    if (!wok || wst != ModbusStateCode.Success)
                    {
                        System.Diagnostics.Debug.WriteLine($"   FAILED: {werr}");
                        return (false, wst, $"Batch write failed at 0x{startAddr:X4}: {werr}");
                    }

                    System.Diagnostics.Debug.WriteLine($"   OK ({actualChunkSize} registers written)");

                    offset += actualChunkSize;
                    await Task.Delay(150);
                }

                // Lock
                System.Diagnostics.Debug.WriteLine($"\nLocking...");
                await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { (ushort)0 }, timeoutMs);

                System.Diagnostics.Debug.WriteLine("\n=== ALL ZONES WRITTEN (BATCHED FLOAT32) ===");
                return (true, ModbusStateCode.Success, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EXCEPTION: {ex}");
                return (false, ModbusStateCode.UndefinedError, ex.Message);
            }
        }


        // Add near other helpers
        private static async Task<(bool ok, ModbusStateCode state, string? error)> UnlockRtuAsync(
            ExportSettings s, byte unitId, int timeoutMs, int settleMs = 500)
        {
            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;

            // Strict unlock exactly on 0x103F (no Off1 / ±1 fallback)
            var (ok, st, err) = await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs);
            if (ok && st == ModbusStateCode.Success)
            {
                if (settleMs > 0) await Task.Delay(settleMs);
                return (true, ModbusStateCode.Success, null);
            }

            return (false, st, string.IsNullOrWhiteSpace(err) ? "Unlock failed at 0x103F" : err);
        }

        // Helper: try to write a single holding register for unlock/lock with robust fallbacks
        private static bool TryWriteUnlockTcp(string host, int port, byte unitId, ushort addr, ushort value,
            int connectTimeoutMs, int sendTimeoutMs, int receiveTimeoutMs,
            out ModbusStateCode state, out string? error)
        {
            // 1) Raw wire write first (exact address)
            if (WriteHoldingRegistersTcpDirectExact(host, port, unitId, addr, new[] { value },
                connectTimeoutMs, sendTimeoutMs, receiveTimeoutMs, out state, out error))
                return true;

            // 2) Fallback: library with OffsetFromOne -> pass Off1(addr)
            try
            {
                var p = new ProtocolParams
                {
                    Flags = ProtocolFlags.OffsetFromOne,
                    SuccessiveRequestDelay = 100,
                    ConnectionTimeout = Math.Min(connectTimeoutMs, 5000),
                    SendTimeout = Math.Min(sendTimeoutMs, 3000),
                    ReceiveTimeout = Math.Min(receiveTimeoutMs, 5000),
                    ReceiveAgainTimeout = 3000
                };
                using var modbus = new ModbusTcpIp($"{host}:{port}", p);

                if (modbus.WriteToHoldingRegister(unitId, Off1(addr), value, out state, "Unlock/Lock (Off1)"))
                    return state == ModbusStateCode.Success;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            // 3) Last fallback: library without Off1 (some builds ignore the flag for FC16)
            try
            {
                var p2 = new ProtocolParams
                {
                    Flags = ProtocolFlags.OffsetFromOne,
                    SuccessiveRequestDelay = 100,
                    ConnectionTimeout = Math.Min(connectTimeoutMs, 5000),
                    SendTimeout = Math.Min(sendTimeoutMs, 3000),
                    ReceiveTimeout = Math.Min(receiveTimeoutMs, 5000),
                    ReceiveAgainTimeout = 3000
                };
                using var modbus2 = new ModbusTcpIp($"{host}:{port}", p2);

                if (modbus2.WriteToHoldingRegister(unitId, addr, value, out state, "Unlock/Lock (raw addr)"))
                    return state == ModbusStateCode.Success;
            }
            catch (Exception ex2)
            {
                error = ex2.Message;
            }

            if (string.IsNullOrWhiteSpace(error)) error = "All unlock/lock attempts failed.";
            return false;
        }

        private static string BuildZonesDebugReport(IEnumerable<ActivationZone> zones, int maxRows = 8, string title = "Zones debug")
        {
            var z = zones.ToList();
            var sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine($"Total: {z.Count}");
            if (z.Count > 0)
            {
                var latMin = z.Min(a => a.Latitude);
                var latMax = z.Max(a => a.Latitude);
                var lonMin = z.Min(a => a.Longitude);
                var lonMax = z.Max(a => a.Longitude);
                var wMin = z.Min(a => a.Width);
                var wMax = z.Max(a => a.Width);
                var hMin = z.Min(a => a.Height);
                var hMax = z.Max(a => a.Height);
                sb.AppendLine($"Lat range: {latMin} .. {latMax}");
                sb.AppendLine($"Lon range: {lonMin} .. {lonMax}");
                sb.AppendLine($"Width range: {wMin} .. {wMax}");
                sb.AppendLine($"Height range: {hMin} .. {hMax}");
            }
            sb.AppendLine("Sample:");
            foreach (var a in z.Take(maxRows))
            {
                sb.AppendLine($"  {a.MainZone + 1}-{a.SubZone + 1}: Lat={a.Latitude}, Lon={a.Longitude}, W={a.Width}, H={a.Height}, Az={a.Azimuth}, Name='{a.Name}'");
            }
            return sb.ToString();
        }

        // Float32 <-> two 16-bit holding registers using the same mapping you provided.
        // Mapping (Float -> UInt32):
        //   u = (b[1] << 24) | (b[0] << 16) | (b[3] << 8) | b[2];  // b = BitConverter.GetBytes(float)
        // We then split u to (hi=upper16, lo=lower16).
        private static (ushort hi, ushort lo) FloatToMpcWords(float value)
        {
            // BitConverter is little-endian on .NET
            var b = BitConverter.GetBytes(value);
            uint u = ((uint)b[1] << 24) | ((uint)b[0] << 16) | ((uint)b[3] << 8) | (uint)b[2];
            return ((ushort)(u >> 16), (ushort)(u & 0xFFFF));
        }

        // Overload to accept double (casts to float32 on-wire)
        private static (ushort hi, ushort lo) DoubleToMpcWords(double value)
        {
            return FloatToMpcWords((float)value);
        }

        // Reverse mapping (UInt32 -> Float) matching your ConvertUInt32ToFloat:
        // byteArray[2] = value >> 24; byteArray[3] = value >> 16; byteArray[0] = value >> 8; byteArray[1] = value >> 0;
        // Array.Reverse(byteArray); then BitConverter.ToSingle.
        private static float MpcWordsToFloat(ushort hi, ushort lo)
        {
            uint u = ((uint)hi << 16) | lo;
            var byteArray = new byte[4];
            byteArray[2] = (byte)((u >> 24) & 0xFF);
            byteArray[3] = (byte)((u >> 16) & 0xFF);
            byteArray[0] = (byte)((u >> 8) & 0xFF);
            byteArray[1] = (byte)(u & 0xFF);
            Array.Reverse(byteArray);
            return BitConverter.ToSingle(byteArray, 0);
        }

        private static (ushort hi, ushort lo) FloatToWordsBE(float value)
        {
            var b = BitConverter.GetBytes(value); // LE in .NET
            ushort hi = (ushort)((b[3] << 8) | b[2]);
            ushort lo = (ushort)((b[1] << 8) | b[0]);
            return (hi, lo);
        }

        // Two 16-bit registers -> Float32 (big-endian per 16-bit word)
        private static float WordsToFloatBE(ushort hi, ushort lo)
        {
            var b = new byte[4];
            b[0] = (byte)(lo & 0xFF);
            b[1] = (byte)(lo >> 8);
            b[2] = (byte)(hi & 0xFF);
            b[3] = (byte)(hi >> 8);
            return BitConverter.ToSingle(b, 0);
        }

        // --- WRITE: MPC-RTV switch zones over RTU (RS485) ---
        private static async Task<(bool ok, ModbusStateCode state, string? error)> WriteRtvSwitchesRtuAsync(
            ExportSettings s, byte unitId, IReadOnlyList<ActivationZone> switchZones, int timeoutMs)
        {
            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;
            const int interWriteDelayMs = 200;

            try
            {
                // 1) Unlock
                var (uok, ust, uerr) = await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs);
                if (!uok || ust != ModbusStateCode.Success)
                    return (false, ust, $"Unlock failed: {uerr}");
                await Task.Delay(600);

                foreach (var z in switchZones)
                {
                    int main = Math.Clamp(z.MainZone, 0, 4);
                    int sub = Math.Clamp(z.SubZone, 0, 6);
                    ushort baseAddr = RtvSwitchBaseAddr(main, sub);

                    int lat32 = (int)Math.Round(z.Latitude * 1_000_000.0, MidpointRounding.AwayFromZero);
                    int lon32 = (int)Math.Round(z.Longitude * 1_000_000.0, MidpointRounding.AwayFromZero);

                    ushort latHi = (ushort)((lat32 >> 16) & 0xFFFF);
                    ushort latLo = (ushort)(lat32 & 0xFFFF);
                    ushort lonHi = (ushort)((lon32 >> 16) & 0xFFFF);
                    ushort lonLo = (ushort)(lon32 & 0xFFFF);

                    ushort lengthCm = ToUInt16(z.Height * 100.0);
                    ushort widthCm = ToUInt16(z.Width * 100.0);
                    ushort az = (ushort)Math.Clamp(z.Azimuth, 0, 359);

                    // Re-unlock per zone
                    await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs);
                    await Task.Delay(250);

                    var (ok1, st1, err1) = await WriteHoldingRegistersRtuAsync(s, unitId, baseAddr, new[] { latHi }, timeoutMs);
                    if (!ok1 || st1 != ModbusStateCode.Success) return (false, st1, $"Switch lat0 failed @0x{baseAddr:X4}: {err1}");
                    if (interWriteDelayMs > 0) await Task.Delay(interWriteDelayMs);

                    var (ok2, st2, err2) = await WriteHoldingRegistersRtuAsync(s, unitId, (ushort)(baseAddr + 1), new[] { latLo }, timeoutMs);
                    if (!ok2 || st2 != ModbusStateCode.Success) return (false, st2, $"Switch lat1 failed @0x{(baseAddr + 1):X4}: {err2}");
                    if (interWriteDelayMs > 0) await Task.Delay(interWriteDelayMs);

                    var (ok3, st3, err3) = await WriteHoldingRegistersRtuAsync(s, unitId, (ushort)(baseAddr + 2), new[] { lonHi }, timeoutMs);
                    if (!ok3 || st3 != ModbusStateCode.Success) return (false, st3, $"Switch lon0 failed @0x{(baseAddr + 2):X4}: {err3}");
                    if (interWriteDelayMs > 0) await Task.Delay(interWriteDelayMs);

                    var (ok4, st4, err4) = await WriteHoldingRegistersRtuAsync(s, unitId, (ushort)(baseAddr + 3), new[] { lonLo }, timeoutMs);
                    if (!ok4 || st4 != ModbusStateCode.Success) return (false, st4, $"Switch lon1 failed @0x{(baseAddr + 3):X4}: {err4}");
                    if (interWriteDelayMs > 0) await Task.Delay(interWriteDelayMs);

                    var (ok5, st5, err5) = await WriteHoldingRegistersRtuAsync(s, unitId, (ushort)(baseAddr + 4), new[] { lengthCm }, timeoutMs);
                    if (!ok5 || st5 != ModbusStateCode.Success) return (false, st5, $"Switch length failed @0x{(baseAddr + 4):X4}: {err5}");
                    if (interWriteDelayMs > 0) await Task.Delay(interWriteDelayMs);

                    var (ok6, st6, err6) = await WriteHoldingRegistersRtuAsync(s, unitId, (ushort)(baseAddr + 6), new[] { widthCm }, timeoutMs);
                    if (!ok6 || st6 != ModbusStateCode.Success) return (false, st6, $"Switch width failed @0x{(baseAddr + 6):X4}: {err6}");
                    if (interWriteDelayMs > 0) await Task.Delay(interWriteDelayMs);

                    var (ok7, st7, err7) = await WriteHoldingRegistersRtuAsync(s, unitId, (ushort)(baseAddr + 8), new[] { az }, timeoutMs);
                    if (!ok7 || st7 != ModbusStateCode.Success) return (false, st7, $"Switch azimuth failed @0x{(baseAddr + 8):X4}: {err7}");
                    if (interWriteDelayMs > 0) await Task.Delay(interWriteDelayMs);

                    var (rok, rst, rdata, rerr) = await ReadHoldingRegistersRtuAsync(s, unitId, baseAddr, 10, timeoutMs);
                    if (!rok || rst != ModbusStateCode.Success || rdata == null || rdata.Length < 9)
                        return (false, rst, $"Verify read failed @0x{baseAddr:X4}: {rerr}");

                    int rbLat = (rdata[0] << 16) | rdata[1];
                    int rbLon = (rdata[2] << 16) | rdata[3];
                    if (rbLat != lat32 || rbLon != lon32 || rdata[4] != lengthCm || rdata[6] != widthCm || rdata[8] != az)
                        return (false, ModbusStateCode.WrongResponse, $"Verify mismatch @0x{baseAddr:X4}");
                }

                // Lock device
                await WriteHoldingRegistersRtuAsync(s, unitId, UNLOCK_REGISTER, new[] { (ushort)0 }, timeoutMs);
                return (true, ModbusStateCode.Success, null);
            }
            catch (Exception ex)
            {
                return (false, ModbusStateCode.UndefinedError, ex.Message);
            }
        }

        // Float32 -> two 16-bit registers (word-swapped: LO first, then HI)
        private static (ushort first, ushort second) FloatToWordsWS(float value)
        {
            var (hi, lo) = FloatToWordsBE(value);
            return (lo, hi);
        }

        // Two regs -> Float32 (word-swapped: LO first, then HI)
        private static float WordsToFloatWS(ushort first, ushort second)
        {
            // first=LO, second=HI -> feed BE as (HI, LO)
            return WordsToFloatBE(second, first);
        }

        private async void ReinitMPC_Click(object sender, RoutedEventArgs e)
        {
            Settings = ExportSettings.FromWindow(this);
            if (Settings == null)
            {
                MessageBox.Show("Missing export settings.", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Determine connection type
            string conn = (ConnectionComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Trim()?.ToLower() ?? "";
            bool isTcp = conn == "modbus tcp";
            bool isRtu = conn == "serial port";
            bool isTunnel = conn == "serial tunnel";

            if (!isTcp && !isRtu && !isTunnel)
            {
                MessageBox.Show("Please select a connection type.", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Get unit ID
            byte unitId = 1;
            if (Settings.ModemDec.HasValue)
            {
                int v = Settings.ModemDec.Value;
                if (v < 1 || v > 247)
                {
                    MessageBox.Show("Module address must be 1–247.", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                unitId = (byte)v;
            }

            // Použít progress bar s více kroky
            ShowBusy("Re-initializing MPC...", showProgress: true, maxProgress: 100);
            await Task.Delay(50);

            const ushort REINIT_REGISTER = 0x0183;
            const ushort REINIT_VALUE = 0x0001;
            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;
            const int timeoutMs = 5000;

            try
            {
                System.Diagnostics.Debug.WriteLine("=== MPC RE-INIT ===");

                if (isRtu || isTunnel)
                {
                    System.Diagnostics.Debug.WriteLine("Using Modbus ASCII (RTU)");

                    // Step 1: Connecting (0-10%)
                    UpdateProgress(5, 100, "Connecting to device...");
                    await Task.Delay(300);
                    UpdateProgress(10, 100, "Connected");

                    // Step 2: Unlock (10-40%)
                    UpdateProgress(15, 100, "Unlocking device...");
                    System.Diagnostics.Debug.WriteLine($"Unlocking at 0x{UNLOCK_REGISTER:X4}...");
                    var (uok, ust, uerr) = await WriteHoldingRegistersRtuAsync(Settings, unitId, UNLOCK_REGISTER, new[] { UNLOCK_VALUE }, timeoutMs);
                    if (!uok || ust != ModbusStateCode.Success)
                    {
                        await ShowMessageAfterBusyAsync($"Unlock failed: {uerr}", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    System.Diagnostics.Debug.WriteLine("   Unlock OK");
                    UpdateProgress(40, 100, "Device unlocked");
                    await Task.Delay(500);

                    // Step 3: Re-init (40-75%)
                    UpdateProgress(45, 100, "Sending re-init command...");
                    await Task.Delay(200);
                    System.Diagnostics.Debug.WriteLine($"Writing re-init to 0x{REINIT_REGISTER:X4}...");
                    var (rok, rst, rerr) = await WriteHoldingRegistersRtuAsync(Settings, unitId, REINIT_REGISTER, new[] { REINIT_VALUE }, timeoutMs);

                    if (!rok || rst != ModbusStateCode.Success)
                    {
                        await ShowMessageAfterBusyAsync($"Re-init failed at 0x{REINIT_REGISTER:X4}: {rerr}", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    System.Diagnostics.Debug.WriteLine("   Re-init sent");
                    UpdateProgress(75, 100, "Re-init command sent");
                    await Task.Delay(1000);

                    // Step 4: Lock (75-95%)
                    UpdateProgress(80, 100, "Locking device...");
                    await WriteHoldingRegistersRtuAsync(Settings, unitId, UNLOCK_REGISTER, new[] { (ushort)0 }, timeoutMs);
                    UpdateProgress(95, 100, "Device locked");
                    await Task.Delay(300);

                    // Step 5: Complete (95-100%)
                    UpdateProgress(100, 100, "Complete");
                    await Task.Delay(200);
                    await ShowMessageAfterBusyAsync("MPC re-init successful!", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (isTcp)
                {
                    System.Diagnostics.Debug.WriteLine("Using Modbus TCP");

                    string host = Settings.TcpHost?.Trim() ?? "";
                    int port = Settings.TcpPort ?? 502;

                    if (string.IsNullOrEmpty(host))
                    {
                        await ShowMessageAfterBusyAsync("TCP host is required.", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Přesunout TCP operace do background thread
                    await Task.Run(async () =>
                    {
                        // Step 1: Connecting (0-10%)
                        await Dispatcher.InvokeAsync(() => UpdateProgress(5, 100, "Connecting to device..."), DispatcherPriority.Background);
                        await Task.Delay(300);
                        await Dispatcher.InvokeAsync(() => UpdateProgress(10, 100, "Connected"), DispatcherPriority.Background);

                        // Step 2: Unlock (10-40%)
                        await Dispatcher.InvokeAsync(() => UpdateProgress(15, 100, "Unlocking device..."), DispatcherPriority.Background);

                        if (!TryWriteUnlockTcp(host, port, unitId, UNLOCK_REGISTER, UNLOCK_VALUE, 5000, 3000, 5000, out var ust, out var uerr))
                        {
                            await Dispatcher.InvokeAsync(async () =>
                            {
                                await ShowMessageAfterBusyAsync($"Unlock failed: {uerr}", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Error);
                            });
                            return;
                        }
                        await Dispatcher.InvokeAsync(() => UpdateProgress(40, 100, "Device unlocked"), DispatcherPriority.Background);
                        await Task.Delay(500);

                        // Step 3: Re-init (40-75%)
                        await Dispatcher.InvokeAsync(() => UpdateProgress(45, 100, "Sending re-init command..."), DispatcherPriority.Background);
                        await Task.Delay(200);

                        using var client = new System.Net.Sockets.TcpClient();
                        client.SendTimeout = 3000;
                        client.ReceiveTimeout = 5000;

                        await client.ConnectAsync(host, port);
                        using var stream = client.GetStream();

                        // Build Modbus TCP frame for write single register (FC06)
                        var frame = new byte[12];
                        ushort tid = 1;
                        frame[0] = (byte)(tid >> 8);
                        frame[1] = (byte)(tid & 0xFF);
                        frame[2] = 0;
                        frame[3] = 0;
                        frame[4] = 0;
                        frame[5] = 6;
                        frame[6] = unitId;
                        frame[7] = 0x06;
                        frame[8] = (byte)(REINIT_REGISTER >> 8);
                        frame[9] = (byte)(REINIT_REGISTER & 0xFF);
                        frame[10] = (byte)(REINIT_VALUE >> 8);
                        frame[11] = (byte)(REINIT_VALUE & 0xFF);

                        await stream.WriteAsync(frame, 0, frame.Length);
                        await stream.FlushAsync();

                        var resp = new byte[12];
                        int read = await stream.ReadAsync(resp, 0, resp.Length);

                        await Dispatcher.InvokeAsync(() => UpdateProgress(75, 100, "Re-init command sent"), DispatcherPriority.Background);
                        await Task.Delay(1000);

                        // Step 4: Lock (75-95%)
                        await Dispatcher.InvokeAsync(() => UpdateProgress(80, 100, "Locking device..."), DispatcherPriority.Background);
                        TryWriteUnlockTcp(host, port, unitId, UNLOCK_REGISTER, 0, 5000, 3000, 5000, out _, out _);
                        await Dispatcher.InvokeAsync(() => UpdateProgress(95, 100, "Device locked"), DispatcherPriority.Background);
                        await Task.Delay(300);

                        // Step 5: Complete (95-100%)
                        await Dispatcher.InvokeAsync(async () =>
                        {
                            UpdateProgress(100, 100, "Complete");
                            await Task.Delay(200);

                            if (read >= 9 && resp[7] == 0x06)
                            {
                                System.Diagnostics.Debug.WriteLine("   Re-init sent (TCP)");
                                await ShowMessageAfterBusyAsync("MPC re-init successful!", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Information);
                            }
                            else
                            {
                                await ShowMessageAfterBusyAsync("Re-init response invalid", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                await ShowMessageAfterBusyAsync($"Exception: {ex.Message}", "Reinit MPC", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                HideBusy();
            }
        }

        // Add this new TCP worker (pure, no UI)
        private static (bool ok, string msgOrError) ReinitTcpWorker(string host, int port, byte unitId, ushort reinitAddr, ushort reinitVal, bool asSerialTcp = false)
        {
            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;

            try
            {
                // robust unlock
                if (!TryWriteUnlockTcp(host, port, unitId, UNLOCK_REGISTER, UNLOCK_VALUE,
                                       connectTimeoutMs: 3000, sendTimeoutMs: 2000, receiveTimeoutMs: 5000,
                                       out var stUnlock, out var errUnlock))
                {
                    return (false, $"Error unlocking targeted address (state={stUnlock}{(string.IsNullOrWhiteSpace(errUnlock) ? "" : $", {errUnlock}")}).");
                }
                System.Threading.Thread.Sleep(600);

                var protocolParams = BuildProtocolParams(asSerialTcp);
                protocolParams.ConnectionTimeout = 3000;
                protocolParams.SendTimeout = 2000;
                protocolParams.ReceiveTimeout = 3000;
                protocolParams.ReceiveAgainTimeout = 2000;

                using var modbus = new ModbusTcpIp($"{host}:{port}", protocolParams);

                if (!modbus.WriteToHoldingRegister(unitId, reinitAddr, reinitVal, out var st, "V2X REINIT"))
                {
                    return (false, $"Reinit error: state={st}");
                }

                // lock back
                TryWriteUnlockTcp(host, port, unitId, UNLOCK_REGISTER, 0,
                    connectTimeoutMs: 3000, sendTimeoutMs: 2000, receiveTimeoutMs: 5000,
                    out _, out _);

                return (true, "OK");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }


        // TCP: FC06 - Write Single Register (exact on-wire address)
        private static bool WriteSingleRegisterTcpFC16(
        string host, int port, byte unitId,
        ushort addr, ushort value,
        int connectTimeoutMs, int sendTimeoutMs, int receiveTimeoutMs,
        out ModbusStateCode state, out string? error)
        {
            state = ModbusStateCode.Success;
            error = null;

            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                if (!connectTask.Wait(connectTimeoutMs))
                {
                    state = ModbusStateCode.Timeout;
                    error = "Connect timeout";
                    return false;
                }

                client.NoDelay = true;
                client.SendTimeout = Math.Max(sendTimeoutMs, 3000);
                client.ReceiveTimeout = Math.Max(receiveTimeoutMs, 5000);

                using var stream = client.GetStream();

                // MBAP (7) + PDU (FC16: 1 + 2 + 2 + 2 + 1 + 2)
                ushort txId = 1;
                ushort regCount = 1;

                byte[] buf = new byte[13];
                // MBAP
                buf[0] = (byte)(txId >> 8);
                buf[1] = (byte)(txId & 0xFF);
                buf[2] = 0x00;
                buf[3] = 0x00;
                ushort lenField = (ushort)(1 + 6); // UnitId + PDU length
                buf[4] = (byte)(lenField >> 8);
                buf[5] = (byte)(lenField & 0xFF);
                buf[6] = unitId;

                // PDU: FC16
                buf[7] = 0x10;                  // Function code
                buf[8] = (byte)(addr >> 8);     // Start addr hi
                buf[9] = (byte)(addr & 0xFF);   // Start addr lo
                buf[10] = (byte)(regCount >> 8); // Quantity hi
                buf[11] = (byte)(regCount & 0xFF); // Quantity lo
                buf[12] = 2;                     // Byte count
                                                 // values
                var valBytes = new byte[2] { (byte)(value >> 8), (byte)(value & 0xFF) };

                stream.Write(buf, 0, buf.Length);
                stream.Write(valBytes, 0, 2);

                // MBAP header
                if (!ReadExact(stream, 7, client.ReceiveTimeout, out var mbapResp))
                {
                    state = ModbusStateCode.Timeout;
                    error = "No MBAP response";
                    return false;
                }
                int respLen = (mbapResp[4] << 8) | mbapResp[5];
                if (!ReadExact(stream, respLen, client.ReceiveTimeout, out var rest))
                {
                    state = ModbusStateCode.Timeout;
                    error = "Response timeout";
                    return false;
                }

                // Exception?
                if ((rest[1] & 0x80) != 0)
                {
                    state = ModbusStateCode.IllegalDataAddr;
                    error = $"Exception code 0x{rest[2]:X2}";
                    return false;
                }

                // expected response: register count + values
                if (rest[0] != unitId || rest[1] != 0x10)
                {
                    state = ModbusStateCode.WrongResponse;
                    error = "Unexpected response";
                    return false;
                }

                return true;

                static bool ReadExact(System.IO.Stream s, int needed, int timeoutMs, out byte[] data)
                {
                    data = new byte[needed];
                    int read = 0;
                    var start = DateTime.UtcNow;
                    while (read < needed)
                    {
                        if ((DateTime.UtcNow - start).TotalMilliseconds > timeoutMs)
                            return false;
                        int n;
                        try { n = s.Read(data, read, needed - read); }
                        catch { return false; }
                        if (n <= 0) return false;
                        read += n;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                state = ModbusStateCode.UndefinedError;
                error = ex.Message;
                return false;
            }
        }


        // RTU: FC06 - Write Single Register
        private static async Task<(bool ok, ModbusStateCode state, string? error)> WriteSingleRegisterRtuAsync(
    ExportSettings s, byte slave, ushort addr, ushort value, int timeoutMs)
        {
            return await WriteHoldingRegistersRtuAsync(s, slave, addr, new[] { value }, timeoutMs);
        }

        private async void StartTunnel_Click(object sender, RoutedEventArgs e)
        {
            // Toggle behavior: if tunnel running -> stop, else start
            if (_tunnelListener != null)
            {
                await StopTunnelAsync();
                return;
            }

            // Parse local listen port

            // Parse remote endpoint
            var remoteHost = TunnelRemoteHostTextBox?.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(remoteHost))
            {
                MessageBox.Show("Remote host is required for the tunnel.", "Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!int.TryParse(TunnelRemotePortTextBox?.Text?.Trim(), out int remotePort) || remotePort <= 0 || remotePort > 65535)
            {
                MessageBox.Show("Remote port is invalid (1-65535).", "Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _tunnelCts = new CancellationTokenSource();
                _tunnelListener.Start();

                // Start accept loop: each incoming local client will be proxied to remoteHost:remotePort
                _tunnelTask = Task.Run(() => TunnelAcceptLoopProxyAsync(_tunnelListener, remoteHost, remotePort, _tunnelCts.Token));

                UpdateTunnelIndicator(true);
                TunnelRemoteHostTextBox.IsEnabled = false;
                TunnelRemotePortTextBox.IsEnabled = false;
            }
            catch (Exception ex)
            {
                UpdateTunnelIndicator(false);
                _ = ShowMessageAfterBusyAsync($"Failed to start tunnel: {ex.Message}", "Tunnel", MessageBoxButton.OK, MessageBoxImage.Error);
                try { _tunnelListener?.Stop(); } catch { }
                _tunnelListener = null;
                _tunnelCts?.Dispose();
                _tunnelCts = null;
            }
        }

        private async Task TunnelAcceptLoopProxyAsync(TcpListener listener, string remoteHost, int remotePort, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient? localClient = null;
                TcpClient? remoteClient = null;
                NetworkStream? localStream = null;
                NetworkStream? remoteStream = null;
                try
                {
                    localClient = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    localStream = localClient.GetStream();

                    // Connect to remote endpoint
                    remoteClient = new TcpClient();
                    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var connectTask = remoteClient.ConnectAsync(remoteHost, remotePort);
                    var linked = CancellationTokenSource.CreateLinkedTokenSource(token, connectCts.Token);
                    var t = await Task.WhenAny(connectTask, Task.Delay(-1, linked.Token)).ConfigureAwait(false);
                    if (!remoteClient.Connected)
                    {
                        // cannot connect -> close local client and continue
                        try { localClient.Close(); } catch { }
                        continue;
                    }
                    remoteStream = remoteClient.GetStream();

                    // Relay both directions
                    var t1 = RelayStreamAsync(localStream, remoteStream, token);
                    var t2 = RelayStreamAsync(remoteStream, localStream, token);

                    // Wait until one side closes
                    await Task.WhenAny(t1, t2).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception)
                {
                    if (token.IsCancellationRequested) break;
                    await Task.Delay(200, token).ContinueWith(_ => { });
                }
                finally
                {
                    try { localStream?.Close(); } catch { }
                    try { remoteStream?.Close(); } catch { }
                    try { localClient?.Close(); } catch { }
                    try { remoteClient?.Close(); } catch { }
                }
            }
        }

        private async Task RelayStreamAsync(NetworkStream src, NetworkStream dst, CancellationToken token)
        {
            var buffer = new byte[4096];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read = 0;
                    try
                    {
                        read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        break;
                    }

                    if (read <= 0) break;

                    try
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            catch { /* swallow */ }
        }

        private async Task StopTunnelAsync()
        {
            try
            {
                _tunnelCts?.Cancel();

                try
                {
                    _tunnelListener?.Stop();
                }
                catch { }

                // Wait for acceptance loop to finish
                if (_tunnelTask != null)
                {
                    try { await _tunnelTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                    _tunnelTask = null;
                }
            }
            finally
            {
                try
                {
                    lock (_tunnelLock)
                    {
                        if (_tunnelSerial != null)
                        {
                            try { _tunnelSerial.Close(); } catch { }
                            _tunnelSerial.Dispose();
                            _tunnelSerial = null;
                        }
                    }
                }
                catch { }

                _tunnelListener = null;
                _tunnelCts?.Dispose();
                _tunnelCts = null;

                // Update UI on UI thread and re-enable the input fields
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        UpdateTunnelIndicator(false);

                        // Re-enable tunnel input controls so user can edit them after stopping
                        if (TunnelRemoteHostTextBox != null) TunnelRemoteHostTextBox.IsEnabled = true;
                        if (TunnelRemotePortTextBox != null) TunnelRemotePortTextBox.IsEnabled = true;
                    });
                }
                catch
                {
                    // best-effort UI update, swallow exceptions
                }
            }
        }

        private async Task TunnelAcceptLoopAsync(TcpListener listener, SerialPort serial, CancellationToken token)
        {
            // Accept single client at a time. If a client disconnects, accept new one.
            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                NetworkStream? ns = null;
                try
                {
                    client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    ns = client.GetStream();

                    // Relay bidirectionally between ns and serial.BaseStream
                    var clientToSerial = RelayNetworkToSerialAsync(ns, serial, token);
                    var serialToClient = RelaySerialToNetworkAsync(serial, ns, token);

                    // Wait until either side faults or cancellation
                    await Task.WhenAny(clientToSerial, serialToClient);

                    // Ensure both tasks are cancelled
                    try { client.Close(); } catch { }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception)
                {
                    // swallow and continue accepting unless cancellation requested
                    if (token.IsCancellationRequested) break;
                    await Task.Delay(200, token).ContinueWith(_ => { });
                }
                finally
                {
                    if (ns != null) { try { ns.Close(); } catch { } }
                    if (client != null) { try { client.Close(); } catch { } }
                }
            }
        }

        private async Task RelayNetworkToSerialAsync(NetworkStream ns, SerialPort serial, CancellationToken token)
        {
            var buffer = new byte[2048];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read = 0;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    cts.CancelAfter(TimeSpan.FromSeconds(15)); // read timeout guard

                    try
                    {
                        read = await ns.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    if (read <= 0) break;

                    try
                    {
                        lock (_tunnelLock)
                        {
                            if (serial != null && serial.IsOpen)
                                serial.BaseStream.Write(buffer, 0, read);
                        }
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            catch { }
        }

        private async Task RelaySerialToNetworkAsync(SerialPort serial, NetworkStream ns, CancellationToken token)
        {
            var buffer = new byte[2048];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read = 0;
                    try
                    {
                        // SerialPort.BaseStream.ReadAsync will block until data or cancellation
                        read = await serial.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    if (read <= 0) break;

                    try
                    {
                        await ns.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
            catch { }
        }

        private void UpdateTunnelIndicator(bool active)
        {
            return;
        }


        private static bool TryProbeRtuOverTcp(string host, int port, byte slave, int timeoutMs, out string? diag)
        {
            diag = null;
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(host, port);
                if (!connectTask.Wait(timeoutMs))
                {
                    diag = "Connect timeout";
                    return false;
                }

                client.NoDelay = true;
                client.SendTimeout = timeoutMs;
                client.ReceiveTimeout = Math.Max(timeoutMs, 2000);

                using var stream = client.GetStream();

                // Build RTU FC03 request: [slave][0x03][addr_hi][addr_lo][qty_hi][qty_lo][crc_lo][crc_hi]
                byte[] req = new byte[8];
                req[0] = slave;
                req[1] = 0x03;
                req[2] = 0x00; // addr hi
                req[3] = 0x00; // addr lo
                req[4] = 0x00; // qty hi
                req[5] = 0x01; // qty lo
                ushort crc = Crc16Modbus(req, 0, 6);
                req[6] = (byte)(crc & 0xFF);
                req[7] = (byte)(crc >> 8);

                // send
                stream.Write(req, 0, req.Length);

                // read header [slave][0x03][byteCount]
                var hdr = new byte[3];
                int r = 0;
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (r < hdr.Length && DateTime.UtcNow < deadline)
                {
                    if (stream.DataAvailable)
                    {
                        int n = stream.Read(hdr, r, hdr.Length - r);
                        if (n <= 0) break;
                        r += n;
                    }
                    else
                    {
                        Thread.Sleep(20);
                    }
                }
                if (r < hdr.Length)
                {
                    diag = "No Tunnel header";
                    return false;
                }

                if (hdr[0] != slave)
                {
                    diag = $"Wrong slave (hdr[0]={hdr[0]})";
                    return false;
                }
                if (hdr[1] == 0x03)
                {
                    int byteCount = hdr[2];
                    var data = new byte[byteCount + 2];
                    r = 0;
                    deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    while (r < data.Length && DateTime.UtcNow < deadline)
                    {
                        if (stream.DataAvailable)
                        {
                            int n = stream.Read(data, r, data.Length - r);
                            if (n <= 0) break;
                            r += n;
                        }
                        else
                        {
                            Thread.Sleep(20);
                        }
                    }
                    if (r < data.Length)
                    {
                        diag = "Tunnel payload truncated";
                        return false;
                    }

                    // Verify CRC
                    // compose bytes to check: hdr[0..2] + data[0..byteCount-1]
                    var full = new byte[3 + byteCount];
                    Array.Copy(hdr, 0, full, 0, 3);
                    Array.Copy(data, 0, full, 3, byteCount);
                    ushort crcCalc = Crc16Modbus(full, 0, full.Length);
                    ushort crcResp = (ushort)(data[byteCount] | (data[byteCount + 1] << 8));
                    if (crcCalc != crcResp)
                    {
                        diag = "Tunnel CRC mismatch";
                        return false;
                    }

                    // Looks like valid RTU reply
                    return true;
                }
                else if ((hdr[1] & 0x80) != 0)
                {
                    // Modbus exception frame (RTU) - still counts as RTU
                    diag = $"Tunnel exception {hdr[2]}";
                    return true;
                }
                else
                {
                    diag = "Not Tunnel function";
                    return false;
                }
            }
            catch (Exception ex)
            {
                diag = ex.Message;
                return false;
            }
        }


        private async Task<(bool ok, List<ActivationZone> zones, string? error)>
    ReadZonesFromModbusRtuOverTcpWorkerAsync(string host, int port, byte unitId)
        {
            const ushort UNLOCK_REGISTER = 0x103F;
            const ushort UNLOCK_VALUE = 4562;

            var resultZones = new List<ActivationZone>();

            try
            {
                using var client = new TcpClient();
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;

                await client.ConnectAsync(host, port).ConfigureAwait(false);
                using NetworkStream stream = client.GetStream();

                if (!await RtuOverTcpWriteSingleAsync(stream, unitId, UNLOCK_REGISTER, UNLOCK_VALUE, 2000).ConfigureAwait(false))
                    return (false, resultZones, "Tunnel: unlock failed");

                await Task.Delay(500).ConfigureAwait(false);

                const int MAIN_MAX = 4;
                const int SUB_MAX = 5;
                const ushort FIRST_ZONE_BASE = (ushort)(MPC_BASE_ADDR + MPCv3RTV_WRITE_OFFSET); // 0x032C

                for (int mainZone = 1; mainZone <= MAIN_MAX; mainZone++)
                {
                    for (int subZone = 1; subZone <= SUB_MAX; subZone++)
                    {
                        if ((mainZone > 1 || subZone > 1) && subZone == 1)
                        {
                            if (!await RtuOverTcpWriteSingleAsync(stream, unitId, UNLOCK_REGISTER, UNLOCK_VALUE, 1500).ConfigureAwait(false))
                                continue;
                            await Task.Delay(300).ConfigureAwait(false);
                        }

                        int zoneIndex = ((mainZone - 1) * SUB_MAX) + (subZone - 1);
                        ushort zoneBase = (ushort)(FIRST_ZONE_BASE + zoneIndex * MPC_ZONE_STRIDE);

                        var (okRead, regs, errRead) = await RtuOverTcpReadHoldingAsync(stream, unitId, zoneBase, 10, 2500).ConfigureAwait(false);
                        if (!okRead || regs == null || regs.Length < 10)
                            continue;

                        if (regs.All(r => r == 0))
                            continue;

                        double latitude = WordsToFloatWS(regs[0], regs[1]);
                        double longitude = WordsToFloatWS(regs[2], regs[3]);
                        double heightMeters = regs[4] / 100.0;
                        double widthMeters = regs[6] / 100.0;
                        int azimuth = Math.Clamp((int)regs[8], 0, 359);

                        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || widthMeters <= 0 || heightMeters <= 0)
                            continue;

                        resultZones.Add(new ActivationZone
                        {
                            Name = $"Zone {mainZone}-{subZone}",
                            MainZone = mainZone - 1,
                            SubZone = subZone - 1,
                            Latitude = latitude,
                            Longitude = longitude,
                            Width = widthMeters,
                            Height = heightMeters,
                            Azimuth = azimuth
                        });
                    }
                }

                await RtuOverTcpWriteSingleAsync(stream, unitId, UNLOCK_REGISTER, 0, 1500).ConfigureAwait(false);

                return (true, resultZones, null);
            }
            catch (Exception ex)
            {
                return (false, resultZones, $"Tunnel read error: {ex.Message}");
            }
        }

        // --- Helper: build RTU FC03 request ---
        private static byte[] BuildReadHoldingRegistersRtu(byte slave, ushort startAddr, ushort count)
        {
            var req = new byte[8];
            req[0] = slave;
            req[1] = 0x03;
            req[2] = (byte)(startAddr >> 8);
            req[3] = (byte)(startAddr & 0xFF);
            req[4] = (byte)(count >> 8);
            req[5] = (byte)(count & 0xFF);
            ushort crc = Crc16Modbus(req, 0, 6);
            req[6] = (byte)(crc & 0xFF);
            req[7] = (byte)(crc >> 8);
            return req;
        }

        // --- Helper: validate CRC of full RTU response frame ---
        private static bool ValidateCrc(ReadOnlySpan<byte> frame)
        {
            if (frame.Length < 5) return false;
            ushort expected = Crc16Modbus(frame, 0, frame.Length - 2);
            ushort given = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
            return expected == given;
        }

        // --- Helper: extract 16-bit words from RTU FC03 response ---
        private static ushort[]? ExtractRegistersFromRtuResponse(ReadOnlySpan<byte> rtu)
        {
            // [0]=slave, [1]=func, [2]=byteCount, [...]=data, [crc_lo],[crc_hi]
            if (rtu.Length < 5 || rtu[1] != 0x03) return null;
            int byteCount = rtu[2];
            int dataBytes = byteCount;
            if (rtu.Length < (3 + dataBytes + 2)) return null;
            if (dataBytes % 2 != 0) return null;

            var regs = new ushort[dataBytes / 2];
            int idx = 3;
            for (int i = 0; i < regs.Length; i++)
            {
                regs[i] = (ushort)((rtu[idx] << 8) | rtu[idx + 1]);
                idx += 2;
            }
            return regs;
        }

        // --- Helper: raw RTU-over-TCP read (single FC03) ---
        private static async Task<(bool ok, ushort[]? regs, string? error)>
            RtuOverTcpReadHoldingAsync(NetworkStream stream, byte slave, ushort startAddr, ushort count, int timeoutMs)
        {
            try
            {
                var req = BuildReadHoldingRegistersRtu(slave, startAddr, count);
                await stream.WriteAsync(req, 0, req.Length).ConfigureAwait(false);

                // Read header first: slave + func + byteCount
                var header = new byte[3];
                int read = 0;
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (read < 3 && DateTime.UtcNow < deadline)
                {
                    if (stream.DataAvailable)
                    {
                        int n = await stream.ReadAsync(header, read, 3 - read).ConfigureAwait(false);
                        if (n <= 0) break;
                        read += n;
                    }
                    else
                        await Task.Delay(15).ConfigureAwait(false);
                }
                if (read < 3) return (false, null, "Timeout (header)");

                if (header[0] != slave)
                    return (false, null, "Wrong slave");
                if ((header[1] & 0x80) != 0)
                    return (false, null, $"Modbus exception {header[2]}");
                if (header[1] != 0x03)
                    return (false, null, "Wrong function");

                int byteCount = header[2];
                int totalPayload = byteCount + 2; // data + CRC
                var payload = new byte[totalPayload];
                read = 0;
                while (read < totalPayload && DateTime.UtcNow < deadline)
                {
                    if (stream.DataAvailable)
                    {
                        int n = await stream.ReadAsync(payload, read, totalPayload - read).ConfigureAwait(false);
                        if (n <= 0) break;
                        read += n;
                    }
                    else
                        await Task.Delay(15).ConfigureAwait(false);
                }
                if (read < totalPayload) return (false, null, "Timeout (payload)");

                // Build full frame to validate CRC
                var full = new byte[3 + totalPayload];
                Array.Copy(header, 0, full, 0, 3);
                Array.Copy(payload, 0, full, 3, totalPayload);

                if (!ValidateCrc(full))
                    return (false, null, "CRC failed");

                var regs = ExtractRegistersFromRtuResponse(full);
                if (regs == null) return (false, null, "Parse failed");

                if (regs.Length != count)
                {
                    // Allow partial (some devices pad) but report mismatch
                    return (true, regs, $"Quantity mismatch: {regs.Length} != {count}");
                }

                return (true, regs, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        // --- Helper: RTU-over-TCP single register write (FC06) ---
        private static async Task<bool> RtuOverTcpWriteSingleAsync(NetworkStream stream, byte slave, ushort addr, ushort value, int timeoutMs)
        {
            try
            {
                // Frame: [slave][0x06][addr_hi][addr_lo][val_hi][val_lo][crc_lo][crc_hi]
                var frame = new byte[8];
                frame[0] = slave;
                frame[1] = 0x06;
                frame[2] = (byte)(addr >> 8);
                frame[3] = (byte)(addr & 0xFF);
                frame[4] = (byte)(value >> 8);
                frame[5] = (byte)(value & 0xFF);
                ushort crc = Crc16Modbus(frame, 0, 6);
                frame[6] = (byte)(crc & 0xFF);
                frame[7] = (byte)(crc >> 8);

                await stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);

                // Expect exact echo
                var resp = new byte[8];
                int read = 0;
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (read < 8 && DateTime.UtcNow < deadline)
                {
                    if (stream.DataAvailable)
                    {
                        int n = await stream.ReadAsync(resp, read, 8 - read).ConfigureAwait(false);
                        if (n <= 0) break;
                        read += n;
                    }
                    else
                        await Task.Delay(10).ConfigureAwait(false);
                }
                if (read < 8) return false;

                if (!ValidateCrc(resp)) return false;
                if (resp[0] != slave || resp[1] != 0x06) return false;
                ushort echoAddr = (ushort)((resp[2] << 8) | resp[3]);
                ushort echoVal = (ushort)((resp[4] << 8) | resp[5]);
                return echoAddr == addr && echoVal == value;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<byte[]> ReadRtuResponseAsync(SerialPort sp, int timeoutMs)
        {
            using var cts = new CancellationTokenSource(timeoutMs);

            // 1) načti 2 bajty: slave + fn
            var head = new byte[2];
            await ReadExactAsync(sp, head, 0, 2, cts.Token);

            byte slave = head[0];
            byte fn = head[1];

            int remaining;
            if ((fn & 0x80) != 0)
            {
                // exception: [excCode][crcLo][crcHi] = 3
                remaining = 3;
            }
            else
            {
                // normální write response pro 0x06 nebo 0x10 je 6 bajtů zbytku
                // (addrHi addrLo qty/valueHi qty/valueLo crcLo crcHi) => 6
                remaining = 6;
            }

            var rest = new byte[remaining];
            await ReadExactAsync(sp, rest, 0, remaining, cts.Token);

            var resp = new byte[2 + remaining];
            Buffer.BlockCopy(head, 0, resp, 0, 2);
            Buffer.BlockCopy(rest, 0, resp, 2, remaining);
            return resp;
        }

        // replace ReadExactAsync with a safe polling version that doesn't cancel in-flight I/O
        private static async Task ReadExactAsync(SerialPort sp, byte[] buf, int off, int len, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds( // use ReadTimeout if set, else 3000
                sp.ReadTimeout > 0 ? sp.ReadTimeout : 3000);

            int read = 0;
            while (read < len)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

                int available;
                try { available = sp.BytesToRead; }
                catch (IOException ioex) when (IsOpAborted(ioex)) { throw new TimeoutException("Serial read aborted."); }
                catch { available = 0; }

                if (available > 0)
                {
                    int n;
                    try { n = sp.Read(buf, off + read, Math.Min(len - read, available)); }
                    catch (IOException ioex) when (IsOpAborted(ioex)) { throw new TimeoutException("Serial read aborted."); }
                    if (n <= 0) throw new TimeoutException("Serial read returned 0 bytes.");
                    read += n;
                    continue;
                }

                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Serial read timed out.");
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
            }
        }

        public static class ModbusRtuRead
        {
            public static async Task<byte[]> ReadWriteResponseAsync(SerialPort sp, int timeoutMs)
            {
                using var cts = new CancellationTokenSource(timeoutMs);

                // Načti 2 bajty: [slave][function]
                var head = new byte[2];
                await ModbusRtuHelpers.ReadExactAsync(sp, head, 0, 2, cts.Token).ConfigureAwait(false);

                byte fn = head[1];

                int remaining;
                if ((fn & 0x80) != 0)
                {
                    // Exception: [exceptionCode][crcLo][crcHi] = 3 bajty
                    remaining = 3;
                }
                else
                {
                    // Write response (0x06 nebo 0x10): zbytek je 6 bajtů
                    remaining = 6;
                }

                var rest = new byte[remaining];
                await ModbusRtuHelpers.ReadExactAsync(sp, rest, 0, remaining, cts.Token).ConfigureAwait(false);

                var full = new byte[2 + remaining];
                Buffer.BlockCopy(head, 0, full, 0, 2);
                Buffer.BlockCopy(rest, 0, full, 2, remaining);

                ModbusRtuHelpers.ValidateCrcOrThrow(full);
                return full;
            }
        }

        public static class ModbusRtuWrite
        {
            public static async Task WriteHoldingRegistersRtuAsync(
                SerialPort sp,
                byte slaveId,
                ushort startRegister,
                ushort[] values,
                int timeoutMs,
                Action<string>? log = null)
            {
                if (values == null) throw new ArgumentNullException(nameof(values));
                if (values.Length == 0) throw new ArgumentException("values is empty.", nameof(values));

                byte[] req;
                byte expectedFn;

                if (values.Length == 1)
                {
                    req = BuildWriteSingleRegister(slaveId, startRegister, values[0]);
                    expectedFn = 0x06;
                }
                else
                {
                    req = BuildWriteMultipleRegisters(slaveId, startRegister, values);
                    expectedFn = 0x10;
                }

                if (log != null) log("RTU TX: " + ModbusRtuHelpers.ToHex(req, req.Length));

                await sp.BaseStream.WriteAsync(req, 0, req.Length).ConfigureAwait(false);
                await sp.BaseStream.FlushAsync().ConfigureAwait(false);

                var resp = await ModbusRtuRead.ReadWriteResponseAsync(sp, timeoutMs).ConfigureAwait(false);
                if (log != null) log("RTU RX: " + ModbusRtuHelpers.ToHex(resp, resp.Length));

                byte fn = resp[1];

                if ((fn & 0x80) != 0)
                {
                    byte exc = resp[2];
                    throw new InvalidOperationException(
                        "Modbus exception. Slave=" + slaveId.ToString("X2") +
                        " Fn=" + expectedFn.ToString("X2") +
                        " Code=" + exc.ToString("X2") +
                        " (" + ModbusRtuHelpers.ExplainExceptionCode(exc) + ")");
                }

                if (fn != expectedFn)
                {
                    throw new InvalidOperationException(
                        "Unexpected function in response. Got=" + fn.ToString("X2") +
                        " Expected=" + expectedFn.ToString("X2"));
                }

                if (resp[0] != slaveId)
                {
                    throw new InvalidOperationException(
                        "Unexpected slave in response. Got=" + resp[0].ToString("X2") +
                        " Expected=" + slaveId.ToString("X2"));
                }
            }

            private static byte[] BuildWriteSingleRegister(byte slave, ushort addr, ushort value)
            {
                // [slave][0x06][addr hi][addr lo][val hi][val lo][crc lo][crc hi]
                var buf = new byte[8];
                int i = 0;

                buf[i++] = slave;
                buf[i++] = 0x06;
                buf[i++] = (byte)(addr >> 8);
                buf[i++] = (byte)(addr & 0xFF);
                buf[i++] = (byte)(value >> 8);
                buf[i++] = (byte)(value & 0xFF);

                ushort crc = ModbusRtuHelpers.Crc16Modbus(buf, 0, 6);
                buf[i++] = (byte)(crc & 0xFF);
                buf[i++] = (byte)((crc >> 8) & 0xFF);

                return buf;
            }

            private static byte[] BuildWriteMultipleRegisters(byte slave, ushort addr, ushort[] values)
            {
                // [slave][0x10][addr hi][addr lo][qty hi][qty lo][bytecount][data..][crc lo][crc hi]
                int qty = values.Length;
                int byteCount = qty * 2;

                var buf = new byte[7 + byteCount + 2];
                int i = 0;

                buf[i++] = slave;
                buf[i++] = 0x10;
                buf[i++] = (byte)(addr >> 8);
                buf[i++] = (byte)(addr & 0xFF);
                buf[i++] = (byte)(qty >> 8);
                buf[i++] = (byte)(qty & 0xFF);
                buf[i++] = (byte)byteCount;

                for (int k = 0; k < qty; k++)
                {
                    ushort v = values[k];
                    buf[i++] = (byte)(v >> 8);
                    buf[i++] = (byte)(v & 0xFF);
                }

                ushort crc = ModbusRtuHelpers.Crc16Modbus(buf, 0, i);
                buf[i++] = (byte)(crc & 0xFF);
                buf[i++] = (byte)((crc >> 8) & 0xFF);

                return buf;
            }
        }

        private static async Task<(bool ok, string? msg, ExportSettings? tuned)> RtuAdaptiveProbeAsync(ExportSettings s, byte slave, int timeoutMs)
        {
            // Force Modbus ASCII framing: 8N1
            var tuned = ExportSettings.CloneFrom(s);
            tuned.SerialHandshake = "None";
            tuned.SerialParity = "None";
            tuned.SerialStopBits = "1";
            tuned.SerialDataBits = 8;
            return (true, "Using Modbus ASCII 8N1 on serial.", tuned);
        }

        private static async Task<(bool ok, ushort usedBase)> AsciiProbeFirstZoneBaseAsync(ExportSettings s, byte slave, ushort baseAddr, int timeoutMs)
        {
            var candidates = new List<ushort> { baseAddr };
            if (baseAddr > 0) candidates.Add((ushort)(baseAddr - 1));
            if (baseAddr < ushort.MaxValue) candidates.Add((ushort)(baseAddr + 1));

            foreach (var a in candidates)
            {
                var (ok, st, data, _) = await ReadHoldingRegistersRtuAsync(s, slave, a, 10, timeoutMs);
                if (ok && st == ModbusStateCode.Success && data != null && data.Length >= 10)
                    return (true, a);
            }
            return (false, baseAddr);
        }

        private static byte ComputeLrc(ReadOnlySpan<byte> bytes)
        {
            int sum = 0;
            for (int i = 0; i < bytes.Length; i++) sum += bytes[i];
            sum = ((sum ^ 0xFF) + 1) & 0xFF;
            return (byte)sum;
        }

        private static string ToHex2(byte b) => b.ToString("X2", CultureInfo.InvariantCulture);

        private static byte[] HexAsciiToBytes(ReadOnlySpan<char> hex)
        {
            if (hex.Length % 2 != 0) throw new FormatException("Odd ASCII hex length.");
            var buf = new byte[hex.Length / 2];
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = byte.Parse(new string(new[] { hex[i * 2], hex[i * 2 + 1] }), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            return buf;
        }

        // replace ReadAsciiFrameAsync with a non-canceling, polling implementation
        private static async Task<string> ReadAsciiFrameAsync(SerialPort sp, int timeoutMs)
        {
            // Manual deadline, no cancellation of in-flight I/O (prevents ERROR_OPERATION_ABORTED 995)
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1, timeoutMs));
            var sb = new StringBuilder(256);
            var tmp = new byte[128];
            bool started = false;

            // Ensure ASCII framing helps
            try { sp.NewLine = "\r\n"; } catch { /* best effort */ }

            while (DateTime.UtcNow < deadline)
            {
                int available;
                try { available = sp.BytesToRead; }
                catch (IOException ioex) when (IsOpAborted(ioex)) { break; }
                catch { available = 0; }

                if (available > 0)
                {
                    int toRead = Math.Min(available, tmp.Length);
                    int n = 0;
                    try { n = sp.Read(tmp, 0, toRead); }
                    catch (IOException ioex) when (IsOpAborted(ioex)) { break; }
                    catch { n = 0; }

                    if (n > 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            char ch = (char)tmp[i];

                            if (!started)
                            {
                                if (ch == ':')
                                {
                                    started = true;
                                    sb.Clear();
                                }
                                continue;
                            }

                            sb.Append(ch);

                            int len = sb.Length;
                            if (len >= 2 && sb[len - 2] == '\r' && sb[len - 1] == '\n')
                            {
                                sb.Length -= 2; // strip CRLF
                                return sb.ToString();
                            }
                        }
                    }
                }

                // Small wait to avoid tight loop
                await Task.Delay(5).ConfigureAwait(false);
            }

            throw new TimeoutException("Modbus ASCII read timed out.");
        }

        private static string BuildAsciiRequest(byte slave, byte function, ReadOnlySpan<byte> pdu)
        {
            // Body: [slave][function][pdu...] then LRC over all above bytes.
            var raw = new byte[2 + pdu.Length];
            raw[0] = slave;
            raw[1] = function;
            pdu.CopyTo(raw.AsSpan(2));
            byte lrc = ComputeLrc(raw);

            // ASCII: ':' + hex(raw) + hex(lrc) + "\r\n"
            var sb = new StringBuilder(1 + (raw.Length + 1) * 2 + 2);
            sb.Append(':');
            for (int i = 0; i < raw.Length; i++) sb.Append(ToHex2(raw[i]));
            sb.Append(ToHex2(lrc));
            sb.Append("\r\n");
            return sb.ToString();
        }

        private static string BuildAsciiReadHolding(byte slave, ushort addr, ushort qty)
        {
            // FC03 PDU: addr_hi, addr_lo, qty_hi, qty_lo
            Span<byte> pdu = stackalloc byte[4];
            pdu[0] = (byte)(addr >> 8);
            pdu[1] = (byte)(addr & 0xFF);
            pdu[2] = (byte)(qty >> 8);
            pdu[3] = (byte)(qty & 0xFF);
            return BuildAsciiRequest(slave, 0x03, pdu);
        }

        private static string BuildAsciiWriteSingle(byte slave, ushort addr, ushort val)
        {
            // FC06 PDU: addr_hi, addr_lo, val_hi, val_lo
            Span<byte> pdu = stackalloc byte[4];
            pdu[0] = (byte)(addr >> 8);
            pdu[1] = (byte)(addr & 0xFF);
            pdu[2] = (byte)(val >> 8);
            pdu[3] = (byte)(val & 0xFF);
            return BuildAsciiRequest(slave, 0x06, pdu);
        }

        private static string BuildAsciiWriteMultiple(byte slave, ushort startAddr, ushort[] regs)
        {
            // FC16 PDU: startAddr(2), qty(2), byteCount(1), data(2*qty)
            int qty = regs.Length;
            var pdu = new byte[5 + qty * 2];
            pdu[0] = (byte)(startAddr >> 8);
            pdu[1] = (byte)(startAddr & 0xFF);
            pdu[2] = (byte)(qty >> 8);
            pdu[3] = (byte)(qty & 0xFF);
            pdu[4] = (byte)(qty * 2);
            int i = 5;
            for (int k = 0; k < qty; k++)
            {
                ushort r = regs[k];
                pdu[i++] = (byte)(r >> 8);
                pdu[i++] = (byte)(r & 0xFF);
            }
            return BuildAsciiRequest(slave, 0x10, pdu);
        }

        private static (bool ok, byte slave, byte func, byte[] payload, string? error) ParseAsciiResponse(string frame)
        {
            // frame: hex of [slave][func][payload...] + LRC at the end (2 hex chars)
            if (frame.Length < 2 + 2 + 2) return (false, 0, 0, Array.Empty<byte>(), "Too short");

            var bytes = HexAsciiToBytes(frame.AsSpan());
            if (bytes.Length < 3) return (false, 0, 0, Array.Empty<byte>(), "Too short bin");

            byte slave = bytes[0];
            byte func = bytes[1];

            // Split payload and LRC
            if (bytes.Length < 3) return (false, slave, func, Array.Empty<byte>(), "No LRC");
            byte lrcGiven = bytes[^1];
            var body = bytes.AsSpan(0, bytes.Length - 1);
            byte lrcCalc = ComputeLrc(body);
            if (lrcCalc != lrcGiven) return (false, slave, func, Array.Empty<byte>(), "Bad LRC");

            var payload = bytes.Skip(2).Take(bytes.Length - 3).ToArray();
            return (true, slave, func, payload, null);
        }

        
    }
}