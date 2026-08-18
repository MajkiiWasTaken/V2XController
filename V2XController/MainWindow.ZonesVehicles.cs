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
    // Map clearing, stop/vehicle filters, zone visuals and collections
    public partial class MainWindow
    {
        public async Task ClearMapAndTable()
        {
            try
            {
                // Remove any drawn activation rectangles from the map
                if (ActivationZonesCollection != null)
                {
                    foreach (var z in ActivationZonesCollection.ToList())
                    {
                        var rect = z?.Rectangle;
                        if (rect != null)
                        {
                            if (rect.Parent is System.Windows.Controls.Panel parent)
                                parent.Children.Remove(rect);
                            z.Rectangle = null;
                        }
                    }

                    ActivationZonesCollection.Clear();
                }

                // Also clear the backing activation dictionary
                if (activationZones.Count > 0)
                {
                    foreach (var kv in activationZones.Keys.ToList())
                    {
                        if (kv != null && kv.Parent is System.Windows.Controls.Panel parent)
                            parent.Children.Remove(kv);
                    }
                    activationZones.Clear();
                }

                // Remove any drawn switch rectangles from the map
                if (SwitchZonesCollection != null)
                {
                    foreach (var z in SwitchZonesCollection.ToList())
                    {
                        var rect = z?.Rectangle;
                        if (rect != null)
                        {
                            if (rect.Parent is System.Windows.Controls.Panel parent)
                                parent.Children.Remove(rect);
                            z.Rectangle = null;
                        }
                    }

                    SwitchZonesCollection.Clear();
                }

                // Also clear the backing switch dictionary
                if (switchZones.Count > 0)
                {
                    foreach (var kv in switchZones.Keys.ToList())
                    {
                        if (kv != null && kv.Parent is System.Windows.Controls.Panel parent)
                            parent.Children.Remove(kv);
                    }
                    switchZones.Clear();
                }

                // *** NOVĚ: Clear polyline zones and visual elements ***
                if (PolylineZonesCollection != null)
                {
                    PolylineZonesCollection.Clear();
                }

                if (_polylineRows != null)
                {
                    _polylineRows.Clear();
                }

                // Remove all polyline visual elements from canvas
                if (_polylineToSegmentZones != null)
                {
                    _polylineToSegmentZones.Clear();
                }

                if (_polylineToSegments != null)
                {
                    foreach (var segments in _polylineToSegments.Values)
                    {
                        foreach (var segment in segments)
                        {
                            if (segment?.Parent is System.Windows.Controls.Panel parent)
                                parent.Children.Remove(segment);
                        }
                    }
                    _polylineToSegments.Clear();
                }

                // Remove polyline vertices (dots)
                if (_polylineVertexMap != null)
                {
                    foreach (var vertex in _polylineVertexMap.Keys.ToList())
                    {
                        if (vertex?.Parent is System.Windows.Controls.Panel parent)
                            parent.Children.Remove(vertex);
                    }
                    _polylineVertexMap.Clear();
                }

                if (_polylineVertexToCircle != null)
                {
                    foreach (var circle in _polylineVertexToCircle.Values)
                    {
                        if (circle?.Parent is System.Windows.Controls.Panel parent)
                            parent.Children.Remove(circle);
                    }
                    _polylineVertexToCircle.Clear();
                }

                // Remove the actual polyline paths
                if (_polylineGeoPoints != null)
                {
                    foreach (var polyline in _polylineGeoPoints.Keys.ToList())
                    {
                        if (polyline?.Parent is System.Windows.Controls.Panel parent)
                            parent.Children.Remove(polyline);
                    }
                    _polylineGeoPoints.Clear();
                }

                // Remove polyline visual zone outlines/fills
                if (_polylineVisualGroups != null)
                {
                    foreach (var groupList in _polylineVisualGroups.Values)
                    {
                        foreach (var path in groupList.ToList())
                        {
                            if (path?.Parent is Panel parent)
                                parent.Children.Remove(path);
                        }
                    }

                    _polylineVisualGroups.Clear();
                }

                _segmentToVisualPath?.Clear();

                if (_segmentToCircles != null)
                {
                    foreach (var circles in _segmentToCircles.Values)
                    {
                        foreach (var circle in circles.ToList())
                        {
                            if (circle?.Parent is Panel parent)
                                parent.Children.Remove(circle);
                        }
                    }

                    _segmentToCircles.Clear();
                }

                ClearPolylineDirectionArrows();

                // Clear current drawing state
                currentPolyline = null;
                polylinePoints?.Clear();
                polylineVertexDots?.Clear();
                _currentPolylineCircles?.Clear();
                _currentPolylineSegments?.Clear();
                _currentPolylineCircleGeoPoints?.Clear();
                _isDrawingPolyline = false;
                _polylineCommittedPointsCount = 0;

                // Reset selection/highlight if any
                if (_highlightedRect != null)
                {
                    try
                    {
                        _highlightedRect.Stroke = _highlightedRectOldBrush ?? _highlightedRect.Stroke;
                        _highlightedRect.StrokeThickness = _highlightedRectOldThickness > 0 ? _highlightedRectOldThickness : _highlightedRect.StrokeThickness;
                        Panel.SetZIndex(_highlightedRect, 100);
                    }
                    catch { }
                    _highlightedRect = null;
                    _highlightedRectOldBrush = null;
                    _highlightedRectOldThickness = 0;
                }
                selectedElement = null;
                selectedRectangle = null;

                // Optional UI refresh
                try
                {
                    ReprojectActivationZonesOnMapChange();
                    ReprojectSwitchZonesOnMapChange();
                    await BringAllOverlaysToFrontSafeAsync();
                }
                catch { }
            }
            catch
            {
                // best-effort clear
            }
        }

        // near other helpers
        public bool IsSwitchModeSelected => SwitchRadio?.IsChecked == true;

        // Replace OpenTerminal_Click body

        private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0; // meters
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }

        // Called for each CAM message after position updated
        private void CheckStopArrivalsDepartures(V2XMessage cam)
        {
            if (stops == null || stops.Count == 0) return;
            if (cam == null || string.IsNullOrWhiteSpace(cam.VehicleID)) return;

            // find nearest stop
            Stop? nearest = null;
            double best = double.MaxValue;
            foreach (var s in stops)
            {
                double d = HaversineMeters(cam.Latitude ?? 0.0, cam.Longitude ?? 0.0, s.Latitude, s.Longitude);
                if (d < best) { best = d; nearest = s; }
            }

            _vehCurrentStop.TryGetValue(cam.VehicleID, out var current);

            // inside radius now?
            bool insideNow = nearest != null && best <= StopRadiusMeters;

            // arrive
            if (insideNow && !ReferenceEquals(current, nearest))
            {
                // if we were at a different stop, treat as depart previous first
                if (current != null)
                {
                    string prevName = string.IsNullOrWhiteSpace(current.StopName) ? "(bez názvu)" : current.StopName;
                }

                string name = string.IsNullOrWhiteSpace(nearest.StopName) ? "(bez názvu)" : nearest.StopName;
                _vehCurrentStop[cam.VehicleID] = nearest;
                return;
            }

            // depart (was inside before, now outside)
            if (!insideNow && current != null)
            {
                string name = string.IsNullOrWhiteSpace(current.StopName) ? "(bez názvu)" : current.StopName;
                _vehCurrentStop[cam.VehicleID] = null;
            }
        }

        private void PopulateTramBoxFromIds(IEnumerable<string?> fullIds)
        {
            if (TramBox == null) return;

            // Extract last-4, dedupe
            var ids = fullIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Length > 4 ? id[^4..] : id)
                .Distinct()
                .ToList();

            // Partition numeric vs non-numeric so we can sort numerically when possible
            var numeric = new List<int>();
            var numericMap = new Dictionary<int, string>(); // preserve original 0-padded string
            var nonNumeric = new List<string>();

            foreach (var s in ids)
            {
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    // store numeric value and keep zero-padding for display
                    numeric.Add(n);
                    numericMap[n] = s.PadLeft(4, '0');
                }
                else
                {
                    nonNumeric.Add(s);
                }
            }

            numeric.Sort();
            nonNumeric.Sort(StringComparer.Ordinal);

            var sorted = new List<string>();
            // "All" must be first element
            sorted.Add("All");

            foreach (var n in numeric)
                sorted.Add(numericMap[n]);

            foreach (var s in nonNumeric)
                sorted.Add(s);

            Dispatcher.Invoke(() =>
            {
                TramBox.Items.Clear();
                foreach (var item in sorted)
                    TramBox.Items.Add(item);

                if (TramBox.Items.Count > 0)
                    TramBox.SelectedIndex = 0;
            });
        }

        private bool IsReplayFilterMatch(string fullId)
        {
            if (string.IsNullOrEmpty(fullId)) return false;
            if (TramBox == null) return true; // no UI to filter by

            var sel = TramBox.SelectedItem as string;
            if (string.IsNullOrEmpty(sel) || string.Equals(sel, "All", StringComparison.OrdinalIgnoreCase))
                return true;

            // compare last 4 digits
            var shortId = fullId.Length > 4 ? fullId[^4..] : fullId;
            return string.Equals(shortId, sel, StringComparison.Ordinal);
        }

        private void TramBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Apply immediately: when a replay is loaded we want to filter visuals + table.
            ApplyReplayFilter();
        }

        private void ApplyReplayFilter()
        {
            // Get current selection
            var sel = TramBox?.SelectedItem as string;
            bool filtering = !string.IsNullOrEmpty(sel) && !string.Equals(sel, "All", StringComparison.OrdinalIgnoreCase);

            // Table-level filter (applies immediately for live/tracked rows)
            var view = CollectionViewSource.GetDefaultView(TramTable);
            if (view != null)
            {
                if (!filtering)
                    view.Filter = null;
                else
                    view.Filter = o =>
                    {
                        if (o is TramInfo t)
                            return string.Equals(t.VehicleId, sel, StringComparison.Ordinal);
                        return false;
                    };
                Dispatcher.Invoke(() => view.Refresh());
            }

            // Decide which activeVehicles to remove (non-SRV) when filtering: collect keys first
            var keysToRemove = new List<string>();
            if (activeVehicles != null && filtering)
            {
                foreach (var kv in activeVehicles)
                {
                    var id = kv.Key;
                    var mp = kv.Value;
                    if (mp == null) continue;

                    // Always keep SRV
                    bool isSrv = string.Equals(mp.Ellipse?.Tag as string, "Srv", StringComparison.OrdinalIgnoreCase);
                    if (isSrv) continue;

                    // If this live vehicle doesn't match the selected filter, remove it now
                    if (!IsReplayFilterMatch(id))
                        keysToRemove.Add(id);
                }
            }

            // Remove collected active vehicles on UI thread (use BeginInvoke to avoid nested blocking)
            foreach (var id in keysToRemove)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (activeVehicles.TryGetValue(id, out var vehicle))
                    {
                        RemoveVehicleCompletely(id, vehicle); // this already runs UI removal internally
                    }
                }));
            }

            // For remaining live vehicles ensure visibility state (if not removing)
            Dispatcher.Invoke(() =>
            {
                if (activeVehicles != null)
                {
                    foreach (var kv in activeVehicles)
                    {
                        var id = kv.Key;
                        var mp = kv.Value;
                        if (mp == null) continue;
                        bool isSrv = string.Equals(mp.Ellipse?.Tag as string, "Srv", StringComparison.OrdinalIgnoreCase);
                        bool match = !filtering || isSrv || IsReplayFilterMatch(id);

                        if (mp.Ellipse != null) mp.Ellipse.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                        if (mp.Text != null) mp.Text.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                        if (mp.Speed != null) mp.Speed.Visibility = match ? Visibility.Visible : Visibility.Collapsed;

                        // Trails for live vehicles (polylines) toggle
                        var trails = TileCanvas.Children.OfType<Polyline>()
                            .Where(pl => pl.Tag is string s && s.Equals($"tram_trail_{id}", StringComparison.OrdinalIgnoreCase));
                        foreach (var pl in trails) pl.Visibility = match ? Visibility.Visible : Visibility.Collapsed;

                        // vehicle boxes
                        if (_vehicleBoxes.TryGetValue(id, out var box))
                            box.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    }
                }

                // Drawn/manual trams: hide/show according to selection
                for (int i = 0; i < drawnTrams?.Length; i++)
                {
                    var tram = drawnTrams[i];
                    if (tram == null) continue;
                    bool match = !filtering || IsReplayFilterMatch(tram.Label ?? string.Empty);
                    if (tram.Ellipse != null) tram.Ellipse.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (tram.Text != null) tram.Text.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (drawnTramTrails?[i] != null) drawnTramTrails[i].Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (tram.TrailDots != null)
                    {
                        foreach (var d in tram.TrailDots)
                            d.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    }
                }

                // Replay visuals: keep previous behavior (hide/show, do not delete replay state)
                if (_replayVehicles != null)
                {
                    foreach (var kv in _replayVehicles)
                    {
                        var id = kv.Key;
                        var mp = kv.Value;
                        if (mp == null) continue;
                        bool match = !filtering || IsReplayFilterMatch(id);
                        if (mp.Ellipse != null) mp.Ellipse.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                        if (mp.Text != null) mp.Text.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                        if (mp.Speed != null) mp.Speed.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                        if (mp.TrailDots != null)
                        {
                            foreach (var d in mp.TrailDots)
                                d.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                        }

                        var trails = TileCanvas.Children.OfType<Polyline>()
                            .Where(pl => pl.Tag is string s && s.Equals($"replay_trail_{id}", StringComparison.OrdinalIgnoreCase));
                        foreach (var pl in trails) pl.Visibility = match ? Visibility.Visible : Visibility.Collapsed;

                        if (_replayBoxes.TryGetValue(id, out var box))
                            box.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            });

            // If classic replay loaded, refresh replay visuals & table to reflect selection
            if (_playbackLoaded)
            {
                RedrawPlaybackToTime(playbackElapsedTime);
                SyncTramTableForReplay(playbackElapsedTime);
            }
        }

        private void AccuracyCB_Checked(object sender, RoutedEventArgs e)
        {
            // When checkbox changes we either remove all accuracy circles (unchecked)
            // or trigger a redraw of replay visuals so circles appear immediately (checked).
            Dispatcher.Invoke(() =>
            {
                bool isChecked = AccuracyCB?.IsChecked == true;

                if (!isChecked)
                {
                    var accs = TileCanvas.Children.OfType<Ellipse>()
                        .Where(el => el.Tag is string s &&
                                     (s.StartsWith("live_acc_", StringComparison.OrdinalIgnoreCase) ||
                                      s.StartsWith("replay_acc_", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    foreach (var el in accs) TileCanvas.Children.Remove(el);
                }
                else
                {
                    if (_playbackLoaded)
                    {
                        RedrawPlaybackToTime(playbackElapsedTime);
                    }

                }
            });
        }

        private void RemoveReplayAccuracyEllipse(string id)
        {
            if (string.IsNullOrEmpty(id) || TileCanvas == null) return;
            var accEllipses = TileCanvas.Children
                .OfType<Ellipse>()
                .Where(e => e.Tag is string s && s.Equals($"replay_acc_{id}", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var e in accEllipses) TileCanvas.Children.Remove(e);
        }

        private void FilterTram_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressFilterTramSelectionChanged) return;
            ApplyLiveFilter();
        }

        private void PopulateLiveTramBoxFromActiveVehicles()
        {
            try
            {
                if (FilterTram == null) return;

                // Build list from currently active vehicles only (last 4 digits)
                var ids = activeVehicles?.Keys
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Select(k => k.Length > 4 ? k[^4..] : k)
                    .Distinct(StringComparer.Ordinal)
                    .ToList() ?? new List<string>();

                // Partition numeric vs non-numeric so we can sort numerically when possible
                var numeric = new List<int>();
                var numericMap = new Dictionary<int, string>();
                var nonNumeric = new List<string>();

                foreach (var s in ids)
                {
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    {
                        if (!numericMap.ContainsKey(n))
                            numericMap[n] = s.PadLeft(4, '0');
                        numeric.Add(n);
                    }
                    else
                    {
                        nonNumeric.Add(s);
                    }
                }

                numeric.Sort();
                nonNumeric.Sort(StringComparer.Ordinal);

                var sorted = new List<string> { "All" };
                foreach (var n in numeric.Distinct()) sorted.Add(numericMap[n]);
                foreach (var s in nonNumeric.Distinct()) sorted.Add(s);

                void UpdateUi()
                {
                    try
                    {
                        _suppressFilterTramSelectionChanged = true;
                        try
                        {
                            var prevSel = FilterTram.SelectedItem as string;
                            FilterTram.Items.Clear();
                            foreach (var it in sorted) FilterTram.Items.Add(it);

                            if (!string.IsNullOrEmpty(prevSel) && FilterTram.Items.Contains(prevSel))
                                FilterTram.SelectedItem = prevSel;
                            else if (FilterTram.Items.Count > 0)
                                FilterTram.SelectedIndex = 0;
                        }
                        finally
                        {
                            _suppressFilterTramSelectionChanged = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[POPULATE] PopulateLiveTramBoxFromActiveVehicles UI update failed: {ex.Message}");
                        _suppressFilterTramSelectionChanged = false;
                    }
                }

                if (FilterTram.Dispatcher.CheckAccess())
                    UpdateUi();
                else
                    FilterTram.Dispatcher.BeginInvoke((Action)UpdateUi);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[POPULATE] PopulateLiveTramBoxFromActiveVehicles failed: {ex.Message}");
            }
        }

        private void ApplyLiveFilter()
        {
            var sel = FilterTram?.SelectedItem as string;
            bool filtering = !string.IsNullOrEmpty(sel) && !string.Equals(sel, "All", StringComparison.OrdinalIgnoreCase);

            // update TramTable view to reflect live-only selection
            var view = CollectionViewSource.GetDefaultView(TramTable);
            if (view != null)
            {
                if (!filtering)
                    view.Filter = null;
                else
                    view.Filter = o =>
                    {
                        if (o is TramInfo t)
                            return string.Equals(t.VehicleId, sel, StringComparison.Ordinal);
                        return false;
                    };
                Dispatcher.Invoke(() => view.Refresh());
            }

            // If no filtering (All), nothing to remove. New CAMs will repopulate.
            if (!filtering)
            {
                // refresh live combobox content from active vehicles
                PopulateLiveTramBoxFromActiveVehicles();
                return;
            }

            // Otherwise, remove all active non-matching tram visuals (preserve SRV)
            var keysToRemove = new List<string>();
            foreach (var kv in activeVehicles)
            {
                var id = kv.Key;
                var mp = kv.Value;
                if (mp == null) continue;

                bool isSrv = string.Equals(mp.Ellipse?.Tag as string, "Srv", StringComparison.OrdinalIgnoreCase);
                if (isSrv) continue;

                var shortId = id.Length > 4 ? id[^4..] : id;
                if (!string.Equals(shortId, sel, StringComparison.Ordinal))
                    keysToRemove.Add(id);
            }

            // remove on UI thread
            foreach (var id in keysToRemove)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (activeVehicles.TryGetValue(id, out var vehicle))
                    {
                        RemoveVehicleCompletely(id, vehicle);
                    }
                }));
            }

        }

        private void RemoveOrphanZoneRectangles()
        {
            if (TileCanvas == null) return;

            // Collect rectangles on canvas that are zone-like (tags used by code)
            var zoneRects = TileCanvas.Children.OfType<Rectangle>()
                .Where(r =>
                {
                    if (r.Tag == null) return false;
                    var t = r.Tag.ToString();
                    return string.Equals(t, "DrawnRectangle", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(t, "SwitchZone", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            foreach (var rect in zoneRects)
            {
                try
                {
                    // Remove from canvas
                    if (TileCanvas.Children.Contains(rect))
                        TileCanvas.Children.Remove(rect);
                }
                catch { /* best effort */ }

                // Remove from activationZones dictionary if present
                try
                {
                    if (activationZones.ContainsKey(rect))
                        activationZones.Remove(rect);
                }
                catch { }

                // Remove from switchZones dictionary if present
                try
                {
                    if (switchZones.ContainsKey(rect))
                        switchZones.Remove(rect);
                }
                catch { }

                // Remove any MapRectangle wrapper referencing this Rectangle
                try
                {
                    var mr = mapRectangles.FirstOrDefault(m => m?.Shape == rect);
                    if (mr != null)
                        mapRectangles.Remove(mr);
                }
                catch { }
            }
        }

        /// <summary>
        /// Rotate the currently selected activation rectangle (if any) by deltaDegrees.
        /// Updates the ActivationZone.Azimuth and reapplies the visual rotation + positioning.
        /// </summary>
        private void RotateSelectedActivationZone(int deltaDegrees)
        {
            try
            {
                ActivationZone? zone = null;
                Rectangle? rect = null;

                // Prefer rectangle currently selected by selection logic
                if (selectedElement is Rectangle selRect && activationZones.TryGetValue(selRect, out var mappedZone))
                {
                    rect = selRect;
                    zone = mappedZone;
                }
                // Fallback: use selectedRectangle field if set
                else if (selectedRectangle != null && activationZones.TryGetValue(selectedRectangle, out mappedZone))
                {
                    rect = selectedRectangle;
                    zone = mappedZone;
                }

                if (zone == null || zone.Rectangle == null)
                    return;

                // Update azimuth and normalize to [0..359]
                int newAz = (zone.Azimuth + deltaDegrees) % 360;
                if (newAz < 0) newAz += 360;
                zone.Azimuth = newAz;

                // Reposition/rotate visually around start point
                UpdateRectanglePositionFromStartPoint(zone);

                isDirty = true;
                Console.WriteLine($"[ROTATE] Zone '{zone.Name}' rotated to {zone.Azimuth}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ROTATE] Error rotating zone: {ex.Message}");
            }
        }

        private Polygon CreateZoneArrow()
        {
            var p = new Polygon
            {
                Opacity = 0.20,
                StrokeThickness = 0.5,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            Panel.SetZIndex(p, 950);
            return p;
        }

        private void EnsureZoneArrow(ActivationZone zone)
        {
            if (zone?.Rectangle == null) return;
            var rect = zone.Rectangle;

            if (!_zoneArrows.TryGetValue(rect, out var arrow))
            {
                arrow = CreateZoneArrow();
                _zoneArrows[rect] = arrow;
                if (!TileCanvas.Children.Contains(arrow))
                    TileCanvas.Children.Add(arrow);
            }

            double left = Canvas.GetLeft(rect);
            double top = Canvas.GetTop(rect);
            double w = rect.Width;
            double h = rect.Height;
            if (double.IsNaN(left) || double.IsNaN(top) || w <= 0 || h <= 0) return;

            double arrowW = Math.Clamp(w * 0.14, 8.0, 36.0);
            double arrowH = Math.Clamp(h * 0.14, 8.0, 36.0);

            arrow.Points = new PointCollection
            {
                new Point(arrowW * 0.5, 0),
                new Point(arrowW, arrowH * 0.86),
                new Point(arrowW * 0.66, arrowH * 0.86),
                new Point(arrowW * 0.66, arrowH * 1.62),
                new Point(arrowW * 0.34, arrowH * 1.62),
                new Point(arrowW * 0.34, arrowH * 0.86),
                new Point(0, arrowH * 0.86)
            };

            double arrowLeft = left + (w - arrowW) / 2.0;
            double arrowTop = top + Math.Max(4.0, h * 0.06);

            var brush = TryBrushFromColor(zone.Color) ?? Brushes.Gray;
            arrow.Fill = brush;
            arrow.Stroke = brush;

            // The arrow should rotate around the same center as the rectangle (bottom center of rect)
            double rectCenterX = left + w / 2.0;
            double rectCenterY = top + h;

            // Calculate arrow's own center for its local rotation
            double arrowCenterX = arrowW / 2.0;
            double arrowCenterY = arrowH / 2.0;

            // Create rotation around rectangle's rotation center
            var rotateTransform = new RotateTransform(
                zone.Azimuth,
                rectCenterX - arrowLeft,  // offset from arrow's left to rect's center
                rectCenterY - arrowTop    // offset from arrow's top to rect's center
            );

            arrow.RenderTransform = rotateTransform;
            Canvas.SetLeft(arrow, arrowLeft);
            Canvas.SetTop(arrow, arrowTop);
        }

        /// <summary>
        /// Remove arrow associated with rectangle (call before removing rectangle).
        /// </summary>
        private void RemoveZoneArrow(Rectangle? rect)
        {
            if (rect == null) return;
            if (_zoneArrows.TryGetValue(rect, out var arrow))
            {
                if (TileCanvas.Children.Contains(arrow))
                    TileCanvas.Children.Remove(arrow);
                _zoneArrows.Remove(rect);
            }
        }

        private void ActivationZonesCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (ActivationZone zone in e.OldItems)
                {
                    try
                    {
                        zone.PropertyChanged -= ActivationZone_PropertyChanged;

                        if (zone.Rectangle != null)
                        {
                            RemoveZoneArrow(zone.Rectangle);

                            if (TileCanvas.Children.Contains(zone.Rectangle))
                                TileCanvas.Children.Remove(zone.Rectangle);

                            if (selectedElement == zone.Rectangle)
                                DeselectElement();

                            activationZones.Remove(zone.Rectangle);
                        }

                        isDirty = true;
                    }
                    catch { }
                }
            }

            // Reset => remove any rectangles that no longer have a backing zone in the collection
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var kvp in activationZones.ToList())
                {
                    if (!ActivationZonesCollection.Contains(kvp.Value))
                    {
                        RemoveZoneArrow(kvp.Key);

                        if (TileCanvas.Children.Contains(kvp.Key))
                            TileCanvas.Children.Remove(kvp.Key);
                        activationZones.Remove(kvp.Key);
                    }
                }

                RemoveOrphanZoneRectangles();
            }

            // Added zones => create rectangles like XML load and wire everything
            if (e.NewItems != null)
            {
                double mpp = MetersPerPixel(latitude, zoom);

                foreach (ActivationZone zone in e.NewItems)
                {
                    if (zone == null) continue;

                    if (_polylineRows.Contains(zone))
                    {
                        // Polyline segmenty NEMAJÍ Rectangle - jen se zapíší do tabulky
                        zone.PropertyChanged += ActivationZone_PropertyChanged;
                        continue;
                    }

                    // Ensure color
                    var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;

                    // Compute rectangle px size from meters
                    double widthPx = zone.Width > 0 ? zone.Width / mpp : 0;
                    double heightPx = zone.Height > 0 ? zone.Height / mpp : 0;
                    if (widthPx <= 0 || heightPx <= 0)
                    {
                        continue;
                    }

                    // Create the Rectangle if missing
                    if (zone.Rectangle == null)
                    {
                        var rect = new Rectangle
                        {
                            Stroke = brush,
                            StrokeThickness = 2,
                            Fill = Brushes.Transparent,
                            Tag = "DrawnRectangle",
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

                        rect.MouseEnter += Rectangle_MouseEnter;
                        rect.MouseLeave += Rectangle_MouseLeave;
                        rect.MouseLeftButtonDown += Rectangle_MouseLeftButtonDown;
                        Panel.SetZIndex(rect, 100);

                        zone.PropertyChanged += ActivationZone_PropertyChanged;

                        try { EnsureZoneArrow(zone); } catch { }

                        isDirty = true;
                    }
                    else
                    {
                        zone.Rectangle.Width = widthPx;
                        zone.Rectangle.Height = heightPx;

                        var (sx, sy) = ConvertLatLonToCanvasXY(zone.Latitude, zone.Longitude);
                        zone.StartPoint = new Point(sx, sy);

                        UpdateRectanglePositionFromStartPoint(zone);
                        ApplyZoneRotation(zone);
                        UpdateActivationZoneBounds(zone);

                        if (!TileCanvas.Children.Contains(zone.Rectangle))
                            TileCanvas.Children.Add(zone.Rectangle);
                        activationZones[zone.Rectangle] = zone;
                        Panel.SetZIndex(zone.Rectangle, 100);

                        zone.PropertyChanged += ActivationZone_PropertyChanged;

                        try { EnsureZoneArrow(zone); } catch { }
                    }
                }
            }

            UpdateUiEnabledState();
        }

        private void SwitchZonesCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Removed rows
            if (e.OldItems != null)
            {
                foreach (ActivationZone zone in e.OldItems)
                {
                    try
                    {
                        zone.PropertyChanged -= ActivationZone_PropertyChanged;
                        if (zone.Rectangle != null)
                        {
                            if (TileCanvas.Children.Contains(zone.Rectangle))
                                TileCanvas.Children.Remove(zone.Rectangle);
                            if (selectedElement == zone.Rectangle)
                                DeselectElement();

                            if (switchZones.ContainsKey(zone.Rectangle))
                                switchZones.Remove(zone.Rectangle);
                        }
                        isDirty = true;
                    }
                    catch { }
                }
            }

            // Reset action: ensure canvas rectangles match collection
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var kvp in switchZones.ToList())
                {
                    if (!SwitchZonesCollection.Contains(kvp.Value))
                    {
                        if (TileCanvas.Children.Contains(kvp.Key))
                            TileCanvas.Children.Remove(kvp.Key);
                        switchZones.Remove(kvp.Key);
                    }
                }

                // Also remove orphan zone rectangles from canvas (switch-tagged or drawn)
                RemoveOrphanZoneRectangles();
            }

            // Added rows -> create rectangles
            if (e.NewItems != null)
            {
                double mpp = MetersPerPixel(latitude, zoom);

                foreach (ActivationZone zone in e.NewItems)
                {
                    if (zone == null) continue;

                    var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;

                    double widthPx = zone.Width > 0 ? zone.Width / mpp : 0;
                    double heightPx = zone.Height > 0 ? zone.Height / mpp : 0;
                    if (widthPx <= 0 || heightPx <= 0)
                        continue; // wait until valid sizes

                    if (zone.Rectangle == null)
                    {
                        var rect = new Rectangle
                        {
                            Stroke = brush,
                            StrokeThickness = 2,
                            Fill = Brushes.Transparent,
                            Tag = "SwitchZone",
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
                        switchZones[rect] = zone;

                        rect.MouseEnter += Rectangle_MouseEnter;
                        rect.MouseLeave += Rectangle_MouseLeave;
                        rect.MouseLeftButtonDown += Rectangle_MouseLeftButtonDown;
                        Panel.SetZIndex(rect, 100);

                        zone.PropertyChanged += ActivationZone_PropertyChanged;
                        isDirty = true;
                    }
                    else
                    {
                        zone.Rectangle.Width = widthPx;
                        zone.Rectangle.Height = heightPx;

                        var (sx, sy) = ConvertLatLonToCanvasXY(zone.Latitude, zone.Longitude);
                        zone.StartPoint = new Point(sx, sy);

                        UpdateRectanglePositionFromStartPoint(zone);
                        ApplyZoneRotation(zone);
                        UpdateActivationZoneBounds(zone);

                        if (!TileCanvas.Children.Contains(zone.Rectangle))
                            TileCanvas.Children.Add(zone.Rectangle);
                        switchZones[zone.Rectangle] = zone;
                        Panel.SetZIndex(zone.Rectangle, 100);

                        zone.PropertyChanged += ActivationZone_PropertyChanged;
                    }
                }
            }

            UpdateUiEnabledState();
        }

        private void SilentClearAll()
        {
            // Run on UI thread
            Dispatcher.Invoke(() =>
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
                    else if (child is Ellipse ellipse)
                    {
                        if (ellipse.Tag == null || (ellipse.Tag.ToString() != "Tram" && ellipse.Tag.ToString() != "Srv"))
                            elementsToRemove.Add(child);
                    }
                    else if (child is TextBlock textBlock)
                    {
                        if (textBlock.Tag == null || (textBlock.Tag.ToString() != "Tram" && textBlock.Tag.ToString() != "Srv" && textBlock.Tag.ToString() != "Signal"))
                            elementsToRemove.Add(child);
                    }
                }

                foreach (var el in elementsToRemove)
                {
                    try { if (TileCanvas.Children.Contains(el)) TileCanvas.Children.Remove(el); } catch { }
                }

                // Remove non-tram points
                try
                {
                    points.RemoveAll(p => p.Ellipse == null || p.Ellipse.Tag == null || (p.Ellipse.Tag.ToString() != "Tram" && p.Ellipse.Tag.ToString() != "Srv"));
                }
                catch { }

                // Clear map rectangles and activation/switch dictionaries + collections
                try
                {
                    foreach (var kv in activationZones.Keys.ToList())
                    {
                        try { if (kv != null && TileCanvas.Children.Contains(kv)) TileCanvas.Children.Remove(kv); } catch { }
                    }
                    activationZones.Clear();
                    ActivationZonesCollection.Clear();
                }
                catch { }

                try
                {
                    foreach (var kv in switchZones.Keys.ToList())
                    {
                        try { if (kv != null && TileCanvas.Children.Contains(kv)) TileCanvas.Children.Remove(kv); } catch { }
                    }
                    switchZones.Clear();
                    SwitchZonesCollection.Clear();
                }
                catch { }

                // Clear other state similar to ClearButton_Click
                try { mapRectangles.Clear(); } catch { }
                try { connectionLine.Points.Clear(); } catch { }
                try { TramTable.Clear(); } catch { }

                // Clear drawn manual trams and trails
                try
                {
                    for (int i = 0; i < drawnTrams?.Length; i++)
                    {
                        // Replace the block that accessed arrays directly with null- and bounds-checked version
                        if (drawnTrams != null && i >= 0 && i < drawnTrams.Length)
                        {
                            var tram = drawnTrams[i];
                            if (tram != null)
                            {
                                try { if (tram.Ellipse != null) TileCanvas.Children.Remove(tram.Ellipse); } catch { }
                                try { if (tram.Text != null) TileCanvas.Children.Remove(tram.Text); } catch { }
                                try { if (tram.Speed != null) TileCanvas.Children.Remove(tram.Speed); } catch { }

                                if (tram.TrailDots != null)
                                {
                                    foreach (var d in tram.TrailDots.ToList())
                                        try { TileCanvas.Children.Remove(d); } catch { }
                                    tram.TrailDots.Clear();
                                }
                            }
                        }

                        if (drawnTramTrails != null && i >= 0 && i < drawnTramTrails.Length && drawnTramTrails[i] != null)
                        {
                            try { TileCanvas.Children.Remove(drawnTramTrails[i]); } catch { }
                            drawnTramTrails[i] = null;
                        }

                        if (drawnTramTrailPoints != null && i >= 0 && i < drawnTramTrailPoints.Length && drawnTramTrailPoints[i] != null)
                            drawnTramTrailPoints[i]?.Clear();

                        if (drawnTramTrailGeoPoints != null && i >= 0 && i < drawnTramTrailGeoPoints.Length)
                            drawnTramTrailGeoPoints[i].Clear();

                        if (drawnTramLat != null && i >= 0 && i < drawnTramLat.Length)
                            drawnTramLat[i] = null;

                        if (drawnTramLon != null && i >= 0 && i < drawnTramLon.Length)
                            drawnTramLon[i] = null;

                        if (drawnTrams != null && i >= 0 && i < drawnTrams.Length)
                            drawnTrams[i] = null;
                    }
                }
                catch { }

                // Reset drawing state like ClearButton_Click
                isDirty = true;
                rectPhase = RectangleDrawPhase.None;
                isDrawing = false;
                isSelectionMode = true;
                currentDrawingMode = DrawingMode.Point;
                UpdateHitTestForSelectableElements();
            });
        }

    }
}
