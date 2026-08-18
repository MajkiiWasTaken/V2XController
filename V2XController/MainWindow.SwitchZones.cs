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
    // Switch-zone UI, validation and reprojection
    public partial class MainWindow
    {
        public void InitSwitchZonesUi()
        {
            // live-sort for the collection (independent from the grid)
            var view = CollectionViewSource.GetDefaultView(SwitchZonesCollection);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(ActivationZone.MainZone), ListSortDirection.Ascending));
            view.SortDescriptions.Add(new SortDescription(nameof(ActivationZone.SubZone), ListSortDirection.Ascending));
            if (view is ICollectionViewLiveShaping live)
            {
                live.IsLiveSorting = true;
                live.LiveSortingProperties.Add(nameof(ActivationZone.MainZone));
                live.LiveSortingProperties.Add(nameof(ActivationZone.SubZone));
            }

            // Wire collection changes
            SwitchZonesCollection.CollectionChanged += SwitchZonesCollection_CollectionChanged;

            // Optional: only if a grid named "SwitchZonesDataGrid" exists in XAML
            if (this.FindName("SwitchZonesDataGrid") is DataGrid grid)
            {
                grid.SelectionChanged += SwitchZonesDataGrid_SelectionChanged;
                grid.BeginningEdit += SwitchZonesDataGrid_BeginningEdit;
                grid.RowEditEnding += SwitchZonesDataGrid_RowEditEnding;
                grid.CellEditEnding += SwitchZonesDataGrid_CellEditEnding;
                grid.CurrentCellChanged += SwitchZonesDataGrid_CurrentCellChanged;
                grid.PreviewKeyDown += SwitchZonesDataGrid_PreviewKeyDown; // safe, handler exists below
            }
        }

        private void SwitchZonesDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _pendingNewSwitchZone != null)
            {
                var grid = sender as DataGrid;
                grid?.CommitEdit(DataGridEditingUnit.Cell, true);
                grid?.CommitEdit(DataGridEditingUnit.Row, true);
                TryFinalizePendingNewSwitchZone();
                e.Handled = true;
            }
        }

        private void NewSwitchRow_Click(object sender, RoutedEventArgs e)
        {
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

            _pendingNewSwitchZone = zone;
            SwitchZonesCollection.Add(zone);

            if (this.FindName("SwitchZonesDataGrid") is DataGrid grid)
            {
                Dispatcher.BeginInvoke(new Action(() => { FocusCellInGrid(grid, zone, 0); }), DispatcherPriority.Background);
            }
        }

        private bool TryFinalizePendingNewSwitchZone()
        {
            var zone = _pendingNewSwitchZone;
            if (zone == null) return false;

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
                    "The new switch zone is missing required properties.\n\n" +
                    "Required: Name, Latitude, Longitude, Azimuth (0–359), Width (>0), Height (>0).",
                    "Incomplete zone",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var grid = this.FindName("SwitchZonesDataGrid") as DataGrid;
                    if (grid == null) return;

                    if (string.IsNullOrWhiteSpace(zone.Name)) { FocusCellInGrid(grid, zone, 0); return; }
                    if (double.IsNaN(zone.Latitude)) { FocusCellInGrid(grid, zone, 1); return; }
                    if (double.IsNaN(zone.Longitude)) { FocusCellInGrid(grid, zone, 2); return; }
                    if (zone.Azimuth < 0 || zone.Azimuth > 359) { FocusCellInGrid(grid, zone, 3); return; }
                    if (zone.Width <= 0) { FocusCellInGrid(grid, zone, 4); return; }
                    if (zone.Height <= 0) { FocusCellInGrid(grid, zone, 5); return; }
                }), DispatcherPriority.Background);
                return true;
            }

            if (zone.Rectangle != null)
            {
                _pendingNewSwitchZone = null;
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
            if (!SwitchZonesCollection.Contains(zone))
                SwitchZonesCollection.Add(zone);

            rect.MouseEnter += Rectangle_MouseEnter;
            rect.MouseLeave += Rectangle_MouseLeave;
            rect.MouseLeftButtonDown += Rectangle_MouseLeftButtonDown;
            Panel.SetZIndex(rect, 100);

            isDirty = true;
            _pendingNewSwitchZone = null;
            return true;
        }

        private void SwitchZonesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_highlightedRect != null)
            {
                try
                {
                    var prevZone = switchZones.Values.FirstOrDefault(z => ReferenceEquals(z.Rectangle, _highlightedRect))
                                ?? activationZones.Values.FirstOrDefault(z => ReferenceEquals(z.Rectangle, _highlightedRect));

                    var correctBrush = prevZone != null
                        ? (TryBrushFromColor(prevZone.Color) ?? Brushes.Red)
                        : (_highlightedRectOldBrush ?? _highlightedRect.Stroke);

                    _highlightedRect.Stroke = correctBrush;

                    bool wasActive = prevZone?.IsActive == true;
                    _highlightedRect.StrokeThickness = wasActive ? 6 : (_highlightedRectOldThickness > 0 ? _highlightedRectOldThickness : 2);

                    Panel.SetZIndex(_highlightedRect, 100);
                }
                catch { }
                _highlightedRect = null;
                _highlightedRectOldBrush = null;
                _highlightedRectOldThickness = 0;
            }

            var grid = sender as DataGrid;
            var zone = grid?.SelectedItem as ActivationZone;
            if (zone?.Rectangle == null) return;

            _highlightedRect = zone.Rectangle;
            _highlightedRectOldBrush = TryBrushFromColor(zone.Color) ?? zone.Rectangle.Stroke;
            _highlightedRectOldThickness = zone.IsActive ? 6 : 2;

            EmphasizeZoneWithOwnColor(zone, revertAfter: null);
        }

        private void SwitchZonesDataGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
        {
            _suspendSwitchZoneLiveSort = true;
            SetSwitchZonesLiveSorting(false);
        }

        private void SwitchZonesDataGrid_RowEditEnding(object? sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;

            var item = e.Row?.Item;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _suspendSwitchZoneLiveSort = false;
                SetSwitchZonesLiveSorting(true);

                var view = CollectionViewSource.GetDefaultView(SwitchZonesCollection);
                view.Refresh();

                if (item != null)
                {
                    var grid = this.FindName("SwitchZonesDataGrid") as DataGrid;
                    if (grid != null)
                    {
                        grid.SelectedItem = item;
                        grid.ScrollIntoView(item);
                    }
                }
            }), DispatcherPriority.Background);
        }

        private void SwitchZonesDataGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            if (e.Row?.Item is not ActivationZone zone) return;

            // which property?
            string? path = null;
            if (e.Column is DataGridTextColumn textCol && textCol.Binding is Binding bTxt && bTxt.Path != null)
                path = bTxt.Path.Path;
            else if (e.Column is DataGridComboBoxColumn comboCol && comboCol.SelectedItemBinding is Binding bSel && bSel.Path != null)
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

                if (path == nameof(ActivationZone.MainZone))
                    zone.MainZone = Math.Clamp(val, 0, 4); // 0..4
                else
                    zone.SubZone = Math.Clamp(val, 0, 6);  // 0..6

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
        }

        private void SwitchZonesDataGrid_CurrentCellChanged(object? sender, EventArgs e)
        {
            var grid = sender as DataGrid;
            if (grid?.CurrentItem is ActivationZone zone)
            {
                ApplyZoneColor(zone);
            }
        }

        private void SetSwitchZonesLiveSorting(bool enabled)
        {
            var view = CollectionViewSource.GetDefaultView(SwitchZonesCollection);
            if (view is ICollectionViewLiveShaping live)
            {
                live.IsLiveSorting = enabled;
            }
        }

        // Helper: focus cell in given grid
        private void FocusCellInGrid(DataGrid grid, ActivationZone zone, int columnIndex)
        {
            if (grid == null) return;
            columnIndex = Math.Clamp(columnIndex, 0, grid.Columns.Count - 1);

            grid.UpdateLayout();
            grid.ScrollIntoView(zone);
            grid.SelectedItem = zone;
            grid.CurrentCell = new DataGridCellInfo(zone, grid.Columns[columnIndex]);
            grid.BeginEdit();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var cellContent = grid.CurrentCell.Column?.GetCellContent(zone);
                if (cellContent != null)
                    System.Windows.Input.Keyboard.Focus(cellContent);
            }), DispatcherPriority.Input);
        }

        // Reproject switch-zone rectangles after pan/zoom/refresh
        public void ReprojectSwitchZonesOnMapChange()
        {
            // Reproject all switch zones from geo -> canvas, using per-zone latitude for MPP
            foreach (var zone in SwitchZonesCollection.Where(z => z != null))
            {
                if (zone.Rectangle == null) continue;

                double mppLocal = MetersPerPixel(zone.Latitude, zoom);
                double widthPx = zone.Width / mppLocal;
                double heightPx = zone.Height / mppLocal;

                zone.Rectangle.Width = widthPx;
                zone.Rectangle.Height = heightPx;

                var (sx, sy) = ConvertLatLonToCanvasXY(zone.Latitude, zone.Longitude);
                zone.StartPoint = new Point(sx, sy);

                UpdateRectanglePositionFromStartPoint(zone);
                UpdateActivationZoneBounds(zone);

                Panel.SetZIndex(zone.Rectangle, 100);
                if (!TileCanvas.Children.Contains(zone.Rectangle))
                    TileCanvas.Children.Add(zone.Rectangle);
            }
        }

        /// <summary>
        /// Revalidates whether a zone should remain active based on current vehicle positions.
        /// Deactivates the zone if no tracked vehicle is currently inside it.
        /// </summary>
        private void RevalidateZoneActiveState(ActivationZone zone)
        {
            if (!zone.IsActive) return;

            bool anyVehicleInside = false;

            foreach (var kvp in _vehicleActiveZones)
            {
                if (!kvp.Value.Contains(zone)) continue;

                if (!activeVehicles.TryGetValue(kvp.Key, out var vehicle)) continue;

                var pos = vehicle.Position;
                if (zone.Bounds.Contains(pos) && IsPointInRotatedRectangle(pos, zone))
                {
                    anyVehicleInside = true;

                    if (_zoneDeactivateTimers.TryGetValue(zone, out var existingTimer))
                    {
                        existingTimer.Stop();
                        existingTimer.Start();
                    }
                    break;
                }
            }

            if (!anyVehicleInside)
            {
                if (_zoneDeactivateTimers.TryGetValue(zone, out var t))
                {
                    t.Stop();
                    _zoneDeactivateTimers.Remove(zone);
                }

                zone.IsActive = false;
                if (zone.Rectangle != null)
                    zone.Rectangle.StrokeThickness = 2;

                foreach (var kvp in _vehicleActiveZones)
                    kvp.Value.Remove(zone);
            }
        }

        private void ZoneRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (currentDrawingMode == DrawingMode.Rectangle && rectPhase != RectangleDrawPhase.None)
                _drawToSwitchZones = false;

            if (!_suppressModeSwitch)
                _ = ClearMapAndTable();

            ClearPolylineDirectionArrows();
            UpdateUiEnabledState();
        }

        private void SwitchRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (currentDrawingMode == DrawingMode.Rectangle && rectPhase != RectangleDrawPhase.None)
                _drawToSwitchZones = true;

            if (!_suppressModeSwitch)
                _ = ClearMapAndTable();

            ClearPolylineDirectionArrows();
            UpdateUiEnabledState();
        }

    }
}
