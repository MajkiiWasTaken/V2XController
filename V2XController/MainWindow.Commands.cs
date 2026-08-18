using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;

namespace V2XController
{
    // UI commands, connection controls, SRV controls and reprojection helpers
    public partial class MainWindow
    {
        private void RefreshMap_Click(object sender, RoutedEventArgs e)
        {
            RefreshMap();
        }

        /// <summary>
        /// Handler for export button, exports map to PNG.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ExportMap_Click(object sender, RoutedEventArgs e)
        {
            ExportCanvasToPng(TileCanvas, "map_export.png");
            Console.WriteLine("Map exported into: map_export.png");
        }

        /// <summary>
        /// Handler for connect button, connects to a given COM port with given baudrate.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected)
            {
                MessageBox.Show("Connection is already open.");
                return;
            }

            try
            {
                _connectionCts?.Cancel();
                _connectionCts?.Dispose();
                _connectionCts = new CancellationTokenSource();

                if (_connectionType == ConnectionType.Serial)
                {
                    if (ComPortsComboBox.SelectedItem is not ComPortInfo selectedPort)
                    {
                        MessageBox.Show("Select a COM port.");
                        return;
                    }

                    if (!int.TryParse(
                            BaudrateTB.Text.Trim(),
                            out int baudRate) ||
                        baudRate <= 0)
                    {
                        MessageBox.Show("Enter a valid baudrate.");
                        return;
                    }

                    await StartSerialConnectionAsync(
                        selectedPort.PortName,
                        baudRate,
                        _connectionCts.Token);

                    Console.WriteLine(
                        $"[CONNECT] Serial {selectedPort.PortName}, {baudRate} baud.");
                }
                else
                {
                    string host = EthernetAddressTB.Text.Trim();

                    if (string.IsNullOrWhiteSpace(host))
                    {
                        MessageBox.Show("Enter an IP address or hostname.");
                        return;
                    }

                    if (!int.TryParse(
                            EthernetPortTB.Text.Trim(),
                            out int tcpPort) ||
                        tcpPort is < 1 or > 65535)
                    {
                        MessageBox.Show("Enter a valid TCP port from 1 to 65535.");
                        return;
                    }

                    await StartEthernetConnectionAsync(
                        host,
                        tcpPort,
                        _connectionCts.Token);

                    Console.WriteLine(
                        $"[CONNECT] Ethernet {host}:{tcpPort}.");
                }

                _isConnected = true;

                StartTimeshiftSession();
                UpdateUiEnabledState();

                string connectionName = GetConnectionDisplayName();

                MessageBox.Show($"Connected to {connectionName}.");
            }
            catch (Exception ex)
            {
                DisconnectTransport();

                _isConnected = false;
                UpdateUiEnabledState();

                Console.WriteLine($"[CONNECT ERR] {ex}");
                MessageBox.Show($"Failed to connect:\n{ex.Message}");
            }
        }

        private void DisconnectTransport()
        {
            _connectionCts?.Cancel();
            _connectionCts?.Dispose();
            _connectionCts = null;

            try
            {
                if (serialPort != null)
                {
                    if (serialPort.IsOpen)
                        serialPort.Close();

                    serialPort.Dispose();
                    serialPort = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SERIAL CLOSE ERR] {ex.Message}");
            }

            try
            {
                _tcpReader?.Dispose();
                _tcpReader = null;

                _tcpWriter?.Dispose();
                _tcpWriter = null;

                _tcpStream?.Dispose();
                _tcpStream = null;

                _tcpClient?.Dispose();
                _tcpClient = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TCP CLOSE ERR] {ex.Message}");
            }
        }

        /// <summary>
        /// Handler for disconnect button, disconnects from com port.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Determine whether there is anything to save
                bool hasManualRecording = recordedManualCamMessages != null && recordedManualCamMessages.Count > 0;
                bool hasLiveBuffer = recordedCamMessages != null && recordedCamMessages.Count > 0;
                bool promptNeeded = isRecording || hasManualRecording || hasLiveBuffer;

                if (!promptNeeded)
                {
                    // nothing to save — stop timeshift silently and disconnect
                    if (_timeshiftEnabled)
                        StopTimeshiftSession();

                    // proceed with disconnect
                    try
                    {
                        DisconnectTransport();

                        _isConnected = false;
                        StopSrvAutoTimer();

                        UpdateUiEnabledState();

                        Console.WriteLine("[DISCONNECT] Connection closed.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DISCONNECT] Error while closing port: " + ex.Message);
                        MessageBox.Show("Error while closing port: " + ex.Message, "Disconnect", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    ClearLiveVehiclesAndRsu();
                    StopSrvAutoTimer();
                    _isConnected = false;
                    UpdateUiEnabledState();
                    serialPort?.Dispose();
                    serialPort = null;
                    recordedCamMessages.Clear();
                    recordedSrvMessages.Clear();
                    _terminalBuffer.Clear();
                    Console.WriteLine($"[DISCONNECT] User disconnected from selected serial port.");
                    MessageBox.Show("Disconnected from serial port.");

                    return;
                }

                if (!_savedRecording)
                {
                    // There are unsaved items or active recording — ask the user
                    var messageBuilder = new StringBuilder("Recording or buffered data present. Do you want to stop and save before disconnecting?\n\n");
                    if (isRecording) messageBuilder.AppendLine("- Manual recording is active");
                    if (hasManualRecording) messageBuilder.AppendLine($"- {recordedManualCamMessages?.Count ?? 0} manual CAM message(s) to save");
                    if (hasLiveBuffer) messageBuilder.AppendLine($"- {recordedCamMessages?.Count ?? 0} live CAM message(s) in RS485 buffer");
                    var result = MessageBox.Show(messageBuilder.ToString(), "Recording active", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel)
                    {
                        // User aborted disconnect
                        return;
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        // Stop & save manual recording (StopRecording shows save dialog)
                        if (isRecording)
                            StopRecording();

                        // Save manual buffer if present and not recorded via StopRecording
                        if (hasManualRecording && !isRecording)
                        {
                            var dlgManual = new Microsoft.Win32.SaveFileDialog
                            {
                                FileName = DateTime.Now.ToString("yyyy-MM-dd_HH_mm", CultureInfo.InvariantCulture) + ".camrec",
                                DefaultExt = ".camrec",
                                Filter = "CAM Recording (*.camrec)|*.camrec|All files (*.*)|*.*",
                                Title = "Save manual CAM recording"
                            };
                            if (dlgManual.ShowDialog() == true)
                            {
                                WriteCamrecWithCenter(dlgManual.FileName, recordedManualCamMessages ?? new List<string>());
                                MessageBox.Show("Manual CAM recording saved to:\n" + dlgManual.FileName, "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                                recordedManualCamMessages?.Clear();
                            }
                        }

                        // Save live RS485 buffer if present
                        if (hasLiveBuffer)
                        {
                            SaveLiveCamBuffer();
                        }

                        // Stop timeshift after saving
                        if (_timeshiftEnabled) StopTimeshiftSession();
                    }
                    else // No -> stop and discard recordings/buffers
                    {
                        if (isRecording)
                        {
                            isRecording = false;
                            recordedManualCamMessages?.Clear();
                        }

                        if (hasManualRecording)
                            recordedManualCamMessages?.Clear();
                        if (hasLiveBuffer)
                            recordedCamMessages?.Clear();

                        if (_timeshiftEnabled) StopTimeshiftSession();
                    }
                }

                // proceed with disconnect
                try
                {
                    DisconnectTransport();

                    _isConnected = false;
                    StopSrvAutoTimer();

                    UpdateUiEnabledState();

                    Console.WriteLine("[DISCONNECT] Connection closed.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERR] Error while closing port: " + ex.Message);
                    MessageBox.Show("Error while closing port: " + ex.Message, "Disconnect", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                ClearLiveVehiclesAndRsu();
                StopSrvAutoTimer();
                _isConnected = false;

                UpdateUiEnabledState();
                Console.WriteLine($"[DISCONNECT] User disconnected from selected serial port.");
                MessageBox.Show("Disconnected from serial port.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERR] Error disconnecting: " + ex.Message);
                MessageBox.Show("Error disconnecting: " + ex.Message);
            }
        }

        /// <summary>
        /// Clears all live vehicles, drawn trams, RSU markers, and the radius circle from the canvas.
        /// Called on disconnect to leave the map clean for the next session.
        /// </summary>
        private void ClearLiveVehiclesAndRsu()
        {
            // 1. Cancel all pending trail-cleanup tokens
            foreach (var kv in vehicleTrailCleanupTokens.ToList())
            {
                try { kv.Value.Cancel(); kv.Value.Dispose(); } catch { }
            }
            vehicleTrailCleanupTokens.Clear();

            // 2. Remove all live CAM / SRV vehicles
            foreach (var kv in activeVehicles.ToList())
                RemoveVehicleCompletely(kv.Key, kv.Value);
            activeVehicles.Clear();

            // 3. Remove all drawn (manual) trams
            for (int i = 0; i < drawnTrams.Length; i++)
            {
                if (drawnTrams[i] != null)
                    RemoveDrawnTramCompletely(i, drawnTrams[i]);
            }

            // 4. Remove any RSU-tagged canvas elements created by the Protobuf SRV path
            //    (these are added directly to TileCanvas without going through activeVehicles)
            var rsuEllipses = TileCanvas.Children.OfType<Ellipse>()
                .Where(el => el.Tag is string t && t.StartsWith("RSU", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var el in rsuEllipses) TileCanvas.Children.Remove(el);

            var rsuLabels = TileCanvas.Children.OfType<TextBlock>()
                .Where(tb => tb.Tag is string t && t.StartsWith("RSU", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var tb in rsuLabels) TileCanvas.Children.Remove(tb);

            // 5. Remove the radius circle and reset SRV position state
            if (radiusEllipse != null)
            {
                TileCanvas.Children.Remove(radiusEllipse);
                radiusEllipse = null;
            }
            srvLatitude = null;
            srvLongitude = null;

            // 6. Clear supporting state so the next session starts fresh
            TramTable.Clear();
            vehicleColorMap.Clear();
            _lastLatLon.Clear();
            _lastHeadingLive.Clear();
            _lastLiveAccuracyById.Clear();
            _liveAccuracyTextById.Clear();
            _vehicleActiveZones.Clear();
            _vehicleZoneValidEntry.Clear();

            Console.WriteLine("[DISCONNECT] RSU and all vehicle visuals cleared.");
        }

        /// <summary>
        /// Handler for save to xml button, saves map drawings to an xml file.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SaveToXML_Click(object? sender, RoutedEventArgs? e)
        {
            // consider activation zones and railways too
            bool hasZones = activationZones.Count > 0 || (ActivationZonesCollection?.Count > 0);
            bool hasPolylines = _polylineGeoPoints.Count > 0;

            bool hasAnything =
                hasZones ||
                points.Count > 0 ||
                mapRectangles.Count > 0 ||
                connectionLine.Points.Count > 0 ||
                hasPolylines;

            if (!hasAnything)
            {
                var confirm = MessageBox.Show(
                    "Drawing is empty. Do you still want to save?",
                    "Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "map_rectangles",
                DefaultExt = ".xml",
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "Save map into XML"
            };

            bool? result = dlg.ShowDialog();
            if (result != true) return;

            string fileNameToSave = dlg.FileName;
            loadedFileName = fileNameToSave;

            SaveXML(fileNameToSave);
            MessageBox.Show("Data saved into file:\n" + fileNameToSave, "Data saved.", MessageBoxButton.OK, MessageBoxImage.Information);

            isDirty = false;
        }

        /// <summary>
        /// Handler for load from xml button, loads drawings from an xml file.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LoadFromXML_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                DefaultExt = ".xml",
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "Select an XML file to load onto the map"
            };

            bool? result = dlg.ShowDialog();

            if (result == true)
            {
                LoadXML(dlg.FileName);
                MessageBox.Show("Data loaded from file:\n" + dlg.FileName, "Data loaded", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Handler for play movement button, starts replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PlayMovement_Click(object sender, RoutedEventArgs e)
        {
            if (_keyframes == null || _keyframes.Count == 0)
                BuildPlaybackKeyframes();

            if (_keyframes == null || _keyframes.Count == 0)
            {
                MessageBox.Show("No CAM frames to play.", "Playback", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (isPlaying) return;

            PurgeLiveCamVehiclesForPlayback();

            // Use ReplaySlider for classic replay
            ReplaySlider.Minimum = 0;
            ReplaySlider.Maximum = Math.Max(0, _keyframes.Count - 1);
            ReplaySlider.TickFrequency = 1;
            ReplaySlider.SmallChange = 1;
            ReplaySlider.LargeChange = Math.Max(1, _keyframes.Count / 20);
            ReplaySlider.IsSnapToTickEnabled = true;
            ReplaySlider.Value = _playbackIndex;

            var t0 = _keyframes[_playbackIndex];
            playbackElapsedTime = t0;
            RedrawPlaybackToTime(t0);
            UpdateReplayTimerLabel();

            SendPlaybackCamForTime(t0);

            _isPlaybackSessionActive = true;
            isPlaying = true;
            playbackStartTime = DateTime.Now - t0;
            if (playbackTimer == null)
            {
                playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                playbackTimer.Tick += PlaybackTimer_Tick;
            }
            playbackTimer.Start();

            UpdateUiEnabledState();
        }

        /// <summary>
        /// Handler for record movement button, records replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnRecordMovement_Click(object sender, RoutedEventArgs e)
        {
            // removing all active vehicles
            foreach (var vehicleId in activeVehicles.Keys.ToList())
            {
                if (activeVehicles.TryGetValue(vehicleId, out var vehicle))
                    RemoveVehicleCompletely(vehicleId, vehicle);
            }
            activeVehicles.Clear();

            // removing of all drawn trams
            for (int i = 0; i < drawnTrams.Length; i++)
            {
                if (drawnTrams[i] != null)
                    RemoveDrawnTramCompletely(i, drawnTrams[i]);
                drawnTrams[i] = null;
                drawnTramTrailPoints[i].Clear();
                if (drawnTramTrails[i] != null)
                {
                    TileCanvas.Children.Remove(drawnTramTrails[i]);
                    drawnTramTrails[i] = null;
                }
            }

            // Vymazání tabulky tramvají
            TramTable.Clear();

            StartRecording();
        }

        /// <summary>
        /// Handler for stop recording button, stops recording.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnStopRecording_Click(object sender, RoutedEventArgs e)
        {
            StopRecording();
        }

        /// <summary>
        /// Handler for map controls button, shows map controls.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MapControls_Click(object sender, RoutedEventArgs e)
        {

            MessageBox.Show(ControlsMessage, "Map controls");

        }

        /// <summary>
        /// Handler for baudrate textbox, saves baudrate for future connection.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BaudrateTB_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = BaudrateTB?.Text?.Trim() ?? string.Empty;

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int baud) && baud > 0)
            {
                try
                {
                    // Apply to the current serial port only when it's closed (safe change)
                    if (serialPort != null && !serialPort.IsOpen)
                    {
                        serialPort.BaudRate = baud;
                    }
                    // If connected, UI prevents editing; no action needed here.
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to apply baudrate: {ex.Message}", "Baudrate", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            // If invalid input, do nothing; Connect_Click will still validate and prompt.
        }

        /// <summary>
        /// Resets all tram trails if trams are live and if the position on the map changes.
        /// </summary>
        private void ResetAllTramTrails()
        {
            // Active vehicles (RS485/live)
            // Remove any polyline tagged as tram trail and clear dots + buffer
            var allTrailLines = TileCanvas.Children
                .OfType<Polyline>()
                .Where(pl => pl.Tag is string s && s.StartsWith("tram_trail", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var l in allTrailLines)
                TileCanvas.Children.Remove(l);

            foreach (var kv in activeVehicles.ToList())
            {
                var v = kv.Value;
                if (v.TrailDots != null)
                {
                    foreach (var dot in v.TrailDots.ToList())
                        TileCanvas.Children.Remove(dot);
                    v.TrailDots.Clear();
                }
                // Clear canvas-based history so new trail starts fresh with next CAMs
                v.MovementFrames?.Clear();
            }

            // Drawn trams (manual X/C)
            for (int i = 0; i < drawnTrams.Length; i++)
            {
                if (drawnTramTrails[i] != null)
                {
                    TileCanvas.Children.Remove(drawnTramTrails[i]);
                    drawnTramTrails[i] = null;
                }
                drawnTramTrailPoints[i].Clear();
                drawnTramTrailGeoPoints[i].Clear();

                var tram = drawnTrams[i];
                if (tram?.TrailDots != null)
                {
                    foreach (var dot in tram.TrailDots.ToList())
                        TileCanvas.Children.Remove(dot);
                    tram.TrailDots.Clear();
                }
                tram?.MovementFrames?.Clear();
            }
        }

        /// <summary>
        /// Redraws vehicle trails.
        /// </summary>
        private void RedrawVehicleTrails()
        {
            // Remove any existing tram trail polylines (both new/old tag variants)
            var tramPolylines = TileCanvas.Children
                .OfType<Polyline>()
                .Where(pl => pl.Tag is string s && s.StartsWith("tram_trail", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var l in tramPolylines)
                TileCanvas.Children.Remove(l);

            foreach (var vehicle in activeVehicles.Values)
            {
                if (!TileCanvas.Children.Contains(vehicle.Ellipse))
                    TileCanvas.Children.Add(vehicle.Ellipse);
                if (!TileCanvas.Children.Contains(vehicle.Text))
                    TileCanvas.Children.Add(vehicle.Text);

                Panel.SetZIndex(vehicle.Ellipse, 1000);
                Panel.SetZIndex(vehicle.Text, 1000);

                // Only rebuild if we decide to redraw history (normally we reset trails after pan/zoom/refresh)
                if (vehicle.MovementFrames != null && vehicle.MovementFrames.Count > 1)
                {
                    var polyline = new Polyline
                    {
                        Stroke = vehicle.Ellipse?.Fill,
                        StrokeThickness = 2,
                        Tag = $"tram_trail_{vehicle.Label}"
                    };

                    foreach (var mf in vehicle.MovementFrames)
                        polyline.Points.Add(mf.Position);

                    TileCanvas.Children.Add(polyline);
                    Panel.SetZIndex(polyline, 999);
                }
            }
        }

        /// <summary>
        /// Brings all overlays to front safely.
        /// </summary>
        public async Task BringAllOverlaysToFrontSafeAsync()
        {
            if (TileCanvas == null) return;

            await Dispatcher.InvokeAsync(() =>
            {
                // Ensure rectangles are above map tiles
                if (mapRectangles != null)
                {
                    foreach (var mr in mapRectangles.Where(m => m?.Shape != null))
                    {
                        var shape = mr.Shape;
                        if (shape != null && TileCanvas.Children.Contains(shape))
                            Panel.SetZIndex(shape, 100);
                    }
                }

                // Ensure points are above rectangles
                if (points != null)
                {
                    foreach (var pt in points.Where(p => p != null))
                    {
                        if (pt.Ellipse != null && TileCanvas.Children.Contains(pt.Ellipse))
                            Panel.SetZIndex(pt.Ellipse, 200);
                        if (pt.Text != null && TileCanvas.Children.Contains(pt.Text))
                            Panel.SetZIndex(pt.Text, 201);
                    }
                }

                // Connection line above rectangles, below points
                if (connectionLine != null && TileCanvas.Children.Contains(connectionLine))
                    Panel.SetZIndex(connectionLine, 150);

                // Vehicle overlays
                if (activeVehicles != null)
                {
                    foreach (var vehicle in activeVehicles.Values.Where(v => v != null))
                    {
                        if (vehicle.Ellipse != null && TileCanvas.Children.Contains(vehicle.Ellipse))
                            Panel.SetZIndex(vehicle.Ellipse, 1000);
                        if (vehicle.Text != null && TileCanvas.Children.Contains(vehicle.Text))
                            Panel.SetZIndex(vehicle.Text, 1000);
                    }
                }

                // Activation zones
                if (activationZones != null)
                {
                    foreach (var zone in activationZones.Values.Where(z => z?.Rectangle != null))
                    {
                        var r = zone.Rectangle;
                        if (r != null && TileCanvas.Children.Contains(r))
                            Panel.SetZIndex(r, 100);
                    }
                }

                // Drawn trams
                if (drawnTrams != null && drawnTramTrails != null)
                {
                    for (int idx = 0; idx < drawnTrams.Length; idx++)
                    {
                        var tram = drawnTrams[idx];
                        if (tram == null) continue;

                        if (tram.Ellipse != null && TileCanvas.Children.Contains(tram.Ellipse))
                            Panel.SetZIndex(tram.Ellipse, 1000);
                        if (tram.Text != null && TileCanvas.Children.Contains(tram.Text))
                            Panel.SetZIndex(tram.Text, 1000);
                        if (idx >= 0 && idx < drawnTramTrails.Length && drawnTramTrails[idx] != null &&
                            TileCanvas.Children.Contains(drawnTramTrails[idx]))
                            Panel.SetZIndex(drawnTramTrails[idx], 999);

                        if (tram.TrailDots != null)
                        {
                            foreach (var dot in tram.TrailDots.Where(d => d != null))
                                if (TileCanvas.Children.Contains(dot))
                                    Panel.SetZIndex(dot, 1001);
                        }
                    }
                }

                // Tram trails with legacy tag
                foreach (var poly in TileCanvas.Children.OfType<Polyline>().Where(l => (l.Tag as string) == "tram_trail"))
                    Panel.SetZIndex(poly, 999);

                // Dimension textblock always on top
                if (dimensionTextBlock != null && TileCanvas.Children.Contains(dimensionTextBlock))
                    Panel.SetZIndex(dimensionTextBlock, int.MaxValue);
            });
        }

        /// <summary>
        /// Logic for checked radius box.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CheckBox_Checked(object sender, RoutedEventArgs e)
        {
            RadiusComboBox.IsEnabled = true;
            DrawRadiusCircle();
        }

        /// <summary>
        /// Logic for unchecked radius box.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            RadiusComboBox.IsEnabled = false;

            if (radiusEllipse != null)
            {
                TileCanvas.Children.Remove(radiusEllipse);
            }
        }

        /// <summary>
        /// Logic for radius combo box selection.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RadiusComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CircleCheckBox.IsChecked == true)
                DrawRadiusCircle();

        }

        /// <summary>
        /// Logic for clearing all objects.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine($"[CLEAR] Clear all objects requested");

            var result = MessageBox.Show("Are you sure you want to delete everything?",
                                         "Confirmation",
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                Console.WriteLine($"[CLEAR] Cancelled by user\n");
                return;
            }

            Console.WriteLine($"[CLEAR] Clearing all objects...");

            ClearAll();
        }

        /// <summary>
        /// Clears polyline direction arrows and their wings
        /// </summary>
        private void ClearPolylineDirectionArrows()
        {
            try
            {
                // Work on a snapshot to avoid collection-modification issues
                var allEntries = _polylineDirectionArrows.ToList();

                foreach (var kv in allEntries)
                {
                    var segments = kv.Value;
                    if (segments == null)
                        continue;

                    foreach (var seg in segments.ToList())
                    {
                        if (seg == null)
                            continue;

                        // If the segment is currently attached to a panel, remove it safely on UI thread
                        if (seg.Parent is System.Windows.Controls.Panel parentPanel)
                        {
                            parentPanel.Dispatcher.Invoke(() =>
                            {
                                if (parentPanel.Children.Contains(seg))
                                    parentPanel.Children.Remove(seg);
                            });
                        }
                        else
                        {
                            // Fallback: try removing via main window dispatcher searching common canvases
                            Application.Current?.Dispatcher.Invoke(() =>
                            {
                                // Try common canvas names if available (TileCanvas, MapCanvas, etc.)
                                // This is defensive: removal above should normally succeed.
                                if (TileCanvas != null && TileCanvas.Children.Contains(seg))
                                    TileCanvas.Children.Remove(seg);
                            });
                        }
                    }

                    // clear the list for this polyline
                    segments.Clear();
                    _polylineDirectionArrows.Remove(kv.Key);
                }

                // ensure dictionary is empty
                _polylineDirectionArrows.Clear();
            }
            catch (Exception ex)
            {
                // don't crash UI when cleaning up; log for diagnostics
                Console.WriteLine($"[UI CLEANUP] ClearPolylineDirectionArrows: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears everything from the map
        /// </summary>
        private void ClearAll()
        {
            var elementsToRemove = new List<UIElement>();

            foreach (var kv in _zoneArrows.ToArray())
                if (TileCanvas.Children.Contains(kv.Value))
                    TileCanvas.Children.Remove(kv.Value);
            _zoneArrows.Clear();

            foreach (UIElement child in TileCanvas.Children)
            {
                if (child is Rectangle || child is Polyline || child is Line)
                {
                    elementsToRemove.Add(child);
                }
                else if (child is System.Windows.Shapes.Path path)
                {
                    var tag = path.Tag as string;
                    // Support all polyline-related tags
                    if (tag != null && (tag.StartsWith("PolylineZone") ||
                                        tag.StartsWith("PolylineSegment") ||
                                        tag.StartsWith("PolylineCenterLine") ||
                                        tag == "PolylineZoneMerged"))
                    {
                        elementsToRemove.Add(child);
                    }
                }
                else if (child is Ellipse ellipse)
                {
                    var tag = ellipse.Tag as string;

                    if (tag == "PolylineZoneCircle" || tag == "PolylineVertex")
                    {
                        elementsToRemove.Add(child);
                    }
                    else if (ellipse.Tag == null ||
                        (tag != "Tram" &&
                         tag != "Srv" &&
                         tag != "Stop"))
                    {
                        elementsToRemove.Add(child);
                    }
                }
                else if (child is TextBlock textBlock)
                {
                    if (textBlock.Tag == null ||
                        (textBlock.Tag.ToString() != "Tram" &&
                         textBlock.Tag.ToString() != "Srv" &&
                         textBlock.Tag.ToString() != "Stop" &&
                         textBlock.Tag.ToString() != "Signal"))
                    {
                        elementsToRemove.Add(child);
                    }
                }
            }

            foreach (var el in elementsToRemove)
            {
                TileCanvas.Children.Remove(el);
            }

            points.RemoveAll(p => p.Ellipse == null || p.Ellipse.Tag == null ||
                                  (p.Ellipse.Tag.ToString() != "Tram" &&
                                   p.Ellipse.Tag.ToString() != "Srv" &&
                                   p.Ellipse.Tag.ToString() != "Stop"));

            mapRectangles.Clear();
            activationZones.Clear();

            // Clear polyline segments from ActivationZonesCollection
            var polylineSegmentsToRemove = ActivationZonesCollection.Where(z => z.PolylineId.HasValue).ToList();
            foreach (var seg in polylineSegmentsToRemove)
            {
                ActivationZonesCollection.Remove(seg);
            }

            _polylineRows.Clear();
            _polylineToSegmentZones.Clear();

            // Clear remaining activation zones
            ActivationZonesCollection.Clear();

            connectionLine.Points.Clear();
            TramTable.Clear();

            // Clear ALL polyline data structures
            _polylineVertexMap.Clear();
            _polylineVertexToCircle.Clear();
            _polylineToSegments.Clear();
            _polylineGeoPoints.Clear();
            _currentPolylineCircles.Clear();
            _currentPolylineSegments.Clear();
            _drawnPolylines.Clear();

            // Clear active drawing state
            currentPolyline = null;
            polylinePoints.Clear();
            polylineVertexDots.Clear();
            _isDrawingPolyline = false;

            for (int i = 0; i < drawnTrams.Length; i++)
            {
                drawnTrams[i] = null;
                drawnTramTrailPoints[i].Clear();
                drawnTramTrailGeoPoints[i].Clear();
                drawnTramLat[i] = null;
                drawnTramLon[i] = null;

                if (drawnTramTrails?[i] != null)
                {
                    TileCanvas.Children.Remove(drawnTramTrails[i]);
                    drawnTramTrails[i] = null;
                }
            }

            bool isEmpty = points.Count == 0 && mapRectangles.Count == 0 && connectionLine.Points.Count == 0;

            if (isEmpty)
            {
                if (!string.IsNullOrEmpty(loadedFileName))
                {
                    var folder = System.IO.Path.GetDirectoryName(loadedFileName);
                    loadedFileName = System.IO.Path.Combine(folder ?? string.Empty, "default_empty_save.xml");
                }
                else
                {
                    loadedFileName = "default_empty_save.xml";
                }
            }

            isDirty = true;

            rectPhase = RectangleDrawPhase.None;
            isDrawing = false;
            isSelectionMode = true;
            currentDrawingMode = DrawingMode.None;
            UpdateHitTestForSelectableElements();
            CancelAllDrawing();

            Console.WriteLine($"[CLEAR] Complete - all polylines and zones cleared");
        }

        /// <summary>
        /// Logic for Tram1 text box.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Tram1TB_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTramTextChanged) return;

            var tb = sender as TextBox;
            if (tb == null) return;

            // only for 4 characters
            if (tb.Text?.Length > 4)
            {
                _suppressTramTextChanged = true;
                int caret = Math.Min(4, tb.SelectionStart);
                tb.Text = tb.Text.Substring(0, 4);
                tb.SelectionStart = caret;
                _suppressTramTextChanged = false;
            }

            if (isPlaying) return;

            if (!string.IsNullOrWhiteSpace(tb.Text))
                drawnTramIds[0] = "000000" + tb.Text.PadLeft(4, '0');
            else
                drawnTramIds[0] = "0000009999";
        }

        /// <summary>
        /// Logic for Tram2 text box.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Tram2TB_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTramTextChanged) return;

            var tb = sender as TextBox;
            if (tb == null) return;

            // 4 chars
            if (tb.Text?.Length > 4)
            {
                _suppressTramTextChanged = true;
                int caret = Math.Min(4, tb.SelectionStart);
                tb.Text = tb.Text.Substring(0, 4);
                tb.SelectionStart = caret;
                _suppressTramTextChanged = false;
            }

            if (isPlaying) return;

            if (!string.IsNullOrWhiteSpace(tb.Text))
                drawnTramIds[1] = "000000" + tb.Text.PadLeft(4, '0');
            else
                drawnTramIds[1] = "0000001111";
        }

        /// <summary>
        /// Playback file button logic.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnPlaybackFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                DefaultExt = ".camrec",
                Filter = "CAM Recording (*.camrec)|*.camrec|XML files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "Select recording for playback"
            };

            bool? result = dlg.ShowDialog();

            if (result == true)
            {
                LoadPlaybackFile(dlg.FileName);
            }
        }

        /// <summary>
        /// Pause replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_playbackLoaded)
            {
                if (playbackTimer != null && playbackTimer.IsEnabled)
                    playbackTimer.Stop();
                isPlaying = false;

                SyncTramTableForReplay(playbackElapsedTime); // ensure table reflects paused time

                UpdateUiEnabledState();
                return;
            }

            _timeshiftPaused = true;
            _suppressLiveRender = true;
            _timeshiftFollowLive = false;
            _timeshiftPlaybackCts?.Cancel();
            UpdateUiEnabledState();
        }

        /// <summary>
        /// Resume replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            // Classic replay first
            if (_playbackLoaded)
            {
                if (_keyframes == null || _keyframes.Count == 0)
                    BuildPlaybackKeyframes();
                if (_keyframes.Count == 0)
                    return;

                // Make sure slider state won't block updates
                isReplaySliderDragging = false;

                // Start from current slider position
                _playbackIndex = (int)Math.Round(ReplaySlider.Value);
                _playbackIndex = Math.Clamp(_playbackIndex, 0, Math.Max(0, _keyframes.Count - 1));

                // If at the end and no loop, step back one frame so timer can advance
                if (_playbackIndex >= _keyframes.Count - 1)
                {
                    if (LoopCheckbox?.IsChecked == true && _keyframes.Count > 0)
                    {
                        _playbackIndex = 0;
                        ReplaySlider.Value = 0;
                    }
                    else if (_keyframes.Count > 1)
                    {
                        _playbackIndex = _keyframes.Count - 2;
                        ReplaySlider.Value = _playbackIndex;
                    }
                    else
                    {
                        // Single-frame replay; cannot play
                        isPlaying = false;
                        _isPlaybackSessionActive = true;
                        UpdateReplayTimerLabel();
                        UpdateUiEnabledState();
                        return;
                    }
                }

                var t = _keyframes[_playbackIndex];

                playbackElapsedTime = t;
                playbackStartTime = DateTime.Now - playbackElapsedTime;
                RedrawPlaybackToTime(t);
                UpdateReplayTimerLabel();

                UpdateReplayStatsForTime(t);

                // Ensure timer exists and is running
                if (playbackTimer == null)
                {
                    playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                    playbackTimer.Tick += PlaybackTimer_Tick;
                }

                _isPlaybackSessionActive = true;
                isPlaying = true;

                if (!playbackTimer.IsEnabled)
                    playbackTimer.Start();

                UpdateUiEnabledState();
                return;
            }

            // Timeshift resume – only when no classic replay is loaded
            if (_timeshiftEnabled)
            {
                _timeshiftPaused = false;
                _suppressLiveRender = false;
                _timeshiftFollowLive = false;
                StartTimeshiftCatchupFromSliderAsync();
            }

            UpdateUiEnabledState();
        }

        /// <summary>
        /// Logic for when the selected COM port is changed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ComPortsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        /// <summary>
        /// Refresh all COM ports.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RefreshComPorts_Click(object sender, RoutedEventArgs e)
        {
            LoadAvailableComPorts();
        }

        /// <summary>
        /// Handle mouse events while replay is active and user is dragging playback slider.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PlaybackSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isSliderDragging = true;

            if (_timeshiftEnabled)
            {
                _timeshiftFollowLive = false;
                _timeshiftPaused = true;
                _suppressLiveRender = true;
                _timeshiftPlaybackCts?.Cancel();
            }

            if (playbackTimer != null)
                playbackTimer.Stop();
        }

        /// <summary>
        /// Update timer label while replay is active.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void UpdateTimerLabel()
        {
            try
            {
                string text;
                if (_replayStartUtc.HasValue)
                {
                    // Show absolute replay time (local)
                    var curUtc = _replayStartUtc.Value.Add(playbackElapsedTime);
                    var curLocal = TimeZoneInfo.ConvertTimeFromUtc(curUtc.ToUniversalTime(), czechTimeZone);
                    text = $"{curLocal:HH:mm:ss}";
                }
                else
                {
                    // Fallback: elapsed timeline
                    text = $"{playbackElapsedTime:hh\\:mm\\:ss}";
                }

                if (CurrentTimeLabel != null)
                    CurrentTimeLabel.Content = text;
            }
            catch { /* ignore if label not present */ }
        }

        /// <summary>
        /// Loop replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LoopCheckbox_Checked(object sender, RoutedEventArgs e)
        {
            if (LoopCheckbox?.IsChecked == true && _keyframes.Count > 0 &&
                (!isPlaying || _playbackIndex >= _keyframes.Count - 1))
            {
                _playbackIndex = 0;
                playbackElapsedTime = _keyframes[0];
                playbackStartTime = DateTime.Now - playbackElapsedTime;

                RedrawPlaybackToTime(playbackElapsedTime);

                if (playbackTimer == null)
                {
                    playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                    playbackTimer.Tick += PlaybackTimer_Tick;
                }
                if (!playbackTimer.IsEnabled)
                    playbackTimer.Start();

                _isPlaybackSessionActive = true;
                isPlaying = true;

                if (!isReplaySliderDragging)
                    ReplaySlider.Value = 0;

                UpdateTimerLabel();
                UpdateReplayTimerLabel();
            }
        }

        /// <summary>
        /// Set custom speed for simulated tram.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SpeedBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = SpeedBox.Text?.Trim() ?? "";
            text = text.Replace("km/h", "", StringComparison.OrdinalIgnoreCase).Trim();
            text = text.Replace(',', '.');

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var spd) && spd >= 0)
                _manualCamSpeedKmh = spd;
            else
                _manualCamSpeedKmh = 0.0;
        }

        /// <summary>
        /// Send test SRV message.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SendSrv_Click(object sender, RoutedEventArgs e)
        {
            SendSrvMessage();
        }

        /// <summary>
        /// Send test SRV messages periodically.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SrvCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (IsTransportConnected())
            {
                SendSrvMessage();
                StartSrvAutoTimerIfEnabled();
            }
        }

        /// <summary>
        /// Send test SRV message.
        /// </summary>
        private void SendSrvMessage()
        {
            double lat = latitude;
            double lon = longitude;

            string logicalId = "RE108031";
            string dt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            double alt = 0.0;

            string service = $"<service logicalId=\"{logicalId}\" dt=\"{dt}\" lat=\"{lat.ToString(CultureInfo.InvariantCulture)}\" lng=\"{lon.ToString(CultureInfo.InvariantCulture)}\" alt=\"{alt.ToString(CultureInfo.InvariantCulture)}\" />";

            ushort crc = ComputeCRC(service);
            string xml = $"<SRV>{service}<crc>{crc}</crc></SRV>";

            try
            {
                if (IsTransportConnected())
                {
                    SendTransportLine(xml);
                }
                else
                {
                    Console.WriteLine(
                        "[TX][SRV] No active connection. Skipping transmit.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TX][SRV] Error sending SRV over serial: " + ex.Message);
            }

            if (_timeshiftEnabled)
            {
                AddSrvToBuffer(xml);
            }

            try
            {
                var parsed = V2XMessageParser.ParseV2XMessage(xml);
                HandleV2XMessage(parsed, xml);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SRV] Local parse failed: " + ex.Message);
            }

            Console.WriteLine("[TX][SRV] " + xml);
        }

        /// <summary>
        /// Send test SRV messages periodically (default: every minute).
        /// </summary>

        private void StartSrvAutoTimerIfEnabled()
        {
            if (SrvCheckBox?.IsChecked != true)
                return;

            if (_srvTimer == null)
            {
                _srvTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _srvTimer.Tick += (s, e) => SendSrvMessage();
            }
            if (!_srvTimer.IsEnabled)
                _srvTimer.Start();
        }

        /// <summary>
        /// Stops timer for auto sending messages.
        /// </summary>
        private void StopSrvAutoTimer()
        {
            if (_srvTimer != null)
                _srvTimer.Stop();
        }

        /// <summary>
        /// Update UI enabled states.
        /// </summary>
        private void UpdateUiEnabledState()
        {
            bool canConfigureConnection = !_isConnected;

            Disconnect?.SetValue(IsEnabledProperty, _isConnected);
            SendSrv?.SetValue(IsEnabledProperty, _isConnected);
            SrvCheckBox?.SetValue(IsEnabledProperty, _isConnected);
            Connect?.SetValue(IsEnabledProperty, canConfigureConnection);

            ConnectionTypeComboBox?.SetValue(IsEnabledProperty, canConfigureConnection);
            ComPortsComboBox?.SetValue(IsEnabledProperty, canConfigureConnection);
            EthernetAddressTB?.SetValue(IsEnabledProperty, canConfigureConnection);
            EthernetPortTB?.SetValue(IsEnabledProperty, canConfigureConnection);
            RefreshComPorts?.SetValue(IsEnabledProperty, canConfigureConnection);

            if (BaudrateTB != null)
            {
                BaudrateTB.IsReadOnly = _isConnected;
                BaudrateTB.IsEnabled = canConfigureConnection;
            }

            // Drawing and tram simulation
            AccuracyCB?.SetValue(IsEnabledProperty, _isConnected);
            FilterTram?.SetValue(IsEnabledProperty, _isConnected);

            // Manual recording buttons
            if (btnStartRecording != null)
                btnStartRecording.IsEnabled = _isConnected && !isRecording;
            if (btnStopRecording != null)
                btnStopRecording.IsEnabled = _isConnected && isRecording;

            // Classic replay controls
            PlayMovement?.SetValue(IsEnabledProperty, _playbackLoaded);
            LoopCheckbox?.SetValue(IsEnabledProperty, _playbackLoaded);
            StopReplay?.SetValue(IsEnabledProperty, _playbackLoaded);
            ReloadReplay?.SetValue(IsEnabledProperty, _playbackLoaded);
            ReplaySlider?.SetValue(IsEnabledProperty, _playbackLoaded);
            TramBox?.SetValue(IsEnabledProperty, _playbackLoaded);

            // Timeshift controls
            btnPlay?.SetValue(IsEnabledProperty, _playbackLoaded);
            btnPause?.SetValue(IsEnabledProperty, _playbackLoaded);
            FilterCheckBox?.SetValue(IsEnabledProperty, _isConnected || _playbackLoaded);
            ExportButton?.SetValue(IsEnabledProperty, ActivationZonesCollection != null && ActivationZonesCollection.Count > 0);
            CircleCheckBox?.SetValue(IsEnabledProperty, _isConnected);
            PrevCam?.SetValue(IsEnabledProperty, _playbackLoaded);
            NextCam?.SetValue(IsEnabledProperty, _playbackLoaded);
        }

        private void AddRecordedCamMessage(string msg)
        {
            AddCamToBuffer(msg);

            if (recordedCamMessages.Count > MaxRecordedCamMessages)
            {
                int removeCount = recordedCamMessages.Count - MaxRecordedCamMessages;
                recordedCamMessages.RemoveRange(0, removeCount);
            }
        }

        /// <summary>
        /// Reproject activation zones while dragging.
        /// </summary>
        public void ReprojectActivationZonesOnMapChange()
        {
            // Reproject all non-switch zones from geo -> canvas, using per-zone latitude for MPP
            foreach (var zone in ActivationZonesCollection.Where(z => z != null && !IsSwitchZone(z)))
            {
                if (zone.Rectangle == null) continue;

                // meters -> pixels at current zoom (local latitude for better accuracy)
                double mppLocal = MetersPerPixel(zone.Latitude, zoom);
                double widthPx = zone.Width / mppLocal;
                double heightPx = zone.Height / mppLocal;

                zone.Rectangle.Width = widthPx;
                zone.Rectangle.Height = heightPx;

                // Start point from stored Lat/Lon
                var (sx, sy) = ConvertLatLonToCanvasXY(zone.Latitude, zone.Longitude);
                zone.StartPoint = new Point(sx, sy);

                UpdateRectanglePositionFromStartPoint(zone); // anchors to base center and re-applies rotation
                UpdateActivationZoneBounds(zone);

                Panel.SetZIndex(zone.Rectangle, 100);
                if (!TileCanvas.Children.Contains(zone.Rectangle))
                    TileCanvas.Children.Add(zone.Rectangle);
            }
        }

        /// <summary>
        /// Reprojects active vehicles if the map changes (panning, zooming, etc.)
        /// </summary>
        private void ReprojectActiveVehiclesOnMapChange()
        {
            foreach (var kv in activeVehicles)
            {
                var id = kv.Key;
                var mp = kv.Value;
                if (!_lastLatLon.TryGetValue(id, out var ll)) continue;

                var (x, y) = ConvertLatLonToCanvasXY(ll.lat, ll.lon);
                bool isSrv = string.Equals(mp.Ellipse?.Tag?.ToString(), "Srv", StringComparison.OrdinalIgnoreCase);
                var color = mp.VehicleColor ?? mp.Ellipse?.Fill ?? Brushes.Black;

                UpdateVehicleCanvasPosition(mp, new Point(x, y), color, isSrv, mp.Label);

                Panel.SetZIndex(mp.Ellipse, 1000);
                if (mp.Text != null) Panel.SetZIndex(mp.Text, 1000);

                if (!isSrv && _lastHeadingLive.TryGetValue(id, out var hdg))
                {
                    var liveHeadingAdj = (hdg - 180 + 360) % 360;
                    UpdateOrCreateVehicleBox(id, new Point(x, y), color, liveHeadingAdj);
                }
            }
        }

        /// <summary>
        /// Reprojects all zones if the map changes (panning, zooming, etc.)
        /// </summary>
        public async Task ReprojectAllZonesOnMapChange()
        {
            ReprojectActivationZonesOnMapChange();
            ReprojectSwitchZonesOnMapChange();
            ReprojectActiveVehiclesOnMapChange();
            ReprojectReplayOnMapChange();
            ReprojectDrawnTramsOnMapChange();
            ReprojectPolylines();
            //DrawStopsOnCanvasSafe();
            UpdateTramSignalPositions();
            await BringAllOverlaysToFrontSafeAsync();
        }

        // place near other reproject methods
        private void ReprojectReplayOnMapChange()
        {
            if (_replayGeoFrames.Count == 0) return;

            // Rebuild canvas-space frames from stored geo
            _replayFrames.Clear();
            foreach (var kv in _replayGeoFrames)
            {
                var id = kv.Key;
                var geo = kv.Value;
                var frames = new List<MovementFrame>(geo.Count);
                foreach (var g in geo)
                {
                    var (x, y) = ConvertLatLonToCanvasXY(g.lat, g.lon);
                    frames.Add(new MovementFrame { Timestamp = g.ts, Position = new Point(x, y) });
                }
                _replayFrames[id] = frames;
            }

            // Redraw current replay visuals at the same time index
            RedrawPlaybackToTime(playbackElapsedTime);
        }

        /// <summary>
        /// Redraws polyline.
        /// </summary>
        private void ReprojectPolylines()
        {
            UpdatePolylinePositions();
        }

        /// <summary>
        /// Redraws trams if the user is panning, zooming, etc.
        /// </summary>
        private void ReprojectDrawnTramsOnMapChange()
        {
            for (int idx = 0; idx < drawnTrams.Length; idx++)
            {
                var tram = drawnTrams[idx];

                // Do we have anything to render for this slot?
                bool hasGeo = (drawnTramLat[idx].HasValue && drawnTramLon[idx].HasValue)
                              || drawnTramTrailGeoPoints[idx].Count > 0;

                // Lazily create MapPoint when we have geo but no visuals yet
                if (tram == null)
                {
                    if (!hasGeo)
                        continue;

                    tram = new MapPoint
                    {
                        Label = drawnTramIds[idx],
                        VehicleColor = drawnTramColors[idx],
                        TrailDots = new List<Ellipse>(),
                        MovementFrames = new List<MovementFrame>(),
                        IsRecorded = true,
                        LastUpdate = DateTime.Now
                    };
                    drawnTrams[idx] = tram;
                }

                // Ensure ellipse/text exist
                EnsureMapPointVisuals(tram, drawnTramColors[idx], isSrv: false);

                // Reposition to last known geo
                if (drawnTramLat[idx].HasValue && drawnTramLon[idx].HasValue)
                {
                    var (x, y) = ConvertLatLonToCanvasXY(drawnTramLat[idx]!.Value, drawnTramLon[idx]!.Value);
                    UpdateVehicleCanvasPosition(tram, new Point(x, y), drawnTramColors[idx], false, tram.Label);
                    if (tram.Ellipse != null) Panel.SetZIndex(tram.Ellipse, 1000);
                    if (tram.Text != null) Panel.SetZIndex(tram.Text, 1000);

                    // Update body heading if we have at least 2 geo points
                    if (drawnTramTrailGeoPoints[idx].Count >= 2)
                    {
                        var (plat, plon) = drawnTramTrailGeoPoints[idx][^2];
                        var (px, py) = ConvertLatLonToCanvasXY(plat, plon);
                        var headingDeg = CalculateAzimuth(new Point(px, py), new Point(x, y));
                        //headingDeg = (headingDeg - 180 + 360) % 360; // keep manual flip rule
                        UpdateOrCreateVehicleBox(drawnTramIds[idx], new Point(x, y), drawnTramColors[idx], headingDeg);
                    }
                    else
                    {
                        if (_vehicleBoxes.TryGetValue(drawnTramIds[idx], out var rect))
                        {
                            TileCanvas.Children.Remove(rect);
                            _vehicleBoxes.Remove(drawnTramIds[idx]);
                        }
                    }
                }

                // Rebuild manual trail from geo points
                if (drawnTramTrailGeoPoints[idx].Count > 0)
                {
                    if (drawnTramTrails[idx] == null)
                    {
                        drawnTramTrails[idx] = new Polyline
                        {
                            Stroke = drawnTramColors[idx],
                            StrokeThickness = 2,
                            IsHitTestVisible = false
                        };
                        TileCanvas.Children.Add(drawnTramTrails[idx]);
                    }

                    var pl = drawnTramTrails[idx];
                    pl.Points.Clear();

                    // Clear old dots safely
                    if (tram.TrailDots == null) tram.TrailDots = new List<Ellipse>();
                    foreach (var d in tram.TrailDots.ToList())
                        TileCanvas.Children.Remove(d);
                    tram.TrailDots.Clear();

                    for (int i = 0; i < drawnTramTrailGeoPoints[idx].Count; i++)
                    {
                        var (lat, lon) = drawnTramTrailGeoPoints[idx][i];
                        var (tx, ty) = ConvertLatLonToCanvasXY(lat, lon);
                        pl.Points.Add(new Point(tx, ty));

                        // black dot at intermediate points
                        if (i < drawnTramTrailGeoPoints[idx].Count - 1)
                        {
                            var dot = new Ellipse
                            {
                                Width = 5,
                                Height = 5,
                                Fill = Brushes.Black,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(dot, tx - 2.5);
                            Canvas.SetTop(dot, ty - 2.5);
                            TileCanvas.Children.Add(dot);
                            Panel.SetZIndex(dot, 1001);
                            tram.TrailDots.Add(dot);
                        }
                    }

                    Panel.SetZIndex(pl, 999);
                }
            }
        }

    }
}
