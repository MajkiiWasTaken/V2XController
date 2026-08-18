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
    // Playback timeline, timeshift, replay filtering and zone editing helpers
    public partial class MainWindow
    {
        private void BuildPlaybackKeyframes()
        {
            var set = new SortedSet<TimeSpan>();

            foreach (var pt in points)
                if (pt.MovementFrames != null)
                    foreach (var f in pt.MovementFrames)
                        set.Add(f.Timestamp);

            foreach (var frames in _replayFrames.Values)
                foreach (var f in frames)
                    set.Add(f.Timestamp);

            foreach (var srv in _replaySrvFramesById.Values)
                foreach (var f in srv)
                    set.Add(f.ts);

            foreach (var frame in _replaySignalFrames)
                set.Add(frame.ts);

            for (int i = 0; i < drawnTrams.Length; i++)
            {
                var tram = drawnTrams[i];
                if (tram?.MovementFrames == null) continue;
                foreach (var f in tram.MovementFrames)
                    set.Add(f.Timestamp);
            }

            _keyframes = set.ToList();
            if (_keyframes.Count == 0)
                _keyframes.Add(TimeSpan.Zero);

            _playbackIndex = 0;
            playbackMaxTime = _keyframes[^1];

            ReplaySlider.Minimum = 0;
            ReplaySlider.Maximum = Math.Max(0, _keyframes.Count - 1);
            ReplaySlider.Value = 0;
            ReplaySlider.TickFrequency = 1;
            ReplaySlider.SmallChange = 1;
            ReplaySlider.LargeChange = Math.Max(1, _keyframes.Count / 20);
            ReplaySlider.IsSnapToTickEnabled = true;

            UpdateReplayTimerLabel();
        }

        private void NextCam_Click(object sender, RoutedEventArgs e)
        {
            if (_keyframes.Count == 0) return;

            int nextIdx = _playbackIndex + 1;
            if (nextIdx >= _keyframes.Count) return;

            _playbackIndex = nextIdx;
            var t = _keyframes[_playbackIndex];
            playbackElapsedTime = t;
            playbackStartTime = DateTime.Now - playbackElapsedTime;

            RedrawPlaybackToTime(t);
            UpdateReplayTimerLabel();
            UpdateTimerLabel();
            UpdateReplayStatsForTime(t);
            SyncTramTableForReplay(t);

            if (isPlaying && playbackTimer != null && !playbackTimer.IsEnabled)
                playbackTimer.Start();
        }

        private void PrevCam_Click(object sender, RoutedEventArgs e)
        {
            if (_keyframes.Count == 0) return;

            int prevIdx = _playbackIndex - 1;
            if (prevIdx < 0) return;

            _playbackIndex = prevIdx;
            var t = _keyframes[_playbackIndex];
            playbackElapsedTime = t;
            playbackStartTime = DateTime.Now - playbackElapsedTime;

            RedrawPlaybackToTime(t);
            UpdateReplayTimerLabel();
            UpdateTimerLabel();
            UpdateReplayStatsForTime(t);
            SyncTramTableForReplay(t);

            if (isPlaying && playbackTimer != null && !playbackTimer.IsEnabled)
                playbackTimer.Start();
        }

        /// <summary>
        /// Gets time index.
        /// </summary>
        /// <param name="time">Index by time</param>
        private int GetIndexForTime(TimeSpan time)
        {
            if (_keyframes.Count == 0) return 0;
            int lo = 0, hi = _keyframes.Count - 1, ans = 0;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_keyframes[mid] <= time)
                {
                    ans = mid;
                    lo = mid + 1;
                }
                else hi = mid - 1;
            }
            return ans;
        }

        /// <summary>
        /// Sends playback CAMs for user defined time.
        /// </summary>
        /// <param name="t">Defined time</param>
        private void SendPlaybackCamForTime(TimeSpan t)
        {
            if (!isPlaying) return;

            foreach (var kv in _replayFrames)
            {
                var id = kv.Key;
                var frames = kv.Value;
                var frame = frames.FirstOrDefault(f => f.Timestamp == t);
                if (frame == null) continue;

                var lonlat = CanvasPixelsToLatLon(frame.Position, latitude, longitude, zoom);
                double lat = lonlat.Y;
                double lon = lonlat.X;

                double speedMs = 0.0;
                string key = $"{id}|{t.Ticks}";
                if (_playbackSpeedByIdAndTs.TryGetValue(key, out var spd))
                    speedMs = spd;

                // Do not render locally; avoid mixing live state
                SendPointAsCamMessage(id, lat, lon, speed: speedMs, suppressLocalRender: true);
            }

            UpdateReplayStatsForTime(t);
        }

        /// <summary>
        /// Handler for Activation zone datagrid.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ActivationZonesDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            if (_pendingNewZone == null)
                return;

            if (ActivationZonesDataGrid?.CurrentItem is not ActivationZone current)
                return;

            if (!ReferenceEquals(current, _pendingNewZone))
                return;

            ActivationZonesDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ActivationZonesDataGrid.CommitEdit(DataGridEditingUnit.Row, true);

            TryFinalizePendingNewZone();
            e.Handled = true;
        }

        /// <summary>
        /// Adds new row into activation zones datagrid
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void NewRow_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine($"[ROW] Add table row clicked");
            Console.WriteLine($"[ROW] Switch mode: {IsSwitchMode()}");

            var zone = new ActivationZone
            {
                Name = string.Empty,
                Latitude = double.NaN,
                Longitude = double.NaN,
                Width = 0,
                Height = 0,
                Azimuth = 0,
                Color = "#FF0000",
                LastTramId = "-",
                MainZone = 0,
                SubZone = 0
            };

            _pendingNewZone = zone;

            // mark as switch-row if SwitchRadio is active (so finalize will tag it properly)
            if (IsSwitchMode())
                _switchRows.Add(zone);

            ActivationZonesCollection.Add(zone);

            // Focus first cell in the single grid
            Dispatcher.BeginInvoke(new Action(() => { FocusCell(zone, 0); }), DispatcherPriority.Background);
        }

        /// <summary>
        /// Tries to finalize pending zone, if zone is valid, adds as a new zone.
        /// </summary>
        private bool TryFinalizePendingNewZone()
        {
            var zone = _pendingNewZone;
            if (zone == null) return false;

            // Auto-fill name from MainZone/SubZone if left empty
            if (string.IsNullOrWhiteSpace(zone.Name) && zone.MainZone >= 0 && zone.SubZone >= 0)
            {
                bool isSwitch = _switchRows.Contains(zone);
                zone.IsSwitchZone = isSwitch;
                zone.UpdateName();
            }

            // Auto-fill color based on MainZone if still at default red or empty
            if (string.IsNullOrWhiteSpace(zone.Color) || zone.Color == "#FF0000")
            {
                bool isSwitch = _switchRows.Contains(zone);
                zone.Color = GetColorForMainZone(zone.MainZone, isSwitch);
            }

            bool missing =
                string.IsNullOrWhiteSpace(zone.Name) ||
                double.IsNaN(zone.Latitude) ||
                double.IsNaN(zone.Longitude) ||
                zone.Width <= 0 ||
                zone.Height <= 0 ||
                zone.Azimuth < 0 || zone.Azimuth > 359;

            if (missing)
            {
                var res = MessageBox.Show(
                    "The new activation zone is missing required properties.\n\n" +
                    "Required: Name, Latitude, Longitude, Azimuth (0–359), Width (>0), Height (>0).",
                    "Incomplete row",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Dispatcher.BeginInvoke(new Action(() => FocusFirstMissingField(zone)), DispatcherPriority.Background);
                return true;
            }

            if (zone.Rectangle != null)
            {
                _switchRows.Remove(zone);
                _pendingNewZone = null;
                return true;
            }

            double mpp = MetersPerPixel(latitude, zoom);
            double widthPx = zone.Width / mpp;
            double heightPx = zone.Height / mpp;

            var rect = new Rectangle
            {
                Stroke = (SolidColorBrush)(new BrushConverter().ConvertFromString(zone.Color ?? "#FF0000")),
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                Tag = (_switchRows.Contains(zone) || (!string.IsNullOrWhiteSpace(zone.Name) && zone.Name.StartsWith("Switch", StringComparison.OrdinalIgnoreCase)))
                      ? "SwitchZone" : "DrawnRectangle",
                Uid = zone.Name,
                Width = widthPx,
                Height = heightPx,
                IsHitTestVisible = true
            };

            zone.Rectangle = rect;

            var (sx, sy) = ConvertLatLonToCanvasXY(zone.Latitude, zone.Longitude);
            zone.StartPoint = new Point(sx, sy);

            UpdateRectanglePositionFromStartPoint(zone);
            ApplyZoneRotation(zone);
            UpdateActivationZoneBounds(zone);

            TileCanvas.Children.Add(rect);
            activationZones[rect] = zone;
            if (!ActivationZonesCollection.Contains(zone))
                ActivationZonesCollection.Add(zone);

            rect.MouseEnter += Rectangle_MouseEnter;
            rect.MouseLeave += Rectangle_MouseLeave;
            rect.MouseLeftButtonDown += Rectangle_MouseLeftButtonDown;
            Panel.SetZIndex(rect, 100);

            isDirty = true;
            _switchRows.Remove(zone);
            _pendingNewZone = null;
            return true;
        }

        /// <summary>
        /// Focuses on cell in activation zones datagrid.
        /// </summary>
        /// <param name="zone">Activation zone</param>
        /// <param name="columnIndex">Index of the column</param>
        private void FocusCell(ActivationZone zone, int columnIndex)
        {
            if (ActivationZonesDataGrid == null) return;
            columnIndex = Math.Clamp(columnIndex, 0, ActivationZonesDataGrid.Columns.Count - 1);

            ActivationZonesDataGrid.UpdateLayout();
            ActivationZonesDataGrid.ScrollIntoView(zone);
            ActivationZonesDataGrid.SelectedItem = zone;
            ActivationZonesDataGrid.CurrentCell = new DataGridCellInfo(zone, ActivationZonesDataGrid.Columns[columnIndex]);
            ActivationZonesDataGrid.BeginEdit();

            // Try move keyboard focus into the cell’s editing element
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var cellContent = ActivationZonesDataGrid.CurrentCell.Column?.GetCellContent(zone);
                if (cellContent != null)
                    Keyboard.Focus(cellContent);
            }), DispatcherPriority.Input);
        }

        /// <summary>
        /// Focus into first missing field in activation zones datagrid
        /// </summary>
        /// <param name="zone">Which zone to focus to</param>
        private void FocusFirstMissingField(ActivationZone zone)
        {
            if (string.IsNullOrWhiteSpace(zone.Name)) { FocusCell(zone, 0); return; }
            if (double.IsNaN(zone.Latitude)) { FocusCell(zone, 1); return; }
            if (double.IsNaN(zone.Longitude)) { FocusCell(zone, 2); return; }
            if (zone.Azimuth < 0 || zone.Azimuth > 359) { FocusCell(zone, 3); return; }
            if (zone.Width <= 0) { FocusCell(zone, 4); return; }
            if (zone.Height <= 0) { FocusCell(zone, 5); return; }
        }

        /// <summary>
        /// Clears trams from playback that were left as remainders from stopped replay.
        /// </summary>
        private void ClearPlaybackTramsFromCanvas()
        {
            // existing drawnTrams cleanup (kept)
            for (int i = 0; i < drawnTrams.Length; i++)
            {
                var tram = drawnTrams[i];
                if (tram != null)
                {
                    if (tram.Ellipse != null) TileCanvas.Children.Remove(tram.Ellipse);
                    if (tram.Text != null) TileCanvas.Children.Remove(tram.Text);
                    if (tram.Speed != null) TileCanvas.Children.Remove(tram.Speed);

                    if (tram.TrailDots != null)
                    {
                        foreach (var dot in tram.TrailDots.ToList())
                            TileCanvas.Children.Remove(dot);
                        tram.TrailDots.Clear();
                    }
                }

                if (drawnTramTrails[i] != null)
                {
                    TileCanvas.Children.Remove(drawnTramTrails[i]);
                    drawnTramTrails[i] = null;
                }

                drawnTramTrailPoints[i].Clear();
                drawnTrams[i] = null;
            }

            foreach (var kv in _replayVehicles.ToList())
            {
                var id = kv.Key;
                var mp = kv.Value;
                if (mp.Ellipse != null) TileCanvas.Children.Remove(mp.Ellipse);
                if (mp.Text != null) TileCanvas.Children.Remove(mp.Text);
                if (mp.Speed != null) TileCanvas.Children.Remove(mp.Speed);
                if (mp.TrailDots != null)
                {
                    foreach (var d in mp.TrailDots.ToList()) TileCanvas.Children.Remove(d);
                    mp.TrailDots.Clear();
                }
                // remove trail polylines
                var old = TileCanvas.Children.OfType<Polyline>()
                    .Where(pl => string.Equals(pl.Tag as string, $"replay_trail_{id}", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var l in old) TileCanvas.Children.Remove(l);

            }

            _replayVehicles.Clear();

            foreach (var kv in _replayBoxes.ToList())
            {
                TileCanvas.Children.Remove(kv.Value);
            }
            _replayBoxes.Clear();

            // ClearPlaybackTramsFromCanvas() – also clear SRV replay visuals (append near other clears)
            foreach (var kv in _replaySrvPoints.ToList())
            {
                var mp = kv.Value;
                if (mp.Ellipse != null) TileCanvas.Children.Remove(mp.Ellipse);
                if (mp.Text != null) TileCanvas.Children.Remove(mp.Text);
            }
            _replaySrvPoints.Clear();

            var accEllipses = TileCanvas.Children.OfType<Ellipse>()
                .Where(e => e.Tag is string s && s.StartsWith("replay_acc_", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var e in accEllipses) TileCanvas.Children.Remove(e);

            _playbackAccuracyByIdAndTs.Clear();
        }

        /// <summary>
        /// Stops replay and resets buffer for replay.
        /// </summary>
        private void StopPlaybackAndReset()
        {
            playbackTimer?.Stop();
            isPlaying = false;
            _isPlaybackSessionActive = false;

            ClearPlaybackTramsFromCanvas();

            _keyframes.Clear();
            _playbackIndex = 0;
            playbackElapsedTime = TimeSpan.Zero;
            playbackMaxTime = TimeSpan.Zero;
            _playbackLoaded = false;

            _replayStartUtc = null;
            _replayEndUtc = null;

            _replayGeoFrames.Clear();
            TramTable.Clear();
            _replayFrames.Clear();
            _replayVehicles.Clear();
            _playbackHeadingByIdAndTs.Clear();
            foreach (var kv in _replayBoxes.ToList())
                TileCanvas.Children.Remove(kv.Value);
            _replayBoxes.Clear();

            try { UpdateTimerLabel(); UpdateReplayTimerLabel(); } catch { }

            _replaySignalFrames.Clear();
        }

        /// <summary>
        /// Purges and ignores new CAM messages while replay is active.
        /// </summary>
        private void PurgeLiveCamVehiclesForPlayback()
        {
            foreach (var kv in activeVehicles.ToList())
            {
                var veh = kv.Value;
                var tag = veh.Ellipse?.Tag?.ToString();

                // ponech RSU/SRV body, smaž ostatní (CAM)
                if (!string.Equals(tag, "Srv", StringComparison.OrdinalIgnoreCase))
                {
                    RemoveVehicleCompletely(kv.Key, veh);
                }
            }

            // volitelně pročisti tabulku tramvají (aby nezůstaly viset staré RS485 záznamy)
            TramTable.Clear();
        }

        /// <summary>
        /// Handler for stop replay button
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void StopReplay_Click(object sender, RoutedEventArgs e)
        {
            StopPlaybackAndReset();
            UpdateUiEnabledState();
        }

        /// <summary>
        /// Resets selected replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ReloadReplay_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastReplayFile) || !File.Exists(_lastReplayFile))
            {
                MessageBox.Show("No previously loaded replay to reload.", "Reload replay", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // LoadPlaybackFile internally stops any playback, clears visuals and reloads frames
            LoadPlaybackFile(_lastReplayFile);
        }

        /// <summary>
        /// Starts timeshift session while replay is active.
        /// </summary>
        private void StartTimeshiftSession()
        {
            _timeshiftEnabled = true;
            _timeshiftPaused = false;
            _suppressLiveRender = false;
            _timeshiftFollowLive = true;  // follow live edge by default

            _timeshiftStartUtc = DateTime.UtcNow;

            // DO NOT touch isRecording here (manual recording is separate)
            // recordingStartTime stays for manual recording only

            if (_timeshiftUiTimer == null)
            {
                _timeshiftUiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _timeshiftUiTimer.Tick += (s, e) =>
                {
                    if (!_timeshiftEnabled) return;

                    var elapsed = DateTime.UtcNow - _timeshiftStartUtc;

                    UpdateTimeshiftTimerLabel(elapsed);
                };
            }
            if (!_timeshiftUiTimer.IsEnabled)
                _timeshiftUiTimer.Start();

            UpdateUiEnabledState();
        }

        /// <summary>
        /// Stops timeshift session and returns to replay.
        /// </summary>
        private void StopTimeshiftSession()
        {
            _timeshiftEnabled = false;
            _timeshiftPaused = false;
            _suppressLiveRender = false;

            _timeshiftUiTimer?.Stop();

            _markInUtc = _markOutUtc = null;

            // Do not auto-save here; simply stop “recording”
            isRecording = false;

            // keep buffer recordedCamMessages in memory; user may export range later if desired
        }

        /// <summary>
        /// Updates timeshift timer label while dragging.
        /// </summary>
        /// <param name="elapsedOverride">Override elapsed timer</param>
        private void UpdateTimeshiftTimerLabel(TimeSpan? elapsedOverride = null)
        {
            try
            {
                var elapsed = elapsedOverride ?? (DateTime.UtcNow - _timeshiftStartUtc);
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            }
            catch { }
        }

        /// <summary>
        /// Starts timeshift catchup while dragging timeshift slider.
        /// </summary>
        private void StartTimeshiftCatchupFromSliderAsync()
        {
            // cancel previous run if any
            _timeshiftPlaybackCts?.Cancel();
            _timeshiftPlaybackCts = new CancellationTokenSource();
            var token = _timeshiftPlaybackCts.Token;

            var liveUtc = DateTime.UtcNow;

            _isTimeshiftPlaybackActive = true;
            _suppressLiveRender = false; // we are rendering the catch-up
            _timeshiftPaused = false;

            Task.Run(async () =>
            {
                try
                {
                    // select CAMs in [startUtc, liveUtc] ordered by lastRec
                    var selection = new List<(DateTime t, string xml)>();
                    foreach (var cam in recordedCamMessages)
                    {
                        int vehPtStart = cam.IndexOf("<vehPt", StringComparison.OrdinalIgnoreCase);
                        if (vehPtStart < 0) continue;
                        int lastRecIdx = cam.IndexOf("lastRec=\"", vehPtStart, StringComparison.OrdinalIgnoreCase);
                        if (lastRecIdx < 0) continue;
                        int vStart = lastRecIdx + "lastRec=\"".Length;
                        int vEnd = cam.IndexOf('"', vStart);
                        if (vEnd < 0) continue;
                        var lastRecStr = cam.Substring(vStart, vEnd - vStart);
                        if (!DateTime.TryParse(lastRecStr, null, DateTimeStyles.RoundtripKind, out var tUtc)) continue;

                    }

                    if (selection.Count == 0)
                    {
                        // žádné rámce k dohrání – zůstaň na vybraném čase, nechoď na live
                        Dispatcher.Invoke(() =>
                        {
                            _isTimeshiftPlaybackActive = false;
                            _suppressLiveRender = false;
                            _timeshiftPaused = false;
                            _timeshiftFollowLive = false; // NEaktivuj auto-follow
                            UpdateTimeshiftTimerLabel();
                        });
                        return;
                    }

                    selection.Sort((a, b) => a.t.CompareTo(b.t));

                    // play back in real time
                    DateTime? prev = null;
                    foreach (var (t, xml) in selection)
                    {
                        token.ThrowIfCancellationRequested();

                        if (prev != null)
                        {
                            var delay = t - prev.Value;
                            // clamp excessive gaps
                            if (delay > TimeSpan.Zero && delay < TimeSpan.FromSeconds(5))
                                await Task.Delay(delay, token);
                        }
                        prev = t;

                        try
                        {
                            var msg = V2XMessageParser.ParseV2XMessage(xml);
                            // Render on UI thread
                            Dispatcher.Invoke(() => HandleV2XMessage(msg, xml));

                            // advance slider to reflect current catch-up position
                            Dispatcher.Invoke(() =>
                            {
                                var seconds = (t - _timeshiftStartUtc).TotalSeconds;
                                UpdateTimeshiftTimerLabel();
                            });
                        }
                        catch { /* ignore malformed */ }
                    }
                }
                catch (OperationCanceledException) { /* cancelled */ }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        _isTimeshiftPlaybackActive = false;
                        _suppressLiveRender = false;
                        _timeshiftFollowLive = false;
                        UpdateTimeshiftTimerLabel();
                    });
                }
            });
        }

        /// <summary>
        /// Updates timer label while replay is active.
        /// </summary>
        private void UpdateReplayTimerLabel()
        {
            try
            {
                if (_replayStartUtc.HasValue && _replayEndUtc.HasValue)
                {
                    var startLocal = TimeZoneInfo.ConvertTimeFromUtc(_replayStartUtc.Value.ToUniversalTime(), czechTimeZone);
                    var endLocal = TimeZoneInfo.ConvertTimeFromUtc(_replayEndUtc.Value.ToUniversalTime(), czechTimeZone);
                    ReplayTimeLabel.Content = $"{startLocal:HH:mm:ss} - {endLocal:HH:mm:ss}";
                }
                else
                {
                    // fallback: old behavior
                    ReplayTimeLabel.Content = $"{playbackElapsedTime:hh\\:mm\\:ss} / {playbackMaxTime:hh\\:mm\\:ss}";
                }
            }
            catch { }
        }

        /// <summary>
        /// Handler for value changes on replay slider.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ReplaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_keyframes.Count == 0) return;

            int idx = (int)Math.Round(ReplaySlider.Value);
            idx = Math.Clamp(idx, 0, Math.Max(0, _keyframes.Count - 1));
            var t = _keyframes[idx];

            if (isReplaySliderDragging)
            {
                _playbackIndex = idx;
                playbackElapsedTime = t;
                UpdateReplayTimerLabel();
                UpdateTimerLabel();

                // Keep table in sync with current timeline while dragging
                SyncTramTableForReplay(t);

                return;
            }

            _playbackIndex = idx;
            playbackElapsedTime = t;
            playbackStartTime = DateTime.Now - playbackElapsedTime;
            RedrawPlaybackToTime(t);
            UpdateReplayTimerLabel();
            UpdateTimerLabel();

            UpdateReplayStatsForTime(t);
            SyncTramTableForReplay(t);
        }

        /// <summary>
        /// Handler
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ReplaySlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isReplaySliderDragging = true;
            wasPlayingBeforeReplayDrag = isPlaying && playbackTimer != null && playbackTimer.IsEnabled;
            playbackTimer?.Stop();
            e.Handled = false;
        }

        // Resume playback correctly after slider drag, including loop-from-end and step-back
        private void ReplaySlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            isReplaySliderDragging = false;

            if (_keyframes.Count == 0) { UpdateReplayTimerLabel(); UpdateTimerLabel(); return; }

            _playbackIndex = (int)Math.Round(ReplaySlider.Value);
            _playbackIndex = Math.Clamp(_playbackIndex, 0, Math.Max(0, _keyframes.Count - 1));
            var t = _keyframes[_playbackIndex];

            playbackElapsedTime = t;
            playbackStartTime = DateTime.Now - playbackElapsedTime;
            RedrawPlaybackToTime(t);
            UpdateReplayTimerLabel();
            UpdateTimerLabel();

            UpdateReplayStatsForTime(t);
            SyncTramTableForReplay(t);

            // If we were not playing before dragging, stay paused (do not auto-start)
            if (!wasPlayingBeforeReplayDrag)
            {
                isPlaying = false;
                _isPlaybackSessionActive = true; // keep session active while paused
                playbackTimer?.Stop();
                return;
            }

            // We were playing before drag -> resume from dropped position
            if (_playbackIndex >= _keyframes.Count - 1)
            {
                if (LoopCheckbox?.IsChecked == true && _keyframes.Count > 0)
                {
                    // loop to start and continue
                    _playbackIndex = 0;
                    var t0 = _keyframes[0];
                    playbackElapsedTime = t0;
                    playbackStartTime = DateTime.Now - t0;
                    RedrawPlaybackToTime(t0);
                    UpdateReplayTimerLabel();
                    ReplaySlider.Value = 0;
                }
                else if (_keyframes.Count > 1)
                {
                    // step one frame back so timer can advance
                    _playbackIndex = _keyframes.Count - 2;
                    var tPrev = _keyframes[_playbackIndex];
                    playbackElapsedTime = tPrev;
                    playbackStartTime = DateTime.Now - tPrev;
                    RedrawPlaybackToTime(tPrev);
                    UpdateReplayTimerLabel();
                    ReplaySlider.Value = _playbackIndex;
                }
                else
                {
                    // single-frame replay: nothing to play, remain paused at end
                    isPlaying = false;
                    _isPlaybackSessionActive = true;
                    playbackTimer?.Stop();
                    return;
                }
            }

            _isPlaybackSessionActive = true;
            isPlaying = true;

            if (playbackTimer == null)
            {
                playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                playbackTimer.Tick += PlaybackTimer_Tick;
            }
            if (!playbackTimer.IsEnabled)
                playbackTimer.Start();
        }

        private void ActivationZonesDataGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (e.Row?.Item is not ActivationZone zone) return;

            string? path = null;
            if (e.Column is DataGridTextColumn textCol && textCol.Binding is System.Windows.Data.Binding bTxt && bTxt.Path != null)
                path = bTxt.Path.Path;
            else if (e.Column is DataGridComboBoxColumn comboCol && comboCol.SelectedItemBinding is System.Windows.Data.Binding bSel && bSel.Path != null)
                path = bSel.Path.Path;

            if (path == nameof(ActivationZone.Color))
            {
                Dispatcher.BeginInvoke(new Action(() => ApplyZoneColor(zone)), DispatcherPriority.Background);
                Dispatcher.BeginInvoke(new Action(() => EmphasizeZoneWithOwnColor(zone, TimeSpan.FromMilliseconds(400))), DispatcherPriority.Background);
            }

            if (path is null) return;

            if (path == nameof(ActivationZone.MainZone) || path == nameof(ActivationZone.SubZone))
            {
                int val;
                if (e.EditingElement is TextBox tb)
                {
                    if (!int.TryParse(tb.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
                        return;
                }
                else if (e.EditingElement is ComboBox cb)
                {
                    if (cb.SelectedItem is int i) val = i;
                    else if (int.TryParse(cb.Text, out var parsed)) val = parsed;
                    else return;
                }
                else return;

                bool isSwitch = zone.IsSwitchZone;

                if (path == nameof(ActivationZone.MainZone))
                    zone.MainZone = Math.Clamp(val, 0, isSwitch ? 4 : 3);
                else
                    zone.SubZone = Math.Clamp(val, 0, isSwitch ? 6 : 4);

                return;
            }

            if (path == nameof(ActivationZone.Latitude) || path == nameof(ActivationZone.Longitude))
            {
                if (e.EditingElement is TextBox tb)
                {
                    var raw = tb.Text?.Trim() ?? "";
                    var norm = raw.Replace(',', '.');

                    if (path == nameof(ActivationZone.Latitude))
                    {
                        if (double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                            zone.Latitude = d;
                    }
                    else
                    {
                        if (double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                            zone.Longitude = d;
                    }
                }
                return;
            }

            if (path == nameof(ActivationZone.Color))
            {
                if (e.EditingElement is ComboBox cb)
                {
                    var chosen = cb.SelectedItem as string ?? cb.Text;
                    if (!string.IsNullOrWhiteSpace(chosen))
                        zone.Color = chosen;
                }

                var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;
                if (zone.Rectangle != null)
                {
                    zone.Rectangle.Stroke = brush;
                    if (!ReferenceEquals(_highlightedRect, zone.Rectangle))
                    {
                        zone.Rectangle.StrokeThickness = 2;
                        zone.Rectangle.Fill = Brushes.Transparent;
                    }
                }
                EmphasizeZoneWithOwnColor(zone, TimeSpan.FromMilliseconds(400));
            }

            if (e.EditAction != DataGridEditAction.Commit) return;

            zone = e.Row.Item as ActivationZone;
            if (zone == null) return;

            // Check if edited column is MainZone or SubZone
            var columnHeader = e.Column.Header?.ToString();
            if (columnHeader == "Main" || columnHeader == "Sub")
            {
                // Schedule name update after value is committed
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    zone.UpdateName();
                }), DispatcherPriority.DataBind);
            }
        }

        private void ActivationZonesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_highlightedRect != null)
            {
                try
                {
                    var prevZone = activationZones.Values
                        .FirstOrDefault(z => ReferenceEquals(z.Rectangle, _highlightedRect));

                    var correctBrush = prevZone != null
                        ? (TryBrushFromColor(prevZone.Color) ?? Brushes.Red)
                        : (_highlightedRectOldBrush ?? _highlightedRect.Stroke);

                    _highlightedRect.Stroke = correctBrush;

                    bool wasActive = prevZone?.IsActive == true;

                    _highlightedRect.StrokeThickness = wasActive
                        ? 6
                        : (_highlightedRectOldThickness > 0
                            ? _highlightedRectOldThickness
                            : 2);

                    if (_zoneArrows.TryGetValue(_highlightedRect, out var oldArrow))
                    {
                        oldArrow.Opacity = 0.20;
                        oldArrow.StrokeThickness = 0.5;
                        Panel.SetZIndex(oldArrow, 950);
                    }

                    Panel.SetZIndex(_highlightedRect, 100);
                }
                catch
                {
                }

                _highlightedRect = null;
                _highlightedRectOldBrush = null;
                _highlightedRectOldThickness = 0;
            }

            var zone = ActivationZonesDataGrid?.SelectedItem as ActivationZone;
            if (zone?.Rectangle == null) return;

            _highlightedRect = zone.Rectangle;
            _highlightedRectOldBrush = TryBrushFromColor(zone.Color) ?? zone.Rectangle.Stroke;
            _highlightedRectOldThickness = zone.IsActive ? 6 : 2;

            EmphasizeZoneWithOwnColor(zone, revertAfter: null);
        }

        private static SolidColorBrush? TryBrushFromColor(string colorHex)
        {
            try
            {
                var obj = new BrushConverter().ConvertFromString(colorHex);
                if (obj is SolidColorBrush b) return b;
            }
            catch { }
            return null;
        }

        private static SolidColorBrush MakeAlphaBrush(SolidColorBrush baseBrush, byte alpha = 60)
        {
            var c = baseBrush.Color;
            return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        }

        private void EmphasizeZoneWithOwnColor(
            ActivationZone zone,
            TimeSpan? revertAfter = null)
        {
            if (zone?.Rectangle == null)
                return;

            var rect = zone.Rectangle;
            var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;

            rect.Stroke = brush;

            bool isSelected = ReferenceEquals(_highlightedRect, rect);

            rect.StrokeThickness = 6;

            if (!isSelected)
                rect.Fill = MakeAlphaBrush((SolidColorBrush)brush, 40);

            // zvýraznění šipky
            EnsureZoneArrow(zone);

            if (_zoneArrows.TryGetValue(rect, out var arrow))
            {
                arrow.Fill = brush;
                arrow.Stroke = Brushes.Transparent;
                arrow.StrokeThickness = 3;
                arrow.Opacity = 1.0;

                Panel.SetZIndex(arrow, 1001);
            }

            if (revertAfter.HasValue && !isSelected)
            {
                var t = new DispatcherTimer
                {
                    Interval = revertAfter.Value
                };

                t.Tick += (s, e) =>
                {
                    ((DispatcherTimer)s!).Stop();

                    if (zone.Rectangle != null && !zone.IsActive)
                    {
                        zone.Rectangle.StrokeThickness = 2;
                        zone.Rectangle.Fill = Brushes.Transparent;

                        if (_zoneArrows.TryGetValue(
                            zone.Rectangle,
                            out var oldArrow))
                        {
                            var oldBrush =
                                TryBrushFromColor(zone.Color)
                                ?? Brushes.Gray;

                            oldArrow.Fill = oldBrush;
                            oldArrow.Stroke = oldBrush;
                            oldArrow.StrokeThickness = 0.5;
                            oldArrow.Opacity = 0.20;

                            Panel.SetZIndex(oldArrow, 950);
                        }
                    }
                };

                t.Start();
            }

            Panel.SetZIndex(rect, 200);
        }
        private void ResetStatusUi()
        {
            // counters
            camOkCount = 0;
            camErrorCount = 0;
            srvOkCount = 0;
            srvErrorCount = 0;

            // last message caches
            lastCamTimes.Clear();
            prevCamTimes.Clear();
            lastCamUpdates.Clear();
        }

        private static bool TryGetInterpolatedPosition(List<MovementFrame> frames, TimeSpan time, out Point pos, out MovementFrame? prevFrameOut)
        {
            pos = default;
            prevFrameOut = null;
            if (frames == null || frames.Count == 0) return false;

            MovementFrame prev = null, next = null;
            foreach (var f in frames)
            {
                if (f.Timestamp > time) { next = f; break; }
                prev = f;
            }

            if (prev == null && next != null)
            {
                pos = next.Position; // before first, show first
                prevFrameOut = next;
                return true;
            }
            if (next == null && prev != null)
            {
                pos = prev.Position; // after last, show last
                prevFrameOut = prev;
                return true;
            }
            if (prev != null && next != null)
            {
                double denom = (next.Timestamp - prev.Timestamp).TotalMilliseconds;
                double t = denom > 0 ? (time - prev.Timestamp).TotalMilliseconds / denom : 0;
                pos = new Point(
                    Lerp(prev.Position.X, next.Position.X, t),
                    Lerp(prev.Position.Y, next.Position.Y, t)
                );
                prevFrameOut = prev;
                return true;
            }
            return false;
        }

        private void UpdateOrCreateBox(Dictionary<string, Rectangle> store, string id, Point topCenter, Brush color, double headingDeg)
        {
            const double w = 15.0;
            const double h = 30.0;

            if (!store.TryGetValue(id, out var rect))
            {
                rect = new Rectangle
                {
                    Width = w,
                    Height = h,
                    Stroke = color,
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false
                };
                store[id] = rect;
                TileCanvas.Children.Add(rect);
                Panel.SetZIndex(rect, 998); // pod tečkou a textem, nad trail
            }

            // barvu drž podle tramvaje
            rect.Stroke = color;

            // pozice: horní střed obdélníku přesně v bodě tramvaje
            Canvas.SetLeft(rect, topCenter.X - w / 2.0);
            Canvas.SetTop(rect, topCenter.Y);

            // rotace okolo horního středu (CenterY=0), posun o +180°, aby "bod byl nahoře"
            double angle = (headingDeg + 180.0) % 360.0;
            rect.RenderTransform = new RotateTransform(angle, w / 2.0, 0.0);
        }

        private void UpdateOrCreateVehicleBox(string id, Point topCenter, Brush color, double headingDeg)
            => UpdateOrCreateBox(_vehicleBoxes, id, topCenter, color, headingDeg);

        private void UpdateOrCreateReplayBox(string id, Point topCenter, Brush color, double headingDeg)
            => UpdateOrCreateBox(_replayBoxes, id, topCenter, color, headingDeg);

        private static bool TryGetStepPosition(List<MovementFrame> frames, TimeSpan time, out MovementFrame? current, out MovementFrame? prev)
        {
            current = null;
            prev = null;
            if (frames == null || frames.Count == 0) return false;

            // Do NOT show anything before the first frame time
            if (time < frames[0].Timestamp)
                return false;

            int idx = -1;
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].Timestamp <= time) idx = i;
                else break;
            }

            if (idx < 0) return false;

            current = frames[idx];
            prev = idx > 0 ? frames[idx - 1] : null;
            return true;
        }

        private static bool AlmostEqual(double a, double b, double eps = 1e-6) => Math.Abs(a - b) <= eps;

        private bool ZoneAlreadyExists(string name, double lat, double lon, double widthMeters, double heightMeters, int azimuth, string color)
        {
            foreach (var z in ActivationZonesCollection)
            {
                if (!string.Equals(z.Name, name, StringComparison.Ordinal)) continue;
                if (!AlmostEqual(z.Latitude, lat)) continue;
                if (!AlmostEqual(z.Longitude, lon)) continue;
                if (!AlmostEqual(z.Width, widthMeters, 1e-3)) continue;   // meters
                if (!AlmostEqual(z.Height, heightMeters, 1e-3)) continue; // meters
                if (z.Azimuth != azimuth) continue;
                if (!string.Equals(z.Color, color, StringComparison.OrdinalIgnoreCase)) continue;
                return true;
            }
            return false;
        }

        private static bool SamePoint(Point a, Point b, double eps = 0.5) => Math.Abs(a.X - b.X) <= eps && Math.Abs(a.Y - b.Y) <= eps;

        private void ApplyZoneColor(ActivationZone zone)
        {
            if (zone?.Rectangle == null) return;

            var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;
            zone.Rectangle.Stroke = brush;

            // Pokud není zóna aktuálně zvýrazněná, vrať normální vzhled
            if (!ReferenceEquals(_highlightedRect, zone.Rectangle))
            {
                zone.Rectangle.StrokeThickness = 2;
                zone.Rectangle.Fill = Brushes.Transparent;
            }
        }

        private void ActivationZonesDataGrid_CurrentCellChanged(object? sender, EventArgs e)
        {
            if (ActivationZonesDataGrid?.CurrentItem is ActivationZone zone)
            {
                ApplyZoneColor(zone);
            }
        }

        private void TramTrailLengthTB_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Parse and clamp: at least 1 segment, at most 100 to be safe
            var text = TramTrailLengthTB?.Text?.Trim() ?? "";
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return;

            v = Math.Clamp(v, 1, 100);
            _maxTrailLength = v;

            // Trim existing live trails to new length
            foreach (var kv in activeVehicles.ToList())
            {
                var veh = kv.Value;
                if (veh?.MovementFrames != null)
                {
                    // keep at most (_maxTrailLength + 1) points -> _maxTrailLength segments
                    while (veh.MovementFrames.Count > _maxTrailLength + 1)
                        veh.MovementFrames.RemoveAt(0);
                }

                if (veh?.TrailDots != null)
                {
                    while (veh.TrailDots.Count > _maxTrailLength)
                    {
                        TileCanvas.Children.Remove(veh.TrailDots[0]);
                        veh.TrailDots.RemoveAt(0);
                    }
                }

                // Rebuild polyline
                var old = TileCanvas.Children.OfType<Polyline>()
                    .FirstOrDefault(pl => string.Equals(pl.Tag as string, $"tram_trail_{kv.Key}", StringComparison.OrdinalIgnoreCase));
                if (old != null) TileCanvas.Children.Remove(old);
                if (veh?.MovementFrames != null && veh.MovementFrames.Count > 1)
                {
                    var poly = new Polyline
                    {
                        Stroke = veh.Ellipse?.Fill ?? Brushes.Black,
                        StrokeThickness = 2,
                        Tag = $"tram_trail_{kv.Key}"
                    };
                    foreach (var mf in veh.MovementFrames) poly.Points.Add(mf.Position);
                    TileCanvas.Children.Add(poly);
                    Panel.SetZIndex(poly, 999);
                }
            }

            // Trim drawn tram trails to new length
            for (int i = 0; i < drawnTramTrailPoints?.Length; i++)
            {
                while (drawnTramTrailPoints[i]?.Count > _maxTrailLength + 1)
                    drawnTramTrailPoints[i]?.RemoveAt(0);

                while (drawnTramTrailGeoPoints[i]?.Count > _maxTrailLength + 1)
                    drawnTramTrailGeoPoints[i]?.RemoveAt(0);

                var tram = drawnTrams[i];
                if (tram?.TrailDots != null)
                {
                    while (tram.TrailDots.Count > _maxTrailLength)
                    {
                        TileCanvas.Children.Remove(tram.TrailDots[0]);
                        tram.TrailDots.RemoveAt(0);
                    }
                }

                if (drawnTramTrails[i] != null)
                {
                    drawnTramTrails[i]?.Points.Clear();
                    foreach (var p in drawnTramTrailPoints[i])
                        drawnTramTrails[i]?.Points.Add(p);
                }
            }

            // Redraw current replay visuals with new length
            RedrawPlaybackToTime(playbackElapsedTime);
        }

        // put near other small helpers
        private void UpdateSecondsSinceLastCamForReplay(TimeSpan t)
        {
            if (!_playbackLoaded) return;
            if (_replayFrames == null || _replayFrames.Count == 0) return;

            foreach (var row in TramTable.ToList())
            {
                var fullId = _replayFrames.Keys.FirstOrDefault(id => id != null && id.EndsWith(row.VehicleId));
                if (fullId == null) continue;
                if (!_replayFrames.TryGetValue(fullId, out var frames) || frames == null || frames.Count == 0)
                    continue;

                // last frame <= current replay time
                int idx = -1;
                for (int i = frames.Count - 1; i >= 0; i--)
                {
                    if (frames[i].Timestamp <= t) { idx = i; break; }
                }

                if (idx < 0)
                {
                    row.SecondsSinceLastCam = 0;
                    continue;
                }

                var delta = t - frames[idx].Timestamp;
                if (delta.TotalSeconds > TableRowTimeout.TotalSeconds)
                {
                    TramTable.Remove(row); // delete row: do not show timer beyond 60 s
                }
                else
                {
                    row.SecondsSinceLastCam = Math.Max(0, (int)Math.Floor(delta.TotalSeconds));
                }
            }
        }

        private bool TryFindNextCamTime(TimeSpan current, out TimeSpan next)
        {
            next = default;
            bool found = false;

            foreach (var frames in _replayFrames.Values)
            {
                if (frames == null || frames.Count == 0) continue;

                foreach (var f in frames)
                {
                    if (f.Timestamp > current && (!found || f.Timestamp < next))
                    {
                        next = f.Timestamp;
                        found = true;
                    }
                }
            }

            // Also include signal state change timestamps
            foreach (var sf in _replaySignalFrames)
            {
                if (sf.ts > current && (!found || sf.ts < next))
                {
                    next = sf.ts;
                    found = true;
                }
            }

            return found;
        }

        private bool TryFindPrevCamTime(TimeSpan current, out TimeSpan prev)
        {
            prev = default;
            bool found = false;

            foreach (var frames in _replayFrames.Values)
            {
                if (frames == null || frames.Count == 0) continue;

                foreach (var f in frames)
                {
                    if (f.Timestamp < current && (!found || f.Timestamp > prev))
                    {
                        prev = f.Timestamp;
                        found = true;
                    }
                }
            }

            // Also include signal state change timestamps
            foreach (var sf in _replaySignalFrames)
            {
                if (sf.ts < current && (!found || sf.ts > prev))
                {
                    prev = sf.ts;
                    found = true;
                }
            }

            return found;
        }

        private void SyncTramTableForReplay(TimeSpan t)
        {
            if (!_playbackLoaded || _replayFrames == null || _replayFrames.Count == 0) return;

            var keep = new HashSet<string>(StringComparer.Ordinal);

            var sel = TramBox?.SelectedItem as string;
            bool filtering = !string.IsNullOrEmpty(sel) && !string.Equals(sel, "All", StringComparison.OrdinalIgnoreCase);

            foreach (var kv in _replayFrames)
            {
                var fullId = kv.Key;
                var frames = kv.Value;
                if (frames == null || frames.Count == 0) continue;

                // If filtering and this id doesn't match, skip
                if (filtering && !IsReplayFilterMatch(fullId))
                    continue;

                // find last frame <= t
                int idx = -1;
                for (int i = frames.Count - 1; i >= 0; i--)
                {
                    if (frames[i].Timestamp <= t) { idx = i; break; }
                }
                if (idx < 0) continue;

                var lastTs = frames[idx].Timestamp;
                var age = t - lastTs;
                if (age.TotalSeconds > TableRowTimeout.TotalSeconds)
                    continue; // too old at this timeline position => don't keep row

                if (FilterReplayByAltitude(fullId, lastTs))
                    continue;

                string shortId = !string.IsNullOrEmpty(fullId) && fullId.Length > 4 ? fullId[^4..] : fullId;
                keep.Add(shortId);

                // LastCamTime shown in table: absolute local time derived from replay start + frame rel. time
                string camTimeStr;
                if (_replayStartUtc.HasValue)
                {
                    var absUtc = _replayStartUtc.Value.Add(lastTs);
                    var absLocal = TimeZoneInfo.ConvertTimeFromUtc(absUtc.ToUniversalTime(), czechTimeZone);
                    camTimeStr = absLocal.ToString("HH:mm:ss");
                }
                else
                {
                    camTimeStr = new DateTime(lastTs.Ticks).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                }

                double speed = 0.0;
                var key = $"{fullId}|{lastTs.Ticks}";
                if (_playbackSpeedByIdAndTs.TryGetValue(key, out var spd))
                    speed = spd; // m/s

                var row = TramTable.FirstOrDefault(r => r.VehicleId == shortId);
                if (row == null)
                {
                    TramTable.Add(new TramInfo
                    {
                        VehicleId = shortId,
                        Speed = speed,
                        LastCamTime = camTimeStr,
                        SecondsSinceLastCam = Math.Max(0, (int)Math.Floor(age.TotalSeconds)),
                        LastMessageTimestamp = null // not used in replay
                    });
                }
                else
                {
                    row.Speed = speed;
                    row.LastCamTime = camTimeStr;
                    row.SecondsSinceLastCam = Math.Max(0, (int)Math.Floor(age.TotalSeconds));
                }
            }

            // remove any row not valid at current timeline
            foreach (var row in TramTable.ToList())
            {
                if (!keep.Contains(row.VehicleId))
                    TramTable.Remove(row);
            }
        }

        private bool IsTramTableRowPresentForId(string fullId)
        {
            if (string.IsNullOrEmpty(fullId)) return false;
            string shortId = fullId.Length > 4 ? fullId[^4..] : fullId;
            return TramTable.Any(r => r.VehicleId == shortId);
        }

        private void ResetVehicleInstance(string id)
        {
            if (!activeVehicles.TryGetValue(id, out var vehicle)) return;
            RemoveVehicleCompletely(id, vehicle);
        }

        private void UpdateAltitudeLabelUI(double? meters)
        {
            try
            {
                if (AltitudeLabel == null) return;

                string text = meters.HasValue
                    ? $"Current elevation: {meters.Value:F0} m"
                    : "Current elevation: n/a";

                if (!Dispatcher.CheckAccess())
                    Dispatcher.Invoke(() => AltitudeLabel.Content = text);
                else
                    AltitudeLabel.Content = text;
            }
            catch { /* ignore */ }
        }

        private void FilterCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_playbackLoaded)
            {
                RedrawPlaybackToTime(playbackElapsedTime);
                SyncTramTableForReplay(playbackElapsedTime);
            }
        }

        // Returns the smallest missing sub-zone in 0..4 for given main zone (excluding 'current' row).
        // - If none missing (all 0..4 used), returns -1.
        private int ComputeExpectedSubZoneForMain(int mainZone, ActivationZone? current = null)
        {
            var used = ActivationZonesCollection
                .Where(z => !IsSwitchZone(z)) // only activation zones
                .Where(z => z.MainZone == mainZone && !ReferenceEquals(z, current))
                .Select(z => z.SubZone)
                .Where(i => i >= 0 && i <= 4)
                .Distinct()
                .OrderBy(i => i)
                .ToList();

            for (int i = 0; i <= 4; i++)
                if (!used.Contains(i)) return i;

            return -1;
        }

        private bool ValidateSubzoneContinuity(out string message)
        {
            var sb = new StringBuilder();
            for (int main = 0; main <= 3; main++)
            {
                var subs = ActivationZonesCollection
                    .Where(z => !IsSwitchZone(z)) // only activation zones
                    .Where(z => z.MainZone == main)
                    .Select(z => z.SubZone)
                    .Where(i => i >= 0 && i <= 4)
                    .OrderBy(i => i)
                    .ToList();

                if (subs.Count == 0) continue;
                if (subs.First() != 0)
                    sb.AppendLine($"Main zone {main}: first sub-zone must be 0.");

                var dup = subs.GroupBy(x => x).FirstOrDefault(g => g.Count() > 1);
                if (dup != null)
                    sb.AppendLine($"Main zone {main}: duplicate sub-zone {dup.Key}.");

                for (int i = 0; i < subs.Count; i++)
                {
                    if (subs[i] != i)
                    {
                        sb.AppendLine($"Main zone {main}: expected continuous sub-zones 0..{subs.Count - 1} without gaps.");
                        break;
                    }
                }
            }
            message = sb.ToString().Trim();
            return string.IsNullOrEmpty(message);
        }

        private bool ValidateMainZoneContinuity(out string message)
        {
            var sb = new StringBuilder();

            var used = ActivationZonesCollection
                .Where(z => !IsSwitchZone(z)) // only activation zones
                .Select(z => z.MainZone)
                .Where(i => i >= 0 && i <= 3)
                .Distinct()
                .OrderBy(i => i)
                .ToList();

            if (used.Count == 0)
            {
                message = string.Empty;
                return true;
            }

            if (used.First() != 0)
                sb.AppendLine("Main zones: first zone must be 0.");

            for (int i = 0; i < used.Count; i++)
            {
                if (used[i] != i)
                {
                    sb.AppendLine($"Main zones: expected 0..{used.Count - 1} without gaps.");
                    break;
                }
            }

            message = sb.ToString().Trim();
            return string.IsNullOrEmpty(message);
        }

        private void SetZonesLiveSorting(bool enabled)
        {
            var view = CollectionViewSource.GetDefaultView(ActivationZonesCollection);
            if (view is ICollectionViewLiveShaping live)
            {
                live.IsLiveSorting = enabled;
            }
        }

        private void ActivationZonesDataGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
        {
            _suspendZoneLiveSort = true;
            SetZonesLiveSorting(false);
        }

        private void ActivationZonesDataGrid_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;

            var item = e.Row?.Item;
            // Re-enable sorting after commit finishes, then refresh and keep selection visible
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _suspendZoneLiveSort = false;
                SetZonesLiveSorting(true);

                var view = CollectionViewSource.GetDefaultView(ActivationZonesCollection);
                view.Refresh();

                if (item != null)
                {
                    ActivationZonesDataGrid.SelectedItem = item;
                    ActivationZonesDataGrid.ScrollIntoView(item);
                }
            }), DispatcherPriority.Background);
        }

        public async Task CenterMapOnZonesAsync(
            IReadOnlyCollection<ActivationZone> zones)
        {
            if (zones == null || zones.Count == 0)
                return;

            var validZones = zones
                .Where(z =>
                    z != null &&
                    !double.IsNaN(z.Latitude) &&
                    !double.IsNaN(z.Longitude) &&
                    !double.IsInfinity(z.Latitude) &&
                    !double.IsInfinity(z.Longitude) &&
                    Math.Abs(z.Latitude) > 0.000001 &&
                    Math.Abs(z.Longitude) > 0.000001)
                .ToList();

            if (validZones.Count == 0)
                return;

            double minLatitude = validZones.Min(z => z.Latitude);
            double maxLatitude = validZones.Max(z => z.Latitude);
            double minLongitude = validZones.Min(z => z.Longitude);
            double maxLongitude = validZones.Max(z => z.Longitude);

            latitude = (minLatitude + maxLatitude) / 2.0;
            longitude = (minLongitude + maxLongitude) / 2.0;

            zoom = validZones.Count == 1
                ? 18
                : CalculateZoomForZoneBounds(
                    minLatitude,
                    maxLatitude,
                    minLongitude,
                    maxLongitude);

            double canvasWidth = TileCanvas.ActualWidth > 0
                ? TileCanvas.ActualWidth
                : CanvasSize;

            double canvasHeight = TileCanvas.ActualHeight > 0
                ? TileCanvas.ActualHeight
                : CanvasSize;

            double centerWorldX = LonToTileX(longitude, zoom) * TileSize;
            double centerWorldY = LatToTileY(latitude, zoom) * TileSize;

            cameraX = (int)Math.Round(centerWorldX - canvasWidth / 2.0);
            cameraY = (int)Math.Round(centerWorldY - canvasHeight / 2.0);

            _currentTopLeftTileX =
                (int)Math.Floor((double)cameraX / TileSize);

            _currentTopLeftTileY =
                (int)Math.Floor((double)cameraY / TileSize);

            tileOffsetX = cameraX - (_currentTopLeftTileX * TileSize);
            tileOffsetY = cameraY - (_currentTopLeftTileY * TileSize);

            UpdateCenterTextBoxesFromFields();

            await LoadTilesSmoothAsync(
                _currentTopLeftTileX,
                _currentTopLeftTileY,
                tileOffsetX,
                tileOffsetY);

            UpdateAllOverlaysLive();
            DrawRadiusCircle();
            await BringAllOverlaysToFrontSafeAsync();

            _ = EnsureLocalAreaAltitudeAsync(force: true);
        }

        private int CalculateZoomForZoneBounds(
            double minLatitude,
            double maxLatitude,
            double minLongitude,
            double maxLongitude)
        {
            double canvasWidth = TileCanvas.ActualWidth > 0
                ? TileCanvas.ActualWidth
                : CanvasSize;

            double canvasHeight = TileCanvas.ActualHeight > 0
                ? TileCanvas.ActualHeight
                : CanvasSize;

            const double padding = 100.0;

            double availableWidth = Math.Max(100.0, canvasWidth - padding);
            double availableHeight = Math.Max(100.0, canvasHeight - padding);

            for (int testZoom = 18; testZoom >= 3; testZoom--)
            {
                double leftTileX = LonToTileX(minLongitude, testZoom);
                double rightTileX = LonToTileX(maxLongitude, testZoom);
                double topTileY = LatToTileY(maxLatitude, testZoom);
                double bottomTileY = LatToTileY(minLatitude, testZoom);

                double widthInPixels =
                    Math.Abs(rightTileX - leftTileX) * TileSize;

                double heightInPixels =
                    Math.Abs(bottomTileY - topTileY) * TileSize;

                if (widthInPixels <= availableWidth &&
                    heightInPixels <= availableHeight)
                {
                    return testZoom;
                }
            }

            return 3;
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine($"[EXPORT] Export requested.");
            Console.WriteLine($"[EXPORT] Switch mode: {IsSwitchMode()}");
            Console.WriteLine($"[EXPORT] Zones count: WLC={ActivationZonesCollection.Count(z => !z.IsSwitchZone)}, RTV={ActivationZonesCollection.Count(z => z.IsSwitchZone)}");

            // Do not open export when there are no activation zones
            if (ActivationZonesCollection == null || ActivationZonesCollection.Count == 0)
            {
                MessageBox.Show("No activation zones to export. Please add at least one activation zone.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // commit pending edits first
            ActivationZonesDataGrid?.CommitEdit(DataGridEditingUnit.Cell, true);
            ActivationZonesDataGrid?.CommitEdit(DataGridEditingUnit.Row, true);

            var sb = new StringBuilder();

            // 1) validate main zones 0..N
            if (!ValidateMainZoneContinuity(out var mainErr) && !string.IsNullOrWhiteSpace(mainErr))
                sb.AppendLine(mainErr);

            // 2) validate subzones per main zone
            if (!ValidateSubzoneContinuity(out var subErr) && !string.IsNullOrWhiteSpace(subErr))
                sb.AppendLine(subErr);

            var errors = sb.ToString().Trim();

            if (!string.IsNullOrEmpty(errors))
            {
                MessageBox.Show(
                    "The order of zones is n" +
                    "ot valid:\n\n" + errors,
                    "Checking zones",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return; // block export
            }

            // Open export window (only when zones exist and validation passes)
            var dlg = new ExportWindow { Owner = this };
            dlg.Title = "Export Activation Zones";
            dlg.SetReadMode(false); // Make sure it's in export mode
            dlg.ShowDialog();
        }

        private (int main, int sub) GetNextZoneIndices()
        {
            // Fill 0,0..0,4 then 1,0..1,4 among activation zones only
            for (int main = 0; ; main++)
            {
                var used = ActivationZonesCollection
                    .Where(z => !IsSwitchZone(z))
                    .Where(z => z.MainZone == main)
                    .Select(z => z.SubZone)
                    .Where(i => i >= 0 && i <= 4)
                    .ToHashSet();

                for (int sub = 0; sub <= 4; sub++)
                {
                    if (!used.Contains(sub))
                        return (main, sub);
                }
            }
        }

        private (int main, int sub) GetNextSwitchZoneIndices()
        {
            for (int main = 0; main <= 4; main++)
            {
                var used = ActivationZonesCollection
                    .Where(z => IsSwitchZone(z))
                    .Where(z => z.MainZone == main)
                    .Select(z => z.SubZone)
                    .Where(i => i >= 0 && i <= 6)
                    .ToHashSet();

                for (int sub = 0; sub <= 6; sub++)
                {
                    if (!used.Contains(sub))
                        return (main, sub);
                }
            }

            int totalSwitchZones = ActivationZonesCollection.Count(z => IsSwitchZone(z));

            int nextMain = totalSwitchZones / 7;
            int nextSub = totalSwitchZones % 7;

            return (nextMain, nextSub);
        }

        private void ReadButtonMain_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine($"[READ] Read from MPC requested.");
            Console.WriteLine($"[READ] Current mode: {(IsSwitchMode() ? "RTV (Switches)" : "WLC (Zones)")}");

            // Open ExportWindow in read mode
            var dlg = new ExportWindow { Owner = this };

            // Configure window for read mode
            dlg.Title = "Read Activation Zones";
            dlg.SetReadMode(true);

            // Show the dialog
            dlg.ShowDialog();
        }

    }
}
