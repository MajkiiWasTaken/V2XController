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
    // Map, tiles, zoom, pan and coordinate conversion
    public partial class MainWindow
    {
        // ===== METHODS FOR THE MAP =====

        /// <summary>
        /// Converts latitude and longitude to tile X/Y coordinates for a given zoom level.
        /// </summary>
        /// <param name="lat">Latitude in degrees</param>
        /// <param name="lon">Longitude in degrees</param>
        /// <param name="zoom">Zoom level</param>
        private static (int tileX, int tileY) LatLonToTileXY(double lat, double lon, int zoom)
        {
            int tileX = (int)Math.Floor((lon + 180.0) / 360.0 * (1 << zoom));
            double latRad = lat * Math.PI / 180.0;
            int tileY = (int)Math.Floor(
                (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * (1 << zoom)
            );
            return (tileX, tileY);
        }

        /// <summary>
        /// Converts latitude and longitude to canvas X/Y coordinates based on the current camera position and zoom level.
        /// </summary>
        /// <param name="lat">Latitude in degrees</param>
        /// <param name="lon">Longitude in degrees</param>
        /// <returns>Canvas X/Y coordinates</returns>
        public (double x, double y) ConvertLatLonToCanvasXY(double? lat, double? lon)
        {
            if (!lat.HasValue || !lon.HasValue)
                return (0, 0);

            // Convert lat/lon to fractional tile coordinates
            double u = LonToTileX(lon.Value, zoom);
            double v = LatToTileY(lat.Value, zoom);

            // Convert tile coords to world pixels
            double worldX = u * TileSize;
            double worldY = v * TileSize;

            // Subtract camera position to get canvas coordinates
            double canvasX = worldX - cameraX;
            double canvasY = worldY - cameraY;

            return (canvasX, canvasY);
        }

        /// <summary>
        /// Converts canvas pixels to Latitude and longitude based on the current camera position and zoom level.
        /// </summary>
        /// <param name="x">Canvas X coordinate</param>
        /// <param name="y">Canvas Y coordinate</param>
        /// <param name="zoom">Zoom level</param>
        /// <returns>Latitude and longitude in degrees</returns>
        public (double Latitude, double Longitude) ConvertCanvasXYToLatLon(double x, double y, int zoom)
        {
            var lonlat = CanvasPixelsToLatLon(new Point(x, y), latitude, longitude, zoom);
            return (lonlat.Y, lonlat.X);

        }

        /// <summary>
        /// Loads map tiles smoothly with optional offsets.
        /// </summary>
        /// <param name="startX">Starting tile X coordinate</param>
        /// <param name="startY">Starting tile Y coordinate</param>
        /// <param name="offsetX">Optional X offset in pixels</param>
        /// <param name="offsetY">Optional Y offset in pixels</param>
        private async Task LoadTilesSmoothAsync(int startX, int startY, double offsetX = 0, double offsetY = 0)
        {
            Console.WriteLine($"[TILES] Start: tile=({startX}, {startY}), offset=({offsetX:F1}, {offsetY:F1})");

            _tileCts?.Cancel();
            _tileCts?.Dispose();
            _tileCts = new CancellationTokenSource();
            var ct = _tileCts.Token;

            try
            {
                _currentTopLeftTileX = startX;
                _currentTopLeftTileY = startY;

                cameraX = startX * TileSize + (int)Math.Round(offsetX);
                cameraY = startY * TileSize + (int)Math.Round(offsetY);

                Console.WriteLine($"[TILES] Camera set to: ({cameraX}, {cameraY})");

                isDrawing = false;
                currentRect = null;
                _currentMapRectangle = null;

                RenderTilesProgressive();

                await Task.Delay(100);

                if (!ct.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        await ReprojectAllZonesOnMapChange();
                        ReprojectDrawnTramsOnMapChange();
                        ReprojectActiveVehiclesOnMapChange();
                        ReprojectReplayOnMapChange();
                        //DrawStopsOnCanvasSafe();
                    });

                    await BringAllOverlaysToFrontSafeAsync();
                }

                Console.WriteLine($"[TILES] Complete");
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine($"[TILES] Cancelled");
            }
        }

        /// <summary>
        /// Fetches a map tile asynchronously, memory safe.
        /// </summary>
        /// <param name="z">Zoom level</param>
        /// <param name="x">Tile X coordinate</param>
        /// <param name="y">Tile Y coordinate</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>BitmapSource of the tile or null if fetch failed</returns>
        private async Task<BitmapSource?> FetchTileAsync(int z, int x, int y, CancellationToken ct)
        {
            // formula for all tiles is 2^Z * 2^Z where Z is the zoom level

            if (_tileCache.TryGetValue((z, x, y), out var cached))
                return cached;

            string url = $"https://tile.openstreetmap.org/{z}/{x}/{y}.png";
            try
            {
                using var resp = await s_httpClient.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;               // load into memory, free stream
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.StreamSource = stream;
                bmp.DecodePixelWidth = TilePixelSize;
                bmp.DecodePixelHeight = TilePixelSize;
                bmp.EndInit();
                bmp.Freeze(); // cross-thread safe

                AddToCache((z, x, y), bmp);

                return bmp;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Tile fetch failed {url}: {ex.Message}");
                return null; // draw nothing for this tile
            }
        }

        /// <summary>
        /// Adds tiles to cache and ensures the cache size is under control by trimming old entries if necessary.
        /// </summary>
        /// <param name="key">The key of the tile (zoom, x, y).</param>
        /// <param name="bmp">The BitmapSource of the tile.</param>
        private void AddToCache((int z, int x, int y) key, BitmapSource bmp)
        {
            if (_tileCache.Count > MaxCachedTiles)
            {
                _tileCache.Clear();
            }

            _tileCache[key] = bmp;
        }

        /// <summary>
        /// Memory safe tile trimming: removes tiles that are not in the current view and keeps cache size under control.
        /// </summary>
        /// <param name="currentTiles">Set of tiles that are currently in view and should be kept in the cache.</param>
        private void TrimTileCache(HashSet<(int z, int x, int y)> currentTiles)
        {
            const int MaxCachedTiles = 2000;

            if (_tileCache.Count <= MaxCachedTiles)
                return;

            var keysToRemove = _tileCache.Keys
                .Where(key => !currentTiles.Contains(key))
                .ToList();

            foreach (var key in keysToRemove)
            {
                _tileCache.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Creates an Image control for a map tile with a fade-in animation.
        /// </summary>
        /// <param name="bmp">BitmapSource of the tile</param>
        /// <returns>Image control with the tile</returns>
        private Image CreateTileImage(BitmapSource? bmp)
        {
            var img = new Image
            {
                Width = TilePixelSize,
                Height = TilePixelSize,
                Opacity = 0,
                Stretch = Stretch.Fill,
                Source = bmp
            };

            // fade-in
            var fade = new DoubleAnimation(0.0, 1.0, new System.Windows.Duration(TimeSpan.FromMilliseconds(250)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            img.BeginAnimation(UIElement.OpacityProperty, fade);

            return img;
        }

        /// <summary>
        /// Generates a list of tile offsets in a spiral order.
        /// </summary>
        /// <param name="count">Number of tiles in one dimension</param>
        /// <returns>List of tile offsets</returns>
        private List<(int offX, int offY)> GenerateSpiralOrder(int count)
        {
            int n = count;
            var list = new List<(int, int)>();
            int center = n / 2;
            var distances = new List<((int, int) pos, int dist)>();

            for (int x = 0; x < n; x++)
                for (int y = 0; y < n; y++)
                    distances.Add(((x - center, y - center), Math.Abs(x - center) + Math.Abs(y - center)));

            // sort by manhattan distance, then by maybe Euclidean (stable)
            foreach (var item in distances.OrderBy(d => d.dist).ThenBy(d => d.pos.Item1 * d.pos.Item1 + d.pos.Item2 * d.pos.Item2))
                list.Add(item.pos);

            return list;
        }

        /// <summary>
        /// Handles the mouse wheel event on the tile canvas.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse wheel event arguments.</param>
        private void TileCanvas_MouseWheel(object sender, MouseWheelEventArgs e) { }

        /// <summary>
        /// Ensures local area altitude is resolved and stored. 
        /// Uses caching to avoid redundant API calls for nearby locations. 
        /// Updates the altitude label in the UI accordingly.
        /// </summary>
        /// <param name="force">Whether to force a refresh of the altitude data.</param>
        private async Task EnsureLocalAreaAltitudeAsync(bool force = false)
        {
            double lat, lon;

            if (srvLatitude.HasValue && srvLongitude.HasValue)
            {
                lat = srvLatitude.Value;
                lon = srvLongitude.Value;
            }
            else
            {
                lat = latitude;
                lon = longitude;
            }

            if (!force && _localAltitudeFor.HasValue)
            {
                var prev = _localAltitudeFor.Value;
                if (Math.Abs(prev.lat - lat) < 1e-6 && Math.Abs(prev.lon - lon) < 1e-6 && _localAltitudeMeters.HasValue)
                {
                    UpdateAltitudeLabelUI(_localAltitudeMeters); // keep label in sync
                    return;
                }
            }

            var alt = await QueryAltitudeAsync(lat, lon);
            if (alt.HasValue)
            {
                _localAltitudeFor = (lat, lon);
                _localAltitudeMeters = alt.Value;
                Console.WriteLine($"[ALT] Local baseline altitude set to {alt.Value:F1} m (lat={lat:F6}, lon={lon:F6})");
                UpdateAltitudeLabelUI(_localAltitudeMeters);
            }
            else
            {
                Console.WriteLine("[ALT] Failed to resolve baseline altitude (all providers).");
                // Do not overwrite a previously resolved value
                if (!_localAltitudeMeters.HasValue)
                    UpdateAltitudeLabelUI(null);
            }
        }

        /// <summary>
        /// Queries the altitude for the specified latitude and longitude.
        /// </summary>
        /// <param name="lat">The latitude of the location.</param>
        /// <param name="lon">The longitude of the location.</param>
        /// <returns>The altitude in meters, or null if the query fails.</returns>
        private static async Task<double?> QueryAltitudeAsync(double lat, double lon)
        {
            // Provider 1: OpenTopodata (SRTM90m)
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var url = $"https://api.opentopodata.org/v1/srtm90m?locations={lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)}";
                using var resp = await client.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    using var doc = await JsonDocument.ParseAsync(stream);
                    var status = doc.RootElement.TryGetProperty("status", out var stEl) ? stEl.GetString() : null;
                    if (string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) &&
                        doc.RootElement.TryGetProperty("results", out var results) &&
                        results.ValueKind == JsonValueKind.Array &&
                        results.GetArrayLength() > 0)
                    {
                        var el = results[0];
                        if (el.TryGetProperty("elevation", out var elevProp) && elevProp.ValueKind == JsonValueKind.Number)
                            return elevProp.GetDouble();
                    }
                    else
                    {
                        Console.WriteLine($"[ALT] OpenTopodata status: {status ?? "n/a"}");
                    }
                }
                else
                {
                    Console.WriteLine($"[ALT] OpenTopodata HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ALT] OpenTopodata error: {ex.Message}");
            }

            // Provider 2: Open‑Meteo elevation fallback
            try
            {
                using var client2 = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var url2 = $"https://api.open-meteo.com/v1/elevation?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}";
                using var resp2 = await client2.GetAsync(url2);
                if (resp2.IsSuccessStatusCode)
                {
                    using var stream2 = await resp2.Content.ReadAsStreamAsync();
                    using var doc2 = await JsonDocument.ParseAsync(stream2);

                    if (doc2.RootElement.TryGetProperty("elevation", out var arr) &&
                        arr.ValueKind == JsonValueKind.Array &&
                        arr.GetArrayLength() > 0 &&
                        arr[0].ValueKind == JsonValueKind.Number)
                    {
                        return arr[0].GetDouble();
                    }
                }
                else
                {
                    Console.WriteLine($"[ALT] Open‑Meteo HTTP {(int)resp2.StatusCode} {resp2.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ALT] Open‑Meteo error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Extracts the altitude from a CAM XML string.
        /// </summary>
        /// <param name="rawXml">The raw CAM XML string.</param>
        /// <param name="altitudeMeters">The extracted altitude in meters.</param>
        /// <returns>True if the altitude was successfully extracted; otherwise, false.</returns>
        private static bool TryExtractAltitudeFromCamXml(string rawXml, out double altitudeMeters)
        {
            altitudeMeters = 0;
            try
            {
                int vehPtStart = rawXml.IndexOf("<vehPt", StringComparison.OrdinalIgnoreCase);
                if (vehPtStart < 0) return false;
                int tagEnd = rawXml.IndexOf('>', vehPtStart);
                if (tagEnd < 0) return false;

                var tag = rawXml.Substring(vehPtStart, tagEnd - vehPtStart);
                int altIdx = tag.IndexOf("alt=\"", StringComparison.OrdinalIgnoreCase);
                if (altIdx < 0) return false;

                int vStart = altIdx + 5;
                int vEnd = tag.IndexOf('"', vStart);
                if (vEnd < 0) return false;

                var altStr = tag.Substring(vStart, vEnd - vStart);
                return double.TryParse(altStr, NumberStyles.Any, CultureInfo.InvariantCulture, out altitudeMeters);
            }
            catch { return false; }
        }

        /// <summary>
        /// Filters a CAM XML string by altitude.
        /// </summary>
        /// <param name="rawXml">The raw CAM XML string.</param>
        /// <returns>True if the CAM should be filtered; otherwise, false.</returns>
        private bool TryFilterCamByAltitude(string rawXml)
        {
            if (FilterCheckBox?.IsChecked != true) return false;

            // kick-off fetch, but don't block UI
            _ = EnsureLocalAreaAltitudeAsync();

            if (!_localAltitudeMeters.HasValue) return false;
            if (!TryExtractAltitudeFromCamXml(rawXml, out var camAlt)) return false;

            return Math.Abs(camAlt - _localAltitudeMeters.Value) > 50.0;
        }

        /// <summary>
        /// Filters a replay by altitude.
        /// </summary>
        /// <param name="fullId">The full vehicle ID.</param>
        /// <param name="ts">The timestamp of the replay.</param>
        /// <returns>True if the replay should be filtered; otherwise, false.</returns>
        private bool FilterReplayByAltitude(string fullId, TimeSpan ts)
        {
            if (FilterCheckBox?.IsChecked != true) return false;

            // ID rule first
            if (IsInvalidVehicleId(fullId)) return true;

            // altitude rule
            if (!_localAltitudeMeters.HasValue) return false;
            var key = $"{fullId}|{ts.Ticks}";
            if (_playbackAltitudeByIdAndTs.TryGetValue(key, out var camAlt))
                return Math.Abs(camAlt - _localAltitudeMeters.Value) > 50.0;

            return false;
        }

        /// <summary>
        /// Determines whether a vehicle ID is invalid.
        /// </summary>
        /// <param name="vehicleId">The vehicle ID to check.</param>
        /// <returns>True if the vehicle ID is invalid; otherwise, false.</returns>
        private static bool IsInvalidVehicleId(string vehicleId)
        {
            if (string.IsNullOrWhiteSpace(vehicleId)) return true;
            if (vehicleId.Length < 10) return true;

            string shortId = vehicleId.Length >= 4 ? vehicleId[^4..] : vehicleId;
            if (int.TryParse(shortId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n >= 3000;

            return true;
        }

        /// <summary>
        /// Determines whether a live vehicle ID should be filtered.
        /// </summary>
        /// <param name="vehicleId">The vehicle ID to check.</param>
        /// <returns>True if the live vehicle ID should be filtered; otherwise, false.</returns>
        private bool ShouldFilterLiveById(string vehicleId)
        {
            return (FilterCheckBox?.IsChecked == true) && IsInvalidVehicleId(vehicleId);
        }

        /// <summary>
        /// Handles the mouse wheel event for zooming on the map.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse wheel event arguments.</param>
        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int delta = e.Delta > 0 ? 1 : -1;
            int newZoom = Math.Clamp(zoom + delta, 3, 18);

            if (newZoom == zoom)
            {
                Console.WriteLine($"[ZOOM] Already at limit: {zoom}");
                return; // Už jsme na limitu
            }

            _pendingZoom = newZoom;

            _lastWheelPos = e.GetPosition(TileCanvas);
            scale.CenterX = _lastWheelPos.X;
            scale.CenterY = _lastWheelPos.Y;

            double previewScale = Math.Pow(2, _pendingZoom - zoom);
            scale.ScaleX = previewScale;
            scale.ScaleY = previewScale;

            Console.WriteLine($"[ZOOM] Wheel event: {zoom} -> {_pendingZoom}, scale: {previewScale:F2}");

            // Vždy znovu vytvořit timer, aby se zajistilo, že funguje
            if (_zoomDebounceTimer != null)
            {
                _zoomDebounceTimer.Stop();
                _zoomDebounceTimer.Tick -= OnZoomDebounceTimerTick; // Odpojit starý handler
            }

            _zoomDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _zoomDebounceTimer.Tick += OnZoomDebounceTimerTick;
            _zoomDebounceTimer.Start();

            Console.WriteLine($"[ZOOM] Timer started");
            e.Handled = true;
        }

        /// <summary>
        /// Handler pro zoom debounce timer - oddělený pro lepší správu
        /// </summary>
        private async void OnZoomDebounceTimerTick(object? sender, EventArgs e)
        {
            if (_zoomDebounceTimer == null) return;

            _zoomDebounceTimer.Stop();
            _zoomDebounceTimer.Tick -= OnZoomDebounceTimerTick;

            int oldZoom = zoom;
            Console.WriteLine($"[ZOOM] Timer fired: processing zoom {oldZoom}  {_pendingZoom}");

            // Schovat všechny overlaye před reprojectem – zabrání bliknutí/přeskoku velikosti
            var overlays = TileCanvas.Children
                .OfType<UIElement>()
                .Where(el => el is not Image)
                .ToList();
            foreach (var el in overlays)
                el.Visibility = Visibility.Hidden;

            try
            {
                double worldMouseX = cameraX + _lastWheelPos.X;
                double worldMouseY = cameraY + _lastWheelPos.Y;

                double oldTileMouseX = worldMouseX / TileSize;
                double oldTileMouseY = worldMouseY / TileSize;

                double mouseLatLon_lon = TileXToLon(oldTileMouseX, oldZoom);
                double mouseLatLon_lat = TileYToLat(oldTileMouseY, oldZoom);

                zoom = _pendingZoom;

                // KRITICKÉ: Reset scale transform PŘED načítáním nových dlaždic
                scale.ScaleX = 1;
                scale.ScaleY = 1;

                Console.WriteLine($"[ZOOM] Scale reset to 1.0, new zoom level: {zoom}");

                // Vyčistit všechny staré dlaždice
                var oldTiles = TileCanvas.Children
                    .OfType<Image>()
                    .Where(img => img.Tag is ValueTuple<int, int, int>)
                    .ToList();

                foreach (var tile in oldTiles)
                {
                    TileCanvas.Children.Remove(tile);
                }
                Console.WriteLine($"[ZOOM] Removed {oldTiles.Count} old tiles");

                double newTileMouseX = LonToTileX(mouseLatLon_lon, zoom);
                double newTileMouseY = LatToTileY(mouseLatLon_lat, zoom);

                int newCameraX = (int)Math.Round(newTileMouseX * TileSize - _lastWheelPos.X);
                int newCameraY = (int)Math.Round(newTileMouseY * TileSize - _lastWheelPos.Y);

                int startX = (int)Math.Floor((double)newCameraX / TileSize);
                int startY = (int)Math.Floor((double)newCameraY / TileSize);

                double offsetX = newCameraX - (startX * TileSize);
                double offsetY = newCameraY - (startY * TileSize);

                Console.WriteLine($"[ZOOM] Loading tiles at ({startX}, {startY}) with offset ({offsetX:F1}, {offsetY:F1})");

                await LoadTilesSmoothAsync(startX, startY, offsetX, offsetY);

                double centerWorldX = cameraX + TileCanvas.ActualWidth / 2.0;
                double centerWorldY = cameraY + TileCanvas.ActualHeight / 2.0;

                double centerTileX = centerWorldX / TileSize;
                double centerTileY = centerWorldY / TileSize;

                longitude = TileXToLon(centerTileX, zoom);
                latitude = TileYToLat(centerTileY, zoom);

                UpdateCenterTextBoxesFromFields();

                _ = EnsureLocalAreaAltitudeAsync(force: true);

                ResetAllTramTrails();
                UpdateAllOverlaysLive();
                DrawRadiusCircle();

                if (lastV2XMessage != null)
                {
                    UpdateVehicleTrail(lastV2XMessage);
                }

                Console.WriteLine($"[ZOOM] Complete: {oldZoom}  {zoom}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZOOM] ERROR: {ex.Message}");
                // V případě chyby obnovit zoom na původní hodnotu
                zoom = oldZoom;
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
            finally
            {
                // Zobrazit overlaye zpět až po dokončeném reprojectu
                foreach (var el in overlays)
                    el.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Handles the mouse down event on the tile canvas.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse button event arguments.</param>
        private void TileCanvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                Console.WriteLine("[MMB] PreviewMouseDown - setting flags");
                isDragging = true;
                isMiddleMousePanning = true;
                lastMousePos = e.GetPosition(TileCanvas);
                TileCanvas.CaptureMouse();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles panning on the map when the middle mouse button is held down and the mouse is moved.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TileCanvas_MouseMove_MiddlePan(object sender, MouseEventArgs e)
        {
            if (!isDragging) return;

            Point currentPos = e.GetPosition(TileCanvas);

            double dx = currentPos.X - lastMousePos.X;
            double dy = currentPos.Y - lastMousePos.Y;

            // Update camera position (inverted because we're moving the world)
            cameraX -= (int)dx;
            cameraY -= (int)dy;

            // Keep _currentTopLeftTileX/Y in sync for compatibility
            _currentTopLeftTileX = (int)Math.Floor((double)cameraX / TileSize);
            _currentTopLeftTileY = (int)Math.Floor((double)cameraY / TileSize);

            lastMousePos = currentPos;

            // Update ALL overlays IMMEDIATELY during drag for smooth movement
            UpdateAllOverlaysLive();

            // Then render tiles with new camera position
            RenderTilesProgressive();

            e.Handled = true;
        }

        /// <summary>
        /// Updates all overlays on the map live during a drag operation.
        /// </summary>
        private void UpdateAllOverlaysLive()
        {
            UpdateStopsPositions();
            UpdateActivationZonesPositions();
            UpdateSwitchZonesPositions();
            UpdateDrawnTramsPositions();
            UpdateActiveVehiclesPositions();
            UpdateReplayVehiclesPositions();
            UpdatePolylinePositions();

            if (CircleCheckBox?.IsChecked == true)
                DrawRadiusCircle();

            UpdateTramSignalPositions();
        }

        /// <summary>
        /// Updates the positions of all polylines and their vertices on the canvas based on their stored geographic coordinates.
        /// </summary>
        private void UpdatePolylinePositions()
        {
            foreach (var kv in _polylineGeoPoints.ToList())
            {
                var poly = kv.Key;

                if (selectedElement is Ellipse selDot &&
                    _polylineVertexMap.TryGetValue(selDot, out var selInfo) &&
                    selInfo.polyline == poly)
                    continue;

                var geoPointsList = kv.Value;
                if (geoPointsList.Count == 0)
                    continue;

                bool isActiveDrawing = (_isDrawingPolyline && poly == currentPolyline);
                double mpp = MetersPerPixel(latitude, zoom);
                double halfWidthPx = (_polylineZoneWidthMeters / 2.0) / mpp;

                // Update ALL points from geo
                for (int i = 0; i < geoPointsList.Count; i++)
                {
                    var (gLat, gLon) = geoPointsList[i];
                    var canvasPos = ConvertLatLonToCanvasXY(gLat, gLon);
                    var newCanvasPoint = new Point(canvasPos.x, canvasPos.y);

                    if (i < poly.Points.Count)
                        poly.Points[i] = newCanvasPoint;
                    else
                        poly.Points.Add(newCanvasPoint);

                    if (poly == currentPolyline && i < polylinePoints.Count)
                        polylinePoints[i] = newCanvasPoint;

                    foreach (var dotKv in _polylineVertexMap.Where(d => d.Value.polyline == poly && d.Value.pointIndex == i))
                    {
                        var dot = dotKv.Key;
                        Canvas.SetLeft(dot, canvasPos.x - 4);
                        Canvas.SetTop(dot, canvasPos.y - 4);

                        if (_polylineVertexToCircle.TryGetValue(dot, out var circle))
                        {
                            Canvas.SetLeft(circle, canvasPos.x - halfWidthPx);
                            Canvas.SetTop(circle, canvasPos.y - halfWidthPx);
                            circle.Width = halfWidthPx * 2;
                            circle.Height = halfWidthPx * 2;
                        }
                    }
                }

                // *** KLÍČOVÁ OPRAVA: Pro aktivní kreslení použít polylinePoints místo GetCommittedPolylinePoints ***
                List<Point> rebuildPoints;

                if (isActiveDrawing)
                {
                    // Při aktivním kreslení použít aktuální polylinePoints
                    rebuildPoints = new List<Point>(polylinePoints);
                    Console.WriteLine($"[UPDATE POS] Active drawing: using {rebuildPoints.Count} points from polylinePoints");
                }
                else
                {
                    // Pro finalizované polyline použít všechny body
                    rebuildPoints = poly.Points.ToList();
                    Console.WriteLine($"[UPDATE POS] Finalized: using {rebuildPoints.Count} points from poly.Points");
                }

                if (rebuildPoints.Count >= 2)
                {
                    // Rozhodnout, kterou rebuild metodu použít
                    bool hasContinuation = (_polylineCommittedPointsCount > 0 && poly == currentPolyline);
                    bool hasTableSegments = (_polylineToSegmentZones.ContainsKey(poly) && _polylineToSegmentZones[poly].Count > 0);

                    // Při aktivním kreslení synchronizovat poly.Points s potvrzenými body (bez preview bodu)
                    if (isActiveDrawing)
                    {
                        poly.Points.Clear();
                        foreach (var pt in rebuildPoints)
                            poly.Points.Add(pt);
                    }

                    if (isActiveDrawing)
                    {
                        RebuildPolylineZone(poly, rebuildPoints, halfWidthPx);
                        UpdatePolylineDirectionArrows(poly, rebuildPoints);
                    }
                    else if (hasTableSegments)
                    {
                        RebuildPolylineZoneWithVariableWidths(poly, rebuildPoints);
                        UpdatePolylineDirectionArrows(poly, rebuildPoints);
                    }
                    else if (!isActiveDrawing && hasTableSegments)
                    {
                        // Finalizovaná polyline s table segments
                        Console.WriteLine($"[UPDATE POS] Rebuilding with VARIABLE widths (finalized)");
                        RebuildPolylineZoneWithVariableWidths(poly, rebuildPoints);
                        UpdatePolylineDirectionArrows(poly, rebuildPoints);
                    }
                    else
                    {
                        // Nová polyline nebo bez table segments - uniform width
                        Console.WriteLine($"[UPDATE POS] Rebuilding with UNIFORM width");
                        RebuildPolylineZone(poly, rebuildPoints, halfWidthPx);
                        UpdatePolylineDirectionArrows(poly, rebuildPoints);
                    }

                    // Sync _currentPolylineSegments
                    if (isActiveDrawing && _polylineToSegments.TryGetValue(poly, out var currentSegs))
                    {
                        _currentPolylineSegments.Clear();
                        _currentPolylineSegments.AddRange(currentSegs);
                        Console.WriteLine($"[UPDATE POS] Synced {currentSegs.Count} segments to _currentPolylineSegments");
                    }
                }
            }
        }

        /// <summary>
        /// Gets committed polyline points on polyline vertices for a given polyline. 
        /// If currently drawing this polyline, excludes the last point which is the 
        /// "preview" point following the mouse.
        /// </summary>
        /// <param name="poly">The polyline to get committed points from.</param>
        /// <returns>A list of committed points.</returns>
        private List<Point> GetCommittedPolylinePoints(Polyline poly)
        {
            var points = poly.Points.ToList();

            if (_isDrawingPolyline &&
                poly == currentPolyline &&
                points.Count > polylinePoints.Count)
            {
                points.RemoveAt(points.Count - 1);
            }

            return points;
        }

        /// <summary>
        /// Collapses the polyline preview by finalizing the last point.
        /// </summary>
        private void CollapsePolylinePreview()
        {
            if (currentPolyline == null)
                return;

            if (polylinePoints.Count == 0)
                return;

            if (currentPolyline.Points.Count == polylinePoints.Count + 1)
            {
                currentPolyline.Points[^1] = polylinePoints[^1];
            }
        }

        /// <summary>
        /// Updates activation zones positions based on their stored geographic coordinates and the current zoom level.
        /// </summary>
        private void UpdateActivationZonesPositions()
        {
            foreach (var zone in ActivationZonesCollection.Where(z => z != null && !IsSwitchZone(z)))
            {
                if (zone.Rectangle == null) continue;

                // meters -> pixels at current zoom
                double mppLocal = MetersPerPixel(zone.Latitude, zoom);
                double widthPx = zone.Width / mppLocal;
                double heightPx = zone.Height / mppLocal;

                zone.Rectangle.Width = widthPx;
                zone.Rectangle.Height = heightPx;

                // Start point from stored Lat/Lon
                var (sx, sy) = ConvertLatLonToCanvasXY(zone.Latitude, zone.Longitude);
                zone.StartPoint = new Point(sx, sy);

                UpdateRectanglePositionFromStartPoint(zone);
                try { EnsureZoneArrow(zone); } catch { }
            }
        }

        /// <summary>
        /// Updates drawn trams positions based on their stored geographic coordinates and the current zoom level.
        /// </summary>
        private void UpdateDrawnTramsPositions()
        {
            for (int idx = 0; idx < drawnTrams?.Length; idx++)
            {
                var tram = drawnTrams[idx];
                if (tram == null) continue;

                // Reposition to last known geo
                if (drawnTramLat[idx].HasValue && drawnTramLon[idx].HasValue)
                {
                    var (x, y) = ConvertLatLonToCanvasXY(drawnTramLat[idx]!.Value, drawnTramLon[idx]!.Value);

                    // Update ellipse position
                    if (tram.Ellipse != null)
                    {
                        Canvas.SetLeft(tram.Ellipse, x - 6);
                        Canvas.SetTop(tram.Ellipse, y - 6);
                    }

                    // Update text position
                    if (tram.Text != null)
                    {
                        Canvas.SetLeft(tram.Text, x + 8);
                        Canvas.SetTop(tram.Text, y - 6);
                    }

                    // Update vehicle box - POINT IS THE TOP CENTER
                    if (drawnTramTrailGeoPoints[idx].Count >= 2)
                    {
                        var (plat, plon) = drawnTramTrailGeoPoints[idx][^2];
                        var (px, py) = ConvertLatLonToCanvasXY(plat, plon);
                        var headingDeg = CalculateAzimuth(new Point(px, py), new Point(x, y));
                        //headingDeg = (headingDeg - 180 + 360) % 360; // Manual flip rule

                        if (_vehicleBoxes.TryGetValue(drawnTramIds[idx], out var box))
                        {
                            UpdateVehicleBoxPosition(box, new Point(x, y), headingDeg);
                        }
                    }
                }

                // Update trail from geo points
                if (drawnTramTrailGeoPoints[idx].Count > 0 && drawnTramTrails[idx] != null)
                {
                    var pl = drawnTramTrails[idx];
                    pl.Points.Clear();

                    for (int i = 0; i < drawnTramTrailGeoPoints[idx].Count; i++)
                    {
                        var (lat, lon) = drawnTramTrailGeoPoints[idx][i];
                        var (tx, ty) = ConvertLatLonToCanvasXY(lat, lon);
                        pl.Points.Add(new Point(tx, ty));
                    }

                    // Update trail dots
                    if (tram.TrailDots != null)
                    {
                        int dotIndex = 0;
                        for (int i = 0; i < drawnTramTrailGeoPoints[idx].Count - 1 && dotIndex < tram.TrailDots.Count; i++)
                        {
                            var (lat, lon) = drawnTramTrailGeoPoints[idx][i];
                            var (tx, ty) = ConvertLatLonToCanvasXY(lat, lon);

                            var dot = tram.TrailDots[dotIndex];
                            Canvas.SetLeft(dot, tx - 2.5);
                            Canvas.SetTop(dot, ty - 2.5);
                            dotIndex++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Updates active vehicles positions based on their stored geographic coordinates and the current zoom level.
        /// </summary>
        private void UpdateActiveVehiclesPositions()
        {
            var now = DateTime.Now;

            foreach (var kvp in activeVehicles)
            {
                var pt = kvp.Value;
                if (pt == null) continue;

                // Skip vehicles that are being cleaned up (older than 25 seconds - give 5s grace before removal)
                if ((now - pt.LastUpdate).TotalSeconds > 25)
                {
                    // Hide trail for inactive vehicles
                    var trailLine = TileCanvas.Children.OfType<Polyline>()
                        .FirstOrDefault(pl => pl.Tag is string tag && tag == $"trail_{pt.Label}");
                    if (trailLine != null)
                    {
                        trailLine.Visibility = Visibility.Collapsed;
                    }

                    // Still update position but don't show trail
                }

                // Get stored lat/lon if available
                if (_lastLatLon.TryGetValue(pt?.Label, out var geo))
                {
                    var (x, y) = ConvertLatLonToCanvasXY(geo.lat, geo.lon);

                    // Update ellipse
                    if (pt.Ellipse != null)
                    {
                        Canvas.SetLeft(pt.Ellipse, x - 6);
                        Canvas.SetTop(pt.Ellipse, y - 6);
                    }

                    // Update text
                    if (pt.Text != null)
                    {
                        StyleVehicleLabel(pt.Text, pt.Text.Foreground);
                        PositionVehicleLabelsTogether(pt.Text, pt.Speed, new Point(x, y));
                    }

                    // Update speed text
                    if (pt.Speed != null)
                    {
                        StyleVehicleLabel(pt.Speed, pt.Speed.Foreground);
                        PositionVehicleLabelsTogether(pt.Text, pt.Speed, new Point(x, y));
                    }

                    // Update accuracy text (the "no acc" label)
                    if (_liveAccuracyTextById.TryGetValue(pt.Label, out var accText))
                    {
                        Canvas.SetLeft(accText, x + 5);
                        Canvas.SetTop(accText, y + 20);
                    }

                    // Update vehicle box
                    if (_vehicleBoxes.TryGetValue(pt.Label, out var box) && _lastHeadingLive.TryGetValue(pt.Label, out var heading))
                    {
                        UpdateVehicleBoxPosition(box, new Point(x, y), heading);
                    }

                    // Update accuracy circle if visible
                    if (AccuracyCB?.IsChecked == true && _lastLiveAccuracyById.TryGetValue(pt.Label, out var accValue) && accValue.HasValue && accValue.Value >= 4)
                    {
                        // Find accuracy circle by tag
                        var accEllipse = TileCanvas.Children.OfType<Ellipse>()
                            .FirstOrDefault(e => e.Tag is string s && s == $"live_acc_{pt.Label}");

                        if (accEllipse != null)
                        {
                            // Recalculate radius in pixels
                            double mpp = MetersPerPixel(geo.lat, zoom);
                            double radiusPx = accValue.Value / Math.Max(1e-6, mpp);

                            // Update size and position
                            accEllipse.Width = radiusPx * 2;
                            accEllipse.Height = radiusPx * 2;
                            Canvas.SetLeft(accEllipse, x - radiusPx);
                            Canvas.SetTop(accEllipse, y - radiusPx);
                        }
                    }
                }

                // Update trail polyline from geo points - ONLY for active vehicles
                bool isActive = (now - pt.LastUpdate).TotalSeconds <= 25;
                if (isActive && pt.TrailGeoPoints != null && pt.TrailGeoPoints.Count > 0)
                {
                    // Find existing polyline
                    var trailLine = TileCanvas.Children.OfType<Polyline>()
                        .FirstOrDefault(pl => pl.Tag is string tag && tag == $"trail_{pt.Label}");

                    if (trailLine != null)
                    {
                        trailLine.Visibility = Visibility.Visible;

                        // Recalculate all points from geo coordinates
                        trailLine.Points.Clear();
                        foreach (var (lat, lon) in pt.TrailGeoPoints)
                        {
                            var (tx, ty) = ConvertLatLonToCanvasXY(lat, lon);
                            trailLine.Points.Add(new Point(tx, ty));
                        }
                    }

                    // Update trail dots from geo points
                    if (pt.TrailDots != null && pt.TrailDots.Count > 0)
                    {
                        // Should have dots for all trail points except the last (current position)
                        int expectedDots = Math.Max(0, pt.TrailGeoPoints.Count - 1);

                        for (int i = 0; i < expectedDots && i < pt.TrailDots.Count && i < pt.TrailGeoPoints.Count; i++)
                        {
                            var (lat, lon) = pt.TrailGeoPoints[i];
                            var (tx, ty) = ConvertLatLonToCanvasXY(lat, lon);

                            if (i < pt.TrailDots.Count)
                            {
                                var dot = pt.TrailDots[i];
                                Canvas.SetLeft(dot, tx - 2.5);
                                Canvas.SetTop(dot, ty - 2.5);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Updates replay vehicles positions based on their stored geographic coordinates and the current zoom level.
        /// </summary>
        private void UpdateReplayVehiclesPositions()
        {
            foreach (var kvp in _replayVehicles)
            {
                var vehicle = kvp.Value;
                if (vehicle == null) continue;

                string id = kvp.Key;

                // Get current geo position from replay frames
                if (_replayGeoFrames.TryGetValue(id, out var frames) && frames.Count > 0)
                {
                    // Find current frame based on replay time
                    var currentFrame = frames.LastOrDefault(f => f.ts <= playbackElapsedTime);
                    if (currentFrame != default)
                    {
                        var (x, y) = ConvertLatLonToCanvasXY(currentFrame.lat, currentFrame.lon);
                        var pos = new Point(x, y);

                        // přidat toto:
                        double headingAct = double.NaN;
                        var headingKey = $"{id}|{currentFrame.ts.Ticks}";

                        if (_playbackHeadingByIdAndTs.TryGetValue(headingKey, out var replayHeading))
                        {
                            headingAct = replayHeading;
                        }

                        CheckActivationZones(pos, id, headingAct);

                        // Update ellipse
                        if (vehicle.Ellipse != null)
                        {
                            Canvas.SetLeft(vehicle.Ellipse, x - 6);
                            Canvas.SetTop(vehicle.Ellipse, y - 6);
                        }

                        // Update text
                        if (vehicle.Text != null)
                        {
                            StyleVehicleLabel(vehicle.Text, vehicle.Text.Foreground);
                            PositionVehicleLabel(vehicle.Text, new Point(x, y), -22);
                            Panel.SetZIndex(vehicle.Text, 1199);
                        }
                        if (vehicle.Speed != null)
                        {
                            string speedKey = $"{id}|{currentFrame.ts.Ticks}";

                            if (_playbackSpeedByIdAndTs.TryGetValue(speedKey, out double speed))
                            {
                                vehicle.Speed.Text = $"{speed:F1} m/s";
                            }

                            StyleVehicleLabel(vehicle.Speed, vehicle.Speed.Foreground);
                            PositionVehicleLabelsTogether(vehicle.Text, vehicle.Speed, new Point(x, y));

                            if (!TileCanvas.Children.Contains(vehicle.Speed))
                                TileCanvas.Children.Add(vehicle.Speed);
                            Panel.SetZIndex(vehicle.Speed, 1200);
                        }

                        PositionVehicleLabelsTogether(vehicle.Text, vehicle.Speed, new Point(x, y));

                        // Update replay box
                        if (_replayBoxes.TryGetValue(id, out var box))
                        {
                            var key = $"{id}|{currentFrame.ts.Ticks}";
                            if (_playbackHeadingByIdAndTs.TryGetValue(key, out var heading))
                            {
                                UpdateVehicleBoxPosition(box, new Point(x, y), heading);
                            }
                        }
                    }
                }

                // Update replay trail podle aktuálního času
                var trailFrames = frames
                    .Where(f => f.ts <= playbackElapsedTime)
                    .TakeLast(_maxTrailLength + 1)
                    .ToList();

                var trailLine = TileCanvas.Children.OfType<Polyline>()
                    .FirstOrDefault(pl => pl.Tag is string tag && tag == $"replay_trail_{id}");

                if (trailLine != null)
                {
                    trailLine.Points.Clear();

                    foreach (var f in trailFrames)
                    {
                        var (tx, ty) = ConvertLatLonToCanvasXY(f.lat, f.lon);
                        trailLine.Points.Add(new Point(tx, ty));
                    }
                }

                // Update trail dots
                if (vehicle.TrailDots != null)
                {
                    foreach (var dot in vehicle.TrailDots)
                        TileCanvas.Children.Remove(dot);

                    vehicle.TrailDots.Clear();

                    for (int i = 0; i < trailFrames.Count - 1; i++)
                    {
                        var f = trailFrames[i];
                        var (tx, ty) = ConvertLatLonToCanvasXY(f.lat, f.lon);

                        var dot = new Ellipse
                        {
                            Width = 5,
                            Height = 5,
                            Fill = Brushes.Black,
                            IsHitTestVisible = false,
                            Tag = $"replay_trail_dot_{id}_{i}"
                        };

                        Canvas.SetLeft(dot, tx - 2.5);
                        Canvas.SetTop(dot, ty - 2.5);

                        vehicle.TrailDots.Add(dot);
                        TileCanvas.Children.Add(dot);
                        Panel.SetZIndex(dot, 1001);
                    }
                }
            }
        }

        /// <summary>
        /// Updates the position and rotation of a vehicle's bounding box on the canvas.
        /// </summary>
        /// <param name="box">The rectangle representing the vehicle's bounding box.</param>
        /// <param name="topCenter">The top center point of the vehicle.</param>
        /// <param name="headingDeg">The heading of the vehicle in degrees.</param>
        private void UpdateVehicleBoxPosition(Rectangle box, Point topCenter, double headingDeg)
        {
            if (box == null) return;

            const double boxWidth = 15.0;

            Canvas.SetLeft(box, topCenter.X - boxWidth / 2.0);
            Canvas.SetTop(box, topCenter.Y);

            box.RenderTransform = new RotateTransform(
                headingDeg,
                boxWidth / 2.0,
                0.0
            );
        }

        /// <summary>
        /// Updates the positions of all switch zones on the canvas based on their stored geographic coordinates and the current zoom level.
        /// </summary>
        private void UpdateSwitchZonesPositions()
        {
            foreach (var zone in ActivationZonesCollection.Where(z => z != null && IsSwitchZone(z)))
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

                try { EnsureZoneArrow(zone); } catch { }
            }
        }

        /// <summary>
        /// Updates the positions of all stops on the canvas based on their stored geographic coordinates and the current zoom level.
        /// </summary>
        private void UpdateStopsPositions()
        {
            if (stops == null || stops.Count == 0 || TileCanvas == null) return;

            // Find all existing stop visuals
            var stopMarkers = TileCanvas.Children.OfType<Ellipse>()
                .Where(el => Equals(el.Tag, "Stop"))
                .ToList();

            var stopLabels = TileCanvas.Children.OfType<TextBlock>()
                .Where(el => Equals(el.Tag, "Stop"))
                .ToList();

            // If we don't have the right number of stops, redraw everything
            if (stopMarkers.Count != stops.Count || stopLabels.Count != stops.Count)
            {
                //DrawStopsOnCanvasSafe();
                return;
            }

            // Update positions using the camera-aware conversion
            for (int i = 0; i < stops.Count; i++)
            {
                var stop = stops[i];
                var (x, y) = ConvertLatLonToCanvasXY(stop.Latitude, stop.Longitude);

                // Update marker position
                if (i < stopMarkers.Count)
                {
                    Canvas.SetLeft(stopMarkers[i], x - 4);
                    Canvas.SetTop(stopMarkers[i], y - 4);
                }

                // Update label position
                if (i < stopLabels.Count)
                {
                    Canvas.SetLeft(stopLabels[i], x + 6);
                    Canvas.SetTop(stopLabels[i], y - 6);
                }
            }
        }

        /// <summary>
        /// Renders tiles progressively on the canvas based on the current camera position and zoom level.
        /// </summary>
        private void RenderTilesProgressive()
        {
            if (TileCanvas.ActualWidth == 0 || TileCanvas.ActualHeight == 0) return;

            int tileSize = TileSize;

            // Calculate how many tiles we need to cover the canvas + buffer
            int columns = (int)Math.Ceiling(TileCanvas.ActualWidth / tileSize) + 2;
            int rows = (int)Math.Ceiling(TileCanvas.ActualHeight / tileSize) + 2;

            // Calculate starting tile indices
            int startTileX = (int)Math.Floor((double)cameraX / tileSize);
            int startTileY = (int)Math.Floor((double)cameraY / tileSize);

            // Calculate local offset within the tile
            int localOffsetX = Mod(cameraX, tileSize);
            int localOffsetY = Mod(cameraY, tileSize);

            double drawOffsetX = -localOffsetX;
            double drawOffsetY = -localOffsetY;

            // Keep track of which tiles we're rendering
            var currentTiles = new HashSet<(int z, int x, int y)>();
            var tilesToUpdate = new List<(Image img, double x, double y)>();

            // First pass: Update existing tiles and mark what we need
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < columns; col++)
                {
                    int tileX = startTileX + col;
                    int tileY = startTileY + row;

                    tileX = WrapTileX(tileX, zoom);
                    tileY = ClampTileY(tileY, zoom);

                    var tileKey = (zoom, tileX, tileY);
                    currentTiles.Add(tileKey);

                    double x = drawOffsetX + col * tileSize;
                    double y = drawOffsetY + row * tileSize;

                    // Check if this tile already exists
                    var existingTile = TileCanvas.Children
                        .OfType<Image>()
                        .FirstOrDefault(img =>
                        {
                            var tag = img.Tag as (int z, int x, int y)?;
                            return tag.HasValue && tag.Value == tileKey;
                        });

                    if (existingTile != null)
                    {
                        // Update position immediately
                        tilesToUpdate.Add((existingTile, x, y));
                    }
                    else
                    {
                        // Load new tile asynchronously (don't await)
                        _ = LoadAndPlaceTileAsync(zoom, tileX, tileY, x, y);
                    }
                }
            }

            // Update all existing tile positions at once (faster)
            foreach (var (img, x, y) in tilesToUpdate)
            {
                Canvas.SetLeft(img, x);
                Canvas.SetTop(img, y);
            }

            // Remove tiles that are no longer visible (immediate cleanup)
            var tilesToRemove = TileCanvas.Children
                .OfType<Image>()
                .Where(img =>
                {
                    var tag = img.Tag as (int z, int x, int y)?;
                    if (!tag.HasValue) return false;
                    return !currentTiles.Contains(tag.Value);
                })
                .ToList();

            foreach (var tile in tilesToRemove)
            {
                TileCanvas.Children.Remove(tile);
            }

            TrimTileCache(currentTiles);

        }

        /// <summary>
        /// Loads and places tiles into correct place on canvas based on given tile coordinates and camera position.
        /// </summary>
        /// <param name="z">Zoom level of the tile.</param>
        /// <param name="x">X coordinate of the tile.</param>
        /// <param name="y">Y coordinate of the tile.</param>
        /// <param name="canvasX">X position on the canvas.</param>
        /// <param name="canvasY">Y position on the canvas.</param>
        private async Task LoadAndPlaceTileAsync(int z, int x, int y, double canvasX, double canvasY)
        {
            try
            {
                await _tileSemaphore.WaitAsync();

                // Check if tile already exists (race condition check)
                var existing = TileCanvas.Children
                    .OfType<Image>()
                    .FirstOrDefault(img =>
                    {
                        var tag = img.Tag as (int z, int x, int y)?;
                        return tag.HasValue && tag.Value == (z, x, y);
                    });

                if (existing != null)
                {
                    // Tile already loaded, just update position
                    await Dispatcher.InvokeAsync(() =>
                    {
                        Canvas.SetLeft(existing, canvasX);
                        Canvas.SetTop(existing, canvasY);
                    });
                    return;
                }

                var bmp = await FetchTileAsync(z, x, y, CancellationToken.None);

                await Dispatcher.InvokeAsync(() =>
                {
                    // Double-check tile is still needed
                    int currentStartTileX = (int)Math.Floor((double)cameraX / TileSize);
                    int currentStartTileY = (int)Math.Floor((double)cameraY / TileSize);
                    int columns = (int)Math.Ceiling(TileCanvas.ActualWidth / TileSize) + 2;
                    int rows = (int)Math.Ceiling(TileCanvas.ActualHeight / TileSize) + 2;

                    bool inRange = x >= currentStartTileX && x < currentStartTileX + columns &&
                                  y >= currentStartTileY && y < currentStartTileY + rows;

                    if (!inRange)
                    {
                        return; // Tile no longer needed
                    }

                    // Check one more time if someone else added it
                    var doubleCheck = TileCanvas.Children
                        .OfType<Image>()
                        .FirstOrDefault(img =>
                        {
                            var tag = img.Tag as (int z, int x, int y)?;
                            return tag.HasValue && tag.Value == (z, x, y);
                        });

                    if (doubleCheck != null)
                    {
                        Canvas.SetLeft(doubleCheck, canvasX);
                        Canvas.SetTop(doubleCheck, canvasY);
                        return;
                    }

                    Image img = CreateTileImage(bmp);
                    img.Tag = (z, x, y); // Tag for tracking
                    Canvas.SetLeft(img, canvasX);
                    Canvas.SetTop(img, canvasY);
                    Panel.SetZIndex(img, 0); // Tiles at bottom

                    // Insert at beginning to keep under overlays
                    TileCanvas.Children.Insert(0, img);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TILES] Failed to load tile {z}/{x}/{y}: {ex.Message}");
            }
            finally
            {
                _tileSemaphore.Release();
            }

        }

        /// <summary>
        /// Update positions of all overlays based on their stored geographic coordinates and the current zoom level.
        /// </summary>
        private async Task UpdateOverlayPositions()
        {
            // Stops are updated separately in MouseMove for better performance
            // UpdateStopsPositions();

            // Update other overlays
            await ReprojectAllZonesOnMapChange();
            ReprojectActiveVehiclesOnMapChange();
            ReprojectReplayOnMapChange();
            //ReprojectRailwaysOnMapChange();
        }

        /// <summary>
        /// Moves all overlays by the specified delta values.
        /// </summary>
        /// <param name="deltaX">The change in the X direction.</param>
        /// <param name="deltaY">The change in the Y direction.</param>
        private void MoveOverlays(double deltaX, double deltaY)
        {
            foreach (var pt in points)
            {
                if (pt.Ellipse != null)
                {
                    Canvas.SetLeft(pt.Ellipse, Canvas.GetLeft(pt.Ellipse) + deltaX);
                    Canvas.SetTop(pt.Ellipse, Canvas.GetTop(pt.Ellipse) + deltaY);
                }
                if (pt.Text != null)
                {
                    Canvas.SetLeft(pt.Text, Canvas.GetLeft(pt.Text) + deltaX);
                    Canvas.SetTop(pt.Text, Canvas.GetTop(pt.Text) + deltaY);
                }
            }

            if (connectionLine != null)
            {
                for (int i = 0; i < connectionLine.Points.Count; i++)
                {
                    connectionLine.Points[i] = new Point(
                        connectionLine.Points[i].X + deltaX,
                        connectionLine.Points[i].Y + deltaY
                    );
                }
            }

            // Move active polyline being drawn
            if (_isDrawingPolyline && currentPolyline != null)
            {
                // Move all committed points in the polyline
                for (int i = 0; i < polylinePoints.Count; i++)
                {
                    polylinePoints[i] = new Point(polylinePoints[i].X + deltaX, polylinePoints[i].Y + deltaY);
                    if (i < currentPolyline.Points.Count)
                    {
                        currentPolyline.Points[i] = polylinePoints[i];
                    }
                }

                // Move vertex dots
                foreach (var dot in polylineVertexDots)
                {
                    Canvas.SetLeft(dot, Canvas.GetLeft(dot) + deltaX);
                    Canvas.SetTop(dot, Canvas.GetTop(dot) + deltaY);
                }

                // Move circles around vertices
                foreach (var circle in _currentPolylineCircles)
                {
                    Canvas.SetLeft(circle, Canvas.GetLeft(circle) + deltaX);
                    Canvas.SetTop(circle, Canvas.GetTop(circle) + deltaY);
                }

                // Move zone segments (Path elements)
                foreach (var segment in _currentPolylineSegments)
                {
                    if (segment.Data is PathGeometry pathGeom)
                    {
                        var transform = new TranslateTransform(deltaX, deltaY);
                        if (pathGeom.Transform is TranslateTransform existingTransform)
                        {
                            existingTransform.X += deltaX;
                            existingTransform.Y += deltaY;
                        }
                        else if (pathGeom.Transform == null || pathGeom.Transform == Transform.Identity)
                        {
                            pathGeom.Transform = transform;
                        }
                        else
                        {
                            var group = new TransformGroup();
                            group.Children.Add(pathGeom.Transform);
                            group.Children.Add(transform);
                            pathGeom.Transform = group;
                        }
                    }
                }

                // Move the preview point if it exists (last point in Points collection)
                if (currentPolyline.Points.Count > polylinePoints.Count)
                {
                    var lastPoint = currentPolyline.Points[^1];
                    currentPolyline.Points[^1] = new Point(lastPoint.X + deltaX, lastPoint.Y + deltaY);
                }
            }

            // This ensures arrows stay correctly positioned and scaled
            foreach (var kvp in activationZones.ToArray())
            {
                try
                {
                    // Update zone StartPoint for panning
                    var zone = kvp.Value;
                    if (zone?.Rectangle != null)
                    {
                        double left = Canvas.GetLeft(zone.Rectangle);
                        double top = Canvas.GetTop(zone.Rectangle);
                        zone.StartPoint = new Point(
                            left + zone.Rectangle.Width / 2.0,
                            top + zone.Rectangle.Height
                        );
                        EnsureZoneArrow(zone);
                    }
                }
                catch { }
            }

            // Also update switch zones arrows
            foreach (var kvp in switchZones.ToArray())
            {
                try
                {
                    var zone = kvp.Value;
                    if (zone?.Rectangle != null)
                    {
                        double left = Canvas.GetLeft(zone.Rectangle);
                        double top = Canvas.GetTop(zone.Rectangle);
                        zone.StartPoint = new Point(
                            left + zone.Rectangle.Width / 2.0,
                            top + zone.Rectangle.Height
                        );
                        EnsureZoneArrow(zone);
                    }
                }
                catch { }
            }

            // Move dimension text block if visible
            if (dimensionTextBlock != null && dimensionTextBlock.Visibility == Visibility.Visible)
            {
                Canvas.SetLeft(dimensionTextBlock, Canvas.GetLeft(dimensionTextBlock) + deltaX);
                Canvas.SetTop(dimensionTextBlock, Canvas.GetTop(dimensionTextBlock) + deltaY);
            }
        }

        /// <summary>
        /// Shifts all tiles and overlays by the specified tile offsets.
        /// </summary>
        /// <param name="shiftX">The number of tiles to shift in the X direction.</param>
        /// <param name="shiftY">The number of tiles to shift in the Y direction.</param>
        private void ShiftTiles(int shiftX, int shiftY)
        {
            foreach (var img in TileCanvas.Children.OfType<Image>())
            {
                Canvas.SetLeft(img, Canvas.GetLeft(img) + shiftX * TilePixelSize);
                Canvas.SetTop(img, Canvas.GetTop(img) + shiftY * TilePixelSize);
            }

            foreach (var pt in points)
            {
                if (pt.Ellipse != null)
                {
                    Canvas.SetLeft(pt.Ellipse, Canvas.GetLeft(pt.Ellipse) + shiftX * TilePixelSize);
                    Canvas.SetTop(pt.Ellipse, Canvas.GetTop(pt.Ellipse) + shiftY * TilePixelSize);
                }
                if (pt.Text != null)
                {
                    Canvas.SetLeft(pt.Text, Canvas.GetLeft(pt.Text) + shiftX * TilePixelSize);
                    Canvas.SetTop(pt.Text, Canvas.GetTop(pt.Text) + shiftY * TilePixelSize);
                }
            }

            if (connectionLine != null)
            {
                for (int i = 0; i < connectionLine.Points.Count; i++)
                {
                    connectionLine.Points[i] = new Point(
                        connectionLine.Points[i].X + shiftX * TilePixelSize,
                        connectionLine.Points[i].Y + shiftY * TilePixelSize
                    );
                }
            }
        }

        /// <summary>
        /// Handles the mouse up event on the tile canvas, committing any pan operations.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void TileCanvas_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;

            isDragging = false;
            TileCanvas.ReleaseMouseCapture();

            // Update logical map center based on camera position
            int centerWorldX = cameraX + (int)(TileCanvas.ActualWidth / 2);
            int centerWorldY = cameraY + (int)(TileCanvas.ActualHeight / 2);

            // Convert world pixels back to tile coordinates
            int centerTileX = (int)Math.Floor((double)centerWorldX / TileSize);
            int centerTileY = (int)Math.Floor((double)centerWorldY / TileSize);

            // Convert tile coordinates to lat/lon
            double lon = TileXToLon(centerTileX + 0.5, zoom);
            double lat = TileYToLat(centerTileY + 0.5, zoom);

            SetMapCenter(lat: lat, lon: lon, updateTextBoxes: true);

            // Reproject overlays
            await ReprojectAllZonesOnMapChange();
            ReprojectActiveVehiclesOnMapChange();
            ReprojectReplayOnMapChange();
            await BringAllOverlaysToFrontSafeAsync();

            e.Handled = true;
        }

        /// <summary>
        /// Checks and loads new tiles during panning.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task CheckAndLoadTilesDuringPanAsync()
        {
            if (!isMiddleMousePanning || isTileLoadPending) return;

            // Spočti vizuální střed v pixelové soustavě canvasu
            var canvasCenter = new Point(CanvasSize / 2, CanvasSize / 2);
            var visualCenter = new Point(canvasCenter.X - translate.X, canvasCenter.Y - translate.Y);

            // Přepočet na lat/lon
            var lonlat = CanvasPixelsToLatLon(visualCenter, latitude, longitude, zoom);

            // Přepočet na tile koordináty
            var (tileX, tileY) = LatLonToTileXY(lonlat.Y, lonlat.X, zoom);

            // Jen pokud je nový tile odlišný od posledního commitnutého
            if (tileX != lastTileX || tileY != lastTileY)
            {
                lastTileX = tileX;
                lastTileY = tileY;
                isTileLoadPending = true;

                int startTileX = tileX - TileCount / 2;
                int startTileY = tileY - TileCount / 2;

                await LoadTilesSmoothAsync(startTileX, startTileY);

                isTileLoadPending = false;
            }
        }

        /// <summary>
        /// Loads tiles initially based on the current map center and zoom level, and renders them progressively on the canvas.
        /// </summary>
        private async Task LoadTilesInitialAsync()
        {
            var (centerX, centerY) = LatLonToTileXY(latitude, longitude, zoom);
            _currentTopLeftTileX = centerX - TileCount / 2;
            _currentTopLeftTileY = centerY - TileCount / 2;
            cameraX = _currentTopLeftTileX * TileSize;
            cameraY = _currentTopLeftTileY * TileSize;

            RenderTilesProgressive();
            await Task.Delay(200); // Give tiles time to load
        }

        /// <summary>
        /// Handles the tile shift operation when the visual offset exceeds the tile size.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task HandleTileShiftAsync()
        {
            int shiftX = 0, shiftY = 0;

            if (visualOffsetX > TilePixelSize) { shiftX = -1; visualOffsetX -= TilePixelSize; }
            if (visualOffsetX < -TilePixelSize) { shiftX = 1; visualOffsetX += TilePixelSize; }
            if (visualOffsetY > TilePixelSize) { shiftY = -1; visualOffsetY -= TilePixelSize; }
            if (visualOffsetY < -TilePixelSize) { shiftY = 1; visualOffsetY += TilePixelSize; }

            if (shiftX != 0 || shiftY != 0)
            {
                _currentTopLeftTileX += shiftX;
                _currentTopLeftTileY += shiftY;

                await LoadTilesShiftAsync(shiftX, shiftY);

                // Odstranění starých dlaždic mimo canvas
                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var img in TileCanvas.Children.OfType<Image>().ToList())
                    {
                        double left = Canvas.GetLeft(img);
                        double top = Canvas.GetTop(img);
                        if (left < -TilePixelSize || left > CanvasSize || top < -TilePixelSize || top > CanvasSize)
                            TileCanvas.Children.Remove(img);
                    }
                });
            }
        }

        /// <summary>
        /// Checks and loads new tiles during panning.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task CheckTileShiftAsync()
        {
            int shiftX = 0;
            int shiftY = 0;

            if (visualOffsetX >= TilePixelSize / 2) { shiftX = -1; visualOffsetX -= TilePixelSize; }
            if (visualOffsetX <= -TilePixelSize / 2) { shiftX = 1; visualOffsetX += TilePixelSize; }
            if (visualOffsetY >= TilePixelSize / 2) { shiftY = -1; visualOffsetY -= TilePixelSize; }
            if (visualOffsetY <= -TilePixelSize / 2) { shiftY = 1; visualOffsetY += TilePixelSize; }

            if (shiftX != 0 || shiftY != 0)
            {
                _currentTopLeftTileX += shiftX;
                _currentTopLeftTileY += shiftY;

                await LoadTilesShiftAsync(shiftX, shiftY);

                // Odstranit staré dlaždice, které jsou mimo viditelnou oblast
                await Dispatcher.InvokeAsync(() =>
                {
                    foreach (var img in TileCanvas.Children.OfType<Image>().ToList())
                    {
                        double left = Canvas.GetLeft(img);
                        double top = Canvas.GetTop(img);

                        if (left < -TilePixelSize || left > CanvasSize || top < -TilePixelSize || top > CanvasSize)
                            TileCanvas.Children.Remove(img);
                    }
                });
            }
        }

        /// <summary>
        /// Loads new tiles that have appeared due to the shift in tile coordinates, and places them in the correct position on the canvas.
        /// </summary>
        /// <param name="shiftX">The horizontal shift in tile coordinates.</param>
        /// <param name="shiftY">The vertical shift in tile coordinates.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task LoadTilesShiftAsync(int shiftX, int shiftY)
        {
            _currentTopLeftTileX += shiftX;
            _currentTopLeftTileY += shiftY;

            // Load jen nové dlaždice, které se objevily
            int startX = _currentTopLeftTileX;
            int startY = _currentTopLeftTileY;

            var tasks = new List<Task>();

            for (int x = 0; x < TileCount; x++)
                for (int y = 0; y < TileCount; y++)
                {
                    // Jen nové kraje (např. když shiftX = 1, jen pravý sloupec)
                    if ((shiftX == 1 && x != TileCount - 1) || (shiftX == -1 && x != 0)) continue;
                    if ((shiftY == 1 && y != TileCount - 1) || (shiftY == -1 && y != 0)) continue;

                    int tileX = startX + x;
                    int tileY = startY + y;

                    tasks.Add(Task.Run(async () =>
                    {
                        var bmp = await FetchTileAsync(zoom, tileX, tileY, CancellationToken.None);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            var img = CreateTileImage(bmp);
                            Canvas.SetLeft(img, x * TilePixelSize + visualOffsetX);
                            Canvas.SetTop(img, y * TilePixelSize + visualOffsetY);
                            TileCanvas.Children.Add(img);
                        });
                    }));
                }

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Strips old replay trails from the canvas while keeping the replay points (vehicles) intact.
        /// </summary>
        private void StripReplayTrailsKeepPoints()
        {
            // odstranění starých replay polylines
            var toRemovePolylines = TileCanvas.Children
                .OfType<Polyline>()
                .Where(pl => pl.Tag is string s && s.StartsWith("replay_trail_", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var pl in toRemovePolylines)
                TileCanvas.Children.Remove(pl);

            // odstranění starých replay teček (dots)
            foreach (var vehicle in _replayVehicles.Values.ToList())
            {
                if (vehicle?.TrailDots == null) continue;

                foreach (var dot in vehicle.TrailDots.ToList())
                    TileCanvas.Children.Remove(dot);

                vehicle.TrailDots.Clear();
            }
        }

        /// <summary>
        /// Refreshes the map by loading new tiles and updating the display.
        /// </summary>
        public async void RefreshMap()
        {
            Console.WriteLine($"[REFRESH] Starting refresh...");
            Console.WriteLine($"[CAMERA] Camera: ({cameraX}, {cameraY})");

            int startX = (int)Math.Floor((double)cameraX / TileSize);
            int startY = (int)Math.Floor((double)cameraY / TileSize);

            double offsetX = cameraX - (startX * TileSize);
            double offsetY = cameraY - (startY * TileSize);

            Console.WriteLine($"[MAP] Start tile: ({startX}, {startY}), Offset: ({offsetX:F1}, {offsetY:F1})");

            await LoadTilesSmoothAsync(startX, startY, offsetX, offsetY);

            Console.WriteLine($"[CAMERA] Camera after: ({cameraX}, {cameraY})");
            Console.WriteLine($"[REFRESH] Complete");

            _ = EnsureLocalAreaAltitudeAsync(force: true);
            ResetAllTramTrails();

            await ReprojectAllZonesOnMapChange();
            ReprojectActiveVehiclesOnMapChange();
            ReprojectReplayOnMapChange();
            ReprojectSwitchZonesOnMapChange();
            //ReprojectRailwaysOnMapChange();
            DrawRadiusCircle();

            await Task.Delay(1);
            await BringAllOverlaysToFrontSafeAsync();
        }

        /// <summary>
        /// Converts meters to pixels at the current zoom level and latitude, which is essential for correctly sizing overlays like switch zones and stops on the map.
        /// </summary>
        /// <param name="latitude">The latitude at which to calculate the meters per pixel.</param>
        /// <param name="zoom">The zoom level of the map.</param>
        /// <returns>The number of meters per pixel at the specified latitude and zoom level.</returns>
        private double MetersPerPixel(double latitude, int zoom)
        {
            double earthCircumference = 40075016.686; // in meters
            double latitudeRad = latitude * Math.PI / 180.0;
            return earthCircumference * Math.Cos(latitudeRad) / Math.Pow(2, zoom + 8); // +8 because of 256 px
        }

        /// <summary>
        /// Gets the parent UIElement of the given element that is a direct child of the TileCanvas. 
        /// This is used to determine which overlay element (e.g., stop, zone) was clicked on when handling mouse events.
        /// </summary>
        /// <param name="element">The UIElement for which to find the parent in the TileCanvas.</param>
        /// <returns>The parent UIElement that is a direct child of the TileCanvas, or null if not found.</returns>
        private UIElement? GetParentElementInCanvas(UIElement? element)
        {
            while (element != null && !TileCanvas.Children.Contains(element))
            {
                element = VisualTreeHelper.GetParent(element) as UIElement;
            }
            return element;
        }

        /// <summary>
        /// Updates the latitude and longitude text boxes in the UI to reflect the current center coordinates of the map.
        /// </summary>
        private void UpdateCenterTextBoxesFromFields()
        {
            // prevent recursive RefreshMap while updating text
            LatitudeBox.TextChanged -= LatitudeBox_TextChanged;
            LongitudeBox.TextChanged -= LongitudeBox_TextChanged;
            LatitudeBox.Text = latitude.ToString("F6", CultureInfo.InvariantCulture);
            LongitudeBox.Text = longitude.ToString("F6", CultureInfo.InvariantCulture);
            LatitudeBox.TextChanged += LatitudeBox_TextChanged;
            LongitudeBox.TextChanged += LongitudeBox_TextChanged;
        }

        /// <summary>
        /// Sets map center for current zoom level and position.
        /// </summary>
        /// <param name="lat">The latitude to set as the map center.</param>
        /// <param name="lon">The longitude to set as the map center.</param>
        /// <param name="updateTextBoxes">Whether to update the latitude and longitude text boxes.</param>
        public void SetMapCenter(double lat, double lon, bool updateTextBoxes = true)
        {
            latitude = lat;
            longitude = lon;

            // keep Mapsettings in sync
            Mapsettings.Latitude = lat;
            Mapsettings.Longitude = lon;

            if (updateTextBoxes)
                UpdateCenterTextBoxesFromFields();

            _ = EnsureLocalAreaAltitudeAsync(force: true);
        }

    }
}
