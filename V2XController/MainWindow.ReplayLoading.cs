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
    // Replay file loading, parsing, rendering and replay statistics
    public partial class MainWindow
    {
        private async void LoadPlaybackFile(string fileName)
        {
            StopPlaybackAndReset();
            _replayGeoFrames.Clear();

            try
            {
                SilentClearAll();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[PLAYBACK] Failed clearing existing zones: " + ex.Message);
            }

            if (!File.Exists(fileName))
            {
                MessageBox.Show("File doesn't exist");
                return;
            }

            ShowLoadingOverlay("Loading replay...");
            try
            {
                await LoadPlaybackFileCore(fileName);
            }
            finally
            {
                HideLoadingOverlay();
            }
        }

        /// <summary>
        /// Loads playback file for replay.
        /// </summary>
        /// <param name="fileName">The name of the playback file.</param>
        private async Task LoadPlaybackFileCore(string fileName)
        {
            StopPlaybackAndReset();
            _replayGeoFrames.Clear();

            try
            {
                SilentClearAll();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[PLAYBACK] Failed clearing existing zones: " + ex.Message);
            }

            if (!File.Exists(fileName))
            {
                MessageBox.Show("File doesn't exist");
                return;
            }

            var ext = System.IO.Path.GetExtension(fileName)?.ToLowerInvariant();
            if (ext == ".camrec")
            {
                await LoadCamRecording(fileName);
                return;
            }

            var doc = XDocument.Load(fileName);

            // If its a full MapData recording, load map center/zoom and zones first
            if (string.Equals(doc.Root?.Name.LocalName, "MapData", StringComparison.OrdinalIgnoreCase))
            {
                var root = doc.Root;

                // Center and zoom
                var latAttr = root.Attribute("CenterLatitude")?.Value;
                var lonAttr = root.Attribute("CenterLongitude")?.Value;
                var zoomAttr = root.Attribute("Zoom")?.Value;
                if (latAttr != null && lonAttr != null && zoomAttr != null &&
                    double.TryParse(latAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out var centerLat) &&
                    double.TryParse(lonAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out var centerLon) &&
                    int.TryParse(zoomAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out var z))
                {
                    latitude = centerLat;
                    longitude = centerLon;
                    zoom = z;

                    // Sync UI without triggering refresh
                    Mapsettings.Latitude = latitude;
                    Mapsettings.Longitude = longitude;
                    LatitudeBox.TextChanged -= LatitudeBox_TextChanged;
                    LongitudeBox.TextChanged -= LongitudeBox_TextChanged;
                    LatitudeBox.Text = latitude.ToString("F6", CultureInfo.InvariantCulture);
                    LongitudeBox.Text = longitude.ToString("F6", CultureInfo.InvariantCulture);
                    LatitudeBox.TextChanged += LatitudeBox_TextChanged;
                    LongitudeBox.TextChanged += LongitudeBox_TextChanged;

                    var (centerX, centerY) = LatLonToTileXY(latitude, longitude, zoom);
                    await LoadTilesSmoothAsync(centerX - TileCount / 2, centerY - TileCount / 2);

                }

                // Clear and load zones and rails (unchanged)
                foreach (var rect in activationZones.Keys.ToList())
                    TileCanvas.Children.Remove(rect);
                activationZones.Clear();
                ActivationZonesCollection.Clear();

                var zonesElement = root.Element("ActivationZones");
                if (zonesElement != null)
                {
                    var zoneElems = zonesElement.Elements("Zone").ToList();
                    foreach (var zoneElem in zoneElems)
                    {
                        try
                        {
                            // Parse attributes defensively (do not throw on malformed/missing attrs)
                            string name = zoneElem.Attribute("Name")?.Value ?? "Unnamed";
                            double latitudeZone = double.TryParse(zoneElem.Attribute("Latitude")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var latParsed) ? latParsed : double.NaN;
                            double longitudeZone = double.TryParse(zoneElem.Attribute("Longitude")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var lonParsed) ? lonParsed : double.NaN;
                            double width = double.TryParse(zoneElem.Attribute("Width")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var wParsed) ? wParsed : 0.0;
                            double height = double.TryParse(zoneElem.Attribute("Height")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var hParsed) ? hParsed : 0.0;
                            int azimuth = int.TryParse(zoneElem.Attribute("Azimuth")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aParsed) ? aParsed : 0;
                            string color = zoneElem.Attribute("Color")?.Value ?? "#FF0000";
                            int mainZone = int.TryParse(zoneElem.Attribute("MainZone")?.Value, out var mParsed) ? Math.Clamp(mParsed, 0, 4) : 0;
                            int subZone = int.TryParse(zoneElem.Attribute("SubZone")?.Value, out var sParsed) ? Math.Clamp(sParsed, 0, 6) : 0;
                            double startXZone = double.TryParse(zoneElem.Attribute("StartX")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var sxParsed) ? sxParsed : double.NaN;
                            double startYZone = double.TryParse(zoneElem.Attribute("StartY")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var syParsed) ? syParsed : double.NaN;

                            // All UI changes must run on UI thread because previous awaits may resume on threadpool.
                            Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    // Skip duplicates
                                    if (ZoneAlreadyExists(name, latitudeZone, longitudeZone, width, height, azimuth, color))
                                        return;

                                    // Compute px sizes safely (guard against zero/NaN)
                                    double mpp = MetersPerPixel(latitude, zoom);
                                    double widthPx = width > 0 ? width / mpp : 0;
                                    double heightPx = height > 0 ? height / mpp : 0;

                                    var brush = TryBrushFromColor(color) ?? Brushes.Red;

                                    var rect = new Rectangle
                                    {
                                        Stroke = brush,
                                        StrokeThickness = 2,
                                        Fill = Brushes.Transparent,
                                        Tag = "DrawnRectangle",
                                        Uid = name,
                                        Width = widthPx,
                                        Height = heightPx
                                    };

                                    var activationZone = new ActivationZone
                                    {
                                        Latitude = double.IsNaN(latitudeZone) ? latitude : latitudeZone,
                                        Longitude = double.IsNaN(longitudeZone) ? longitude : longitudeZone,
                                        Width = Math.Round(width, 2),
                                        Height = Math.Round(height, 2),
                                        Azimuth = azimuth,
                                        Color = color,
                                        Rectangle = rect,
                                        StartPoint = double.IsNaN(startXZone) || double.IsNaN(startYZone) ? new Point(CanvasSize / 2.0, CanvasSize / 2.0) : new Point(startXZone, startYZone),
                                        MainZone = mainZone,
                                        SubZone = subZone,
                                        Name = name
                                    };

                                    // register and add
                                    activationZones[rect] = activationZone;
                                    ActivationZonesCollection.Add(activationZone);
                                    activationZone.PropertyChanged += ActivationZone_PropertyChanged;

                                    UpdateRectanglePositionFromStartPoint(activationZone);
                                    TileCanvas.Children.Add(rect);
                                    UpdateActivationZoneBounds(activationZone);
                                }
                                catch (Exception uiEx)
                                {
                                    Console.WriteLine($"[LOADXML] UI creation for zone failed: {uiEx.Message}");
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            // Log parse failure but continue with remaining zones
                            Console.WriteLine($"[LOADXML] Zone parse failed: {ex.Message}");
                        }
                    }

                    recordedCamMessages.Clear();
                    var camMessagesElement = root.Element("CamMessages");
                    if (camMessagesElement != null)
                    {
                        foreach (var camElem in camMessagesElement.Elements("CAM"))
                            AddCamToBuffer(camElem.ToString(SaveOptions.DisableFormatting));
                    }

                    isDirty = false;
                }

            }

            var camElements = doc.Descendants("CAM").ToList();
            var srvElements = doc.Descendants("SrvMessages").Descendants("SRV").ToList();

            // collect all distinct vehicle IDs present in the replay and populate TramBox
            var allVehicleIds = camElements
                .Select(el => el.Element("vehPt")?.Attribute("statId")?.Value)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();
            PopulateTramBoxFromIds(allVehicleIds);

            // Pre-scan earliest across CAM and SRV
            DateTime? earliest = null;
            foreach (var el in camElements)
            {
                var vehPt = el.Element("vehPt");
                string altStr = vehPt.Attribute("alt")?.Value;

                var lastRecStr = vehPt?.Attribute("lastRec")?.Value;
                if (DateTime.TryParse(lastRecStr, null, DateTimeStyles.RoundtripKind, out var t))
                    earliest = !earliest.HasValue || t < earliest ? t : earliest;
            }
            foreach (var se in srvElements)
            {
                var svc = se.Element("service");
                var dtStr = svc?.Attribute("dt")?.Value;
                if (DateTime.TryParse(dtStr, null, DateTimeStyles.RoundtripKind, out var t))
                    earliest = !earliest.HasValue || t < earliest ? t : earliest;
            }

            if (camElements.Count == 0 && srvElements.Count == 0)
            {
                MessageBox.Show("File is empty.");
                return;
            }

            var firstTime = earliest;
            DateTime? minUtc = earliest, maxUtc = earliest;

            _replayFrames.Clear();
            _replayVehicles.Clear();

            // Identify up to 2 vehicles
            var vehicleIds = camElements
                .Select(el => el.Element("vehPt")?.Attribute("statId")?.Value)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .Take(2)
                .ToList();

            ClearPlaybackTramsFromCanvas();

            var tramFrames = new List<MovementFrame>[drawnTrams.Length];
            for (int i = 0; i < tramFrames.Length; i++)
                tramFrames[i] = new List<MovementFrame>();

            _playbackHeadingByIdAndTs.Clear();
            _playbackSpeedByIdAndTs.Clear();

            foreach (var el in camElements)
            {
                try
                {
                    var vehPt = el.Element("vehPt");
                    if (vehPt == null) continue;

                    string id = vehPt.Attribute("statId")?.Value;
                    string latStr = vehPt.Attribute("lat")?.Value;
                    string lngStr = vehPt.Attribute("lng")?.Value;
                    string altStr = vehPt.Attribute("alt")?.Value;
                    string lastRecStr = vehPt.Attribute("lastRec")?.Value;
                    string speedStr = vehPt.Attribute("speed")?.Value;
                    string headingStr = vehPt.Attribute("heading")?.Value;

                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(latStr) || string.IsNullOrEmpty(lngStr) || string.IsNullOrEmpty(lastRecStr))
                        continue;

                    if (!double.TryParse(latStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double lat)) continue;
                    if (!double.TryParse(lngStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double lng)) continue;
                    var timestamp = DateTime.Parse(lastRecStr, null, DateTimeStyles.RoundtripKind);

                    var tsUtc = timestamp.ToUniversalTime();
                    if (!minUtc.HasValue || tsUtc < minUtc) minUtc = tsUtc;
                    if (!maxUtc.HasValue || tsUtc > maxUtc) maxUtc = tsUtc;
                    if (!maxUtc.HasValue || timestamp > maxUtc) maxUtc = timestamp;

                    if (!_replayFrames.TryGetValue(id, out var list))
                    {
                        list = new List<MovementFrame>();
                        _replayFrames[id] = list;
                    }
                    if (firstTime == null) firstTime = timestamp; // base of relative timeline
                    var (rx, ry) = ConvertLatLonToCanvasXY(lat, lng);
                    var relTs = timestamp - firstTime.Value;
                    list.Add(new MovementFrame { Timestamp = relTs, Position = new Point(rx, ry) });

                    if (!_replayGeoFrames.TryGetValue(id, out var geoList))
                    {
                        geoList = new List<(TimeSpan ts, double lat, double lon)>();
                        _replayGeoFrames[id] = geoList;
                    }

                    geoList.Add((relTs, lat, lng));

                    if (double.TryParse(headingStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double hdg))
                    {
                        string keyHead = $"{id}|{relTs.Ticks}";
                        _playbackHeadingByIdAndTs[keyHead] = hdg;
                    }

                    if (double.TryParse(speedStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double spdAll))
                    {
                        string keyAll = $"{id}|{relTs.Ticks}";
                        _playbackSpeedByIdAndTs[keyAll] = spdAll; // m/s expected
                    }

                    if (double.TryParse(altStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var altVal))
                    {
                        string keyAlt = $"{id}|{relTs.Ticks}";
                        _playbackAltitudeByIdAndTs[keyAlt] = altVal;
                    }

                    double accVal = 0.0;
                    var accAttrNames = new[] { "accuracy", "acc", "accuracy_m", "accuracyMeters", "hacc" };
                    foreach (var an in accAttrNames)
                    {
                        var at = vehPt.Attribute(an);
                        if (at != null && double.TryParse(at.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAcc))
                        {
                            accVal = parsedAcc;
                            break;
                        }
                    }
                    if (accVal > 0)
                    {
                        string keyAcc = $"{id}|{relTs.Ticks}";
                        _playbackAccuracyByIdAndTs[keyAcc] = accVal;
                    }

                    int idx = vehicleIds.IndexOf(id);
                    if (idx >= 0 && idx < tramFrames.Length)
                    {
                        if (firstTime == null) firstTime = timestamp;
                        var (x, y) = ConvertLatLonToCanvasXY(lat, lng);

                        tramFrames[idx].Add(new MovementFrame
                        {
                            Timestamp = relTs,
                            Position = new Point(x, y)
                        });

                        if (double.TryParse(speedStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double spd))
                        {
                            string key = $"{id}|{relTs.Ticks}";
                            _playbackSpeedByIdAndTs[key] = spd; // m/s expected
                        }
                    }
                }
                catch { }
            }

            for (int i = 0; i < drawnTrams.Length && i < vehicleIds.Count; i++)
            {
                if (tramFrames[i].Count > 0)
                {
                    var first = tramFrames[i][0];

                    // Create visuals but DO NOT add to canvas now (prevents the "hanging" dot before play)
                    var ellipse = new Ellipse
                    {
                        Width = 12,
                        Height = 12,
                        Fill = drawnTramColors[i],
                        Stroke = Brushes.Black,
                        StrokeThickness = 1,
                        IsHitTestVisible = false
                    };

                    var text = new TextBlock
                    {
                        Text = vehicleIds[i]?.Length > 4 ? vehicleIds[i][^4..] : vehicleIds[i],
                        Foreground = drawnTramColors[i],
                        FontWeight = FontWeights.Bold,
                        FontSize = 12,
                        IsHitTestVisible = false
                    };

                    var speedText = new TextBlock
                    {
                        Text = "",
                        Foreground = drawnTramColors[i],
                        FontWeight = FontWeights.Bold,
                        FontSize = 12,
                        IsHitTestVisible = false
                    };

                    drawnTrams[i] = new MapPoint
                    {
                        Position = first.Position,
                        Label = vehicleIds[i],
                        Ellipse = ellipse,
                        Text = text,
                        Speed = speedText,
                        TrailDots = new List<Ellipse>(),
                        IsRecorded = true,
                        MovementFrames = tramFrames[i],
                        LastUpdate = DateTime.Now
                    };
                }
            }

            _replaySrvFramesById.Clear();
            foreach (var se in srvElements)
            {
                var svc = se.Element("service");
                if (svc == null) continue;

                var id = svc.Attribute("logicalId")?.Value ?? "RSU";
                var latStr = svc.Attribute("lat")?.Value;
                var lngStr = svc.Attribute("lng")?.Value;
                var dtStr = svc.Attribute("dt")?.Value;

                if (!double.TryParse(latStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lat)) continue;
                if (!double.TryParse(lngStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var lon)) continue;
                if (!DateTime.TryParse(dtStr, null, DateTimeStyles.RoundtripKind, out var ts)) continue;

                var rel = ts - firstTime.Value;

                if (!_replaySrvFramesById.TryGetValue(id, out var list))
                {
                    list = new List<(TimeSpan ts, double lat, double lon)>();
                    _replaySrvFramesById[id] = list;
                }
                list.Add((rel, lat, lon));

                if (!maxUtc.HasValue || ts > maxUtc) maxUtc = ts;
            }

            // Normalize SRV lists
            foreach (var kv in _replaySrvFramesById)
                kv.Value.Sort((a, b) => a.ts.CompareTo(b.ts));

            _replayStartUtc = minUtc;
            _replayEndUtc = maxUtc;
            _playbackLoaded = true;
            _lastReplayFile = fileName;

            BuildPlaybackKeyframes();

            playbackElapsedTime = TimeSpan.Zero;
            _playbackIndex = 0;
            ReplaySlider.Value = 0;

            RedrawPlaybackToTime(TimeSpan.Zero);
            UpdateReplayTimerLabel();
            UpdateTimerLabel();

            UpdateUiEnabledState();
            UpdateReplayTimerLabel();
            HideLoadingOverlay();
            MessageBox.Show("CAM recording loaded. Use Play to start playback.", "Playback ready", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// .camrec loader, a simple custom format we defined for easy recording and replay of CAM messages without the full XML structure.
        /// Supports both full CAM-wrapped XML and standalone &lt;vehPt .../&gt; blocks (protobuf recording format).
        /// </summary>
        /// <param name="fileName">The name of the .camrec file to load.</param>
        private async Task LoadCamRecording(string fileName)
        {
            StopPlaybackAndReset();

            var lines = File.ReadAllLines(fileName)
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .ToList();

            _playbackHeadingByIdAndTs.Clear();

            if (lines.Count > 0 && lines[0].StartsWith("#CENTER", StringComparison.OrdinalIgnoreCase))
            {
                var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var cLat) &&
                    double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var cLon) &&
                    int.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var z))
                {
                    latitude = cLat;
                    longitude = cLon;
                    zoom = z;

                    Mapsettings.Latitude = latitude;
                    Mapsettings.Longitude = longitude;
                    LatitudeBox.TextChanged -= LatitudeBox_TextChanged;
                    LongitudeBox.TextChanged -= LongitudeBox_TextChanged;
                    LatitudeBox.Text = latitude.ToString("F6", CultureInfo.InvariantCulture);
                    LongitudeBox.Text = longitude.ToString("F6", CultureInfo.InvariantCulture);
                    LatitudeBox.TextChanged += LatitudeBox_TextChanged;
                    LongitudeBox.TextChanged += LongitudeBox_TextChanged;

                    var (centerX, centerY) = LatLonToTileXY(latitude, longitude, zoom);
                    await LoadTilesSmoothAsync(centerX - TileCount / 2, centerY - TileCount / 2);
                }
                lines.RemoveAt(0);
            }

            ClearPlaybackTramsFromCanvas();

            // Join remaining lines to support multiline <vehPt .../> blocks (protobuf format)
            var rawText = string.Join("\n", lines);

            // Try to extract full CAM-wrapped blocks first; fall back to standalone <vehPt /> (protobuf)
            var camMessages = new List<string>();
            foreach (var line in lines)
            {
                // Protobuf raw hex řádky
                if (IsProtobufMessage(line))
                {
                    camMessages.Add(line);
                    continue;
                }
                // Standardní CAM XML (jednořádkové)
                if (line.Contains("<vehPt", StringComparison.OrdinalIgnoreCase) ||
                    line.TrimStart().StartsWith("<CAM", StringComparison.OrdinalIgnoreCase))
                {
                    camMessages.Add(line);
                }
            }

            // Víceřádkové CAM XML bloky (fallback)
            if (camMessages.Count == 0)
            {
                var camWrapped = System.Text.RegularExpressions.Regex.Matches(
                    rawText, @"<CAM[\s\S]*?</CAM>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in camWrapped)
                    camMessages.Add(m.Value);
            }

            if (camMessages.Count == 0)
            {
                MessageBox.Show("Recording is empty.");
                return;
            }

            // Snapshot map state needed for coordinate conversion (accessed on background thread)
            int snapZoom = zoom;
            int snapCameraX = cameraX;
            int snapCameraY = cameraY;

            int totalMessages = Math.Max(1, camMessages.Count);
            var progressReporter = new Progress<double>(pct =>
            {
                if (_loadingProgressBar != null)
                    _loadingProgressBar.Value = Math.Clamp(pct, 0, 100);
            });
            var _signalFrames = new List<(TimeSpan ts, int intersectionId, TramSignalDirection direction)>();
            TimeSpan _lastProtoTs = TimeSpan.Zero;

            // Parse all messages once on a background thread to avoid freezing the UI
            var (
                allVehicleIds,
                vehicleIds,
                replayFrames,
                replayGeoFrames,
                headingDict,
                speedDict,
                altDict,
                accDict,
                minUtc,
                maxUtc,
                signalFrames

            ) = await Task.Run(() =>
            {
                var _allIds = new List<string>();
                var _vehicleIds = new List<string>();
                var _replayFrames = new Dictionary<string, List<MovementFrame>>();
                var _replayGeoFrames = new Dictionary<string, List<(TimeSpan ts, double lat, double lon)>>();
                var _headingDict = new Dictionary<string, double>();
                var _speedDict = new Dictionary<string, double>();
                var _altDict = new Dictionary<string, double>();
                var _accDict = new Dictionary<string, double>();
                DateTime? _minUtc = null, _maxUtc = null;
                DateTime? _firstTime = null;

                IProgress<double> prog = progressReporter;
                int processed = 0;
                int total = Math.Max(1, camMessages.Count);

                // Synthetic timestamp base for standalone vehPt blocks (protobuf format)
                var protoBase = DateTime.UtcNow;
                int protoIndex = 0;

                foreach (var raw in camMessages)
                {
                    try
                    {
                        V2XMessage msg = null;
                        double accuracyFromProto = 0.0;
                        bool isStandalone = false;
                        TimeSpan? standaloneRelTs = null;

                        processed++;
                        if (processed % 20 == 0 || processed == total)
                            prog.Report(20.0 + 80.0 * processed / total);

                        if (IsProtobufMessage(raw))
                        {
                            // .camrec protobuf lines usually do not contain a reliable replay timestamp.
                            // Give EVERY protobuf message its own timeline point by preserving file order.
                            // This is important for replay stepping: CAM and intersection status must both be keyframes.
                            var protoRelTs = TimeSpan.FromMilliseconds(protoIndex * 100);
                            protoIndex++;
                            standaloneRelTs = protoRelTs;

                            if (!ProtobufParser.TryDecodeProtobufFromHex(raw, out string decoded)) continue;

                            bool isIntersection = decoded.Contains("intersection_status", StringComparison.OrdinalIgnoreCase) ||
                                                  decoded.Contains("intersectionStatus", StringComparison.OrdinalIgnoreCase) ||
                                                  decoded.Contains("intersection_pass_request_status", StringComparison.OrdinalIgnoreCase) ||
                                                  decoded.Contains("intersectionPassRequestStatus", StringComparison.OrdinalIgnoreCase);

                            var protoCam = ProtoCam.ParseFromJson(decoded);
                            if (protoCam == null || string.IsNullOrWhiteSpace(protoCam.VehicleId))
                            {
                                if (isIntersection)
                                {
                                    int intersectionId;

                                    if (!TryExtractIntersectionId(decoded, out intersectionId))
                                    {
                                        intersectionId = 640; // fallback pro tvoje správné návěstidlo
                                        Console.WriteLine("[SIGNAL CAPTURE] intersection_id not found, using fallback 640");
                                    }

                                    var dir = ParseIntersectionDirection(decoded);

                                    if (dir != TramSignalDirection.None)
                                    {
                                        _signalFrames.Add((protoRelTs, intersectionId, dir));

                                        Console.WriteLine(
                                            $"[SIGNAL CAPTURE] Captured frame: ts={protoRelTs:hh\\:mm\\:ss\\.fff}, intersection={intersectionId}, dir={dir}");
                                    }
                                    else
                                    {
                                        Console.WriteLine("[SIGNAL CAPTURE] Intersection found but direction=None");
                                    }
                                }
                                continue;
                            }

                            msg = protoCam.ToV2XMessage();
                            accuracyFromProto = protoCam.AccuracyInMeters ?? 0.0;
                            isStandalone = true;
                        }
                        else
                        {
                            msg = V2XMessageParser.ParseV2XMessage(raw);
                            if (msg == null || msg.MessageType != "CAM" || string.IsNullOrEmpty(msg.VehicleID))
                                continue;
                        }

                        if (!_allIds.Contains(msg.VehicleID))
                            _allIds.Add(msg.VehicleID);

                        if (_vehicleIds.Count < 2 && !_vehicleIds.Contains(msg.VehicleID))
                            _vehicleIds.Add(msg.VehicleID);

                        TimeSpan relTs;
                        DateTime tsUtc;

                        if (standaloneRelTs.HasValue)
                        {
                            // Protobuf .camrec replay uses synthetic timeline based on line order.
                            relTs = standaloneRelTs.Value;
                            tsUtc = protoBase.Add(relTs);
                        }
                        else
                        {
                            tsUtc = msg.Timestamp?.ToUniversalTime() ?? DateTime.MinValue;

                            if (_firstTime == null)
                                _firstTime = msg.Timestamp;

                            relTs = msg.Timestamp - _firstTime.Value ?? TimeSpan.Zero;
                        }

                        if (!_minUtc.HasValue || tsUtc < _minUtc) _minUtc = tsUtc;
                        if (!_maxUtc.HasValue || tsUtc > _maxUtc) _maxUtc = tsUtc;

                        double u = Math.Log(Math.Tan(msg.Latitude.GetValueOrDefault() * Math.PI / 180.0) + 1.0 / Math.Cos(msg.Latitude.GetValueOrDefault() * Math.PI / 180.0)) / Math.PI;
                        double tileX = (msg.Longitude.GetValueOrDefault() + 180.0) / 360.0 * (1 << snapZoom);
                        double tileY = (1.0 - u) / 2.0 * (1 << snapZoom);
                        double rx = tileX * 256 - snapCameraX;
                        double ry = tileY * 256 - snapCameraY;

                        if (!_replayFrames.TryGetValue(msg.VehicleID, out var frameList))
                        {
                            frameList = new List<MovementFrame>();
                            _replayFrames[msg.VehicleID] = frameList;
                        }
                        frameList.Add(new MovementFrame { Timestamp = relTs, Position = new System.Windows.Point(rx, ry) });

                        if (!_replayGeoFrames.TryGetValue(msg.VehicleID, out var geoList))
                        {
                            geoList = new List<(TimeSpan, double, double)>();
                            _replayGeoFrames[msg.VehicleID] = geoList;
                        }
                        geoList.Add((relTs, msg.Latitude ?? 0.0, msg.Longitude ?? 0.0));
                        string keyBase = $"{msg.VehicleID}|{relTs.Ticks}";
                        _headingDict[keyBase] = msg.Heading ?? 0.0;
                        _speedDict[keyBase] = msg.Speed ?? 0.0;

                        // Update for ALL CAM messages — protobuf AND XML
                        // so that subsequent intersection frames get the correct timestamp
                        _lastProtoTs = relTs;

                        if (isStandalone)
                        {
                            if (accuracyFromProto > 0)
                                _accDict[keyBase] = accuracyFromProto;
                        }
                        else
                        {
                            if (TryExtractAltitudeFromCamXml(raw, out var altVal))
                                _altDict[keyBase] = altVal;

                            double accVal = 0.0;
                            try
                            {
                                int vehPtStart = raw.IndexOf("<vehPt", StringComparison.OrdinalIgnoreCase);
                                if (vehPtStart >= 0)
                                {
                                    int tagEnd = raw.IndexOf('>', vehPtStart);
                                    if (tagEnd > vehPtStart)
                                    {
                                        var tag = raw.Substring(vehPtStart, tagEnd - vehPtStart);
                                        foreach (var an in new[] { "accuracy", "acc", "accuracy_m", "accuracyMeters", "hacc" })
                                        {
                                            int idxAttr = tag.IndexOf(an + "=\"", StringComparison.OrdinalIgnoreCase);
                                            if (idxAttr >= 0)
                                            {
                                                int vStart = idxAttr + an.Length + 2;
                                                int vEnd = tag.IndexOf('"', vStart);
                                                if (vEnd > vStart &&
                                                    double.TryParse(tag.Substring(vStart, vEnd - vStart), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAcc))
                                                {
                                                    accVal = parsedAcc;
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { }

                            if (accVal > 0)
                                _accDict[keyBase] = accVal;
                        }

                    }
                    catch { }
                }

                return (_allIds, _vehicleIds, _replayFrames, _replayGeoFrames, _headingDict, _speedDict, _altDict, _accDict, _minUtc, _maxUtc, _signalFrames);
            });

            // Apply parsed results back on the UI thread
            PopulateTramBoxFromIds(allVehicleIds);

            _replayFrames.Clear();
            foreach (var kv in replayFrames) _replayFrames[kv.Key] = kv.Value;

            _replayVehicles.Clear();
            _replayGeoFrames.Clear();
            foreach (var kv in replayGeoFrames) _replayGeoFrames[kv.Key] = kv.Value;

            _playbackSpeedByIdAndTs.Clear();
            foreach (var kv in speedDict) _playbackSpeedByIdAndTs[kv.Key] = kv.Value;

            foreach (var kv in headingDict) _playbackHeadingByIdAndTs[kv.Key] = kv.Value;
            foreach (var kv in altDict) _playbackAltitudeByIdAndTs[kv.Key] = kv.Value;
            foreach (var kv in accDict) _playbackAccuracyByIdAndTs[kv.Key] = kv.Value;

            _replaySignalFrames.Clear();
            foreach (var f in signalFrames)
                _replaySignalFrames.Add(f);

            Console.WriteLine($"[SIGNAL REPLAY] Total signal frames loaded: {_replaySignalFrames.Count}");
            if (_replaySignalFrames.Count > 0)
            {
                Console.WriteLine($"[SIGNAL REPLAY] First: ts={_replaySignalFrames[0].ts:hh\\:mm\\:ss}, dir={_replaySignalFrames[0].direction}");
                Console.WriteLine($"[SIGNAL REPLAY] Last:  ts={_replaySignalFrames[^1].ts:hh\\:mm\\:ss}, dir={_replaySignalFrames[^1].direction}");
            }
            else
            {
                Console.WriteLine("[SIGNAL REPLAY] WARNING: No signal frames found — check that intersection messages exist in the recording");
            }

            var tramFrames = new List<MovementFrame>[drawnTrams.Length];
            for (int i = 0; i < tramFrames.Length; i++)
            {
                if (i < vehicleIds.Count && replayFrames.TryGetValue(vehicleIds[i], out var vf))
                    tramFrames[i] = vf;
                else
                    tramFrames[i] = new List<MovementFrame>();
            }

            for (int i = 0; i < drawnTrams.Length && i < vehicleIds.Count; i++)
            {
                if (tramFrames[i].Count > 0)
                {
                    var first = tramFrames[i][0];

                    var ellipse = new Ellipse
                    {
                        Width = 12,
                        Height = 12,
                        Fill = drawnTramColors[i],
                        Stroke = Brushes.Black,
                        StrokeThickness = 1,
                        IsHitTestVisible = false
                    };

                    var text = new TextBlock
                    {
                        Text = vehicleIds[i]?.Length > 4 ? vehicleIds[i][^4..] : vehicleIds[i],
                        Foreground = drawnTramColors[i],
                        FontWeight = FontWeights.Bold,
                        FontSize = 12,
                        IsHitTestVisible = false
                    };

                    text.Effect = new DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 3,
                        ShadowDepth = 0,
                        Opacity = 1
                    };

                    var speedText = new TextBlock
                    {
                        Text = "",
                        Foreground = drawnTramColors[i],
                        FontWeight = FontWeights.Bold,
                        FontSize = 12,
                        IsHitTestVisible = false
                    };

                    speedText.Effect = new DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 3,
                        ShadowDepth = 0,
                        Opacity = 1
                    };

                    drawnTrams[i] = new MapPoint
                    {
                        Position = first.Position,
                        Label = vehicleIds[i],
                        Ellipse = ellipse,
                        Text = text,
                        Speed = speedText,
                        TrailDots = new List<Ellipse>(),
                        IsRecorded = true,
                        MovementFrames = tramFrames[i],
                        LastUpdate = DateTime.Now
                    };
                }
            }

            playbackMaxTime = tramFrames.Max(frames => frames.Count > 0 ? frames.Last().Timestamp : TimeSpan.Zero);

            if (playbackMaxTime == TimeSpan.Zero)
            {
                HideLoadingOverlay();
                MessageBox.Show(
                    "Failed to load the recording.\n\nThe file has an invalid format or is corrupted.",
                    "Load error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _replayStartUtc = minUtc;
            _replayEndUtc = maxUtc;
            _playbackLoaded = true;
            _lastReplayFile = fileName;

            BuildPlaybackKeyframes();

            playbackElapsedTime = TimeSpan.Zero;
            _playbackIndex = 0;
            ReplaySlider.Value = 0;

            RedrawPlaybackToTime(TimeSpan.Zero);
            UpdateReplayTimerLabel();
            UpdateTimerLabel();
            UpdateReplayStatsForTime(TimeSpan.Zero);
            SyncTramTableForReplay(TimeSpan.Zero);

            Console.WriteLine($"[PLAYBACK] Keyframes loaded: {_keyframes.Count} (CAM/SRV/signal timeline)");

            UpdateUiEnabledState();
            HideLoadingOverlay();
            MessageBox.Show("Playback data loaded. Use Play button to start playback.", "Playback ready", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// Parses a standalone &lt;vehPt .../&gt; block (protobuf recording format).
        /// Handles both comma and dot as decimal separator.
        /// </summary>
        private static (double lat, double lon, double speed, double heading, double accuracy, double altitude)? ParseStandaloneVehPtBlock(string block)
        {
            static double ReadAttr(string text, string name)
            {
                int idx = text.IndexOf(name + "=\"", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return 0.0;
                int start = idx + name.Length + 2;
                int end = text.IndexOf('"', start);
                if (end <= start) return 0.0;
                var raw = text.Substring(start, end - start).Replace(',', '.');
                return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0.0;
            }

            try
            {
                double lat = ReadAttr(block, "lat");
                double lon = ReadAttr(block, "lon");
                if (lat == 0.0 && lon == 0.0) return null;
                return (lat, lon, ReadAttr(block, "speed"), ReadAttr(block, "heading"), ReadAttr(block, "accuracy"), ReadAttr(block, "altitude"));
            }
            catch { return null; }
        }

        /// <summary>
        /// Updates vehicle canvas position, with speed in m/s
        /// </summary>
        /// <param name="vehicle">The vehicle map point to update.</param>
        /// <param name="newPos">The new position of the vehicle.</param>
        /// <param name="color">The color to use for the vehicle visuals.</param>
        /// <param name="isSrv">Indicates if the vehicle is an SRV.</param>
        /// <param name="label">The label of the vehicle.</param>
        /// <param name="speed">The speed of the vehicle in m/s.</param>
        private void UpdateVehicleCanvasPosition(MapPoint vehicle, Point newPos, Brush color, bool isSrv, string label, double? speed = null)
        {
            if (vehicle == null) return;

            // Recreate visuals if they were removed (e.g., during replay/refresh)
            EnsureMapPointVisuals(vehicle, color, isSrv);

            // dot
            Canvas.SetLeft(vehicle.Ellipse, newPos.X - 6);
            Canvas.SetTop(vehicle.Ellipse, newPos.Y - 6);

            // id label s kontrolou shody s nakreslenými body
            string displayText;
            if (isSrv)
            {
                displayText = label;
            }
            else
            {
                string shortId = label?.Length > 4 ? label[^4..] : label;

                bool matchesDrawnPoint = drawnTramIds.Any(drawnId =>
                    drawnId?.Length >= 4 && drawnId[^4..] == shortId);

                displayText = matchesDrawnPoint ? shortId : $"{shortId} (*)";
            }

            vehicle.Text.Text = displayText;
            vehicle.Text.Foreground = isSrv ? Brushes.Black : (color ?? vehicle.Text.Foreground);

            StyleVehicleLabel(vehicle.Text, vehicle.Text.Foreground);
            PositionVehicleLabel(vehicle.Text, newPos, -22);

            if (!TileCanvas.Children.Contains(vehicle.Text))
                TileCanvas.Children.Add(vehicle.Text);

            // speed label handling:
            // - Live CAM (non-SRV, non-simulated): show/update speed in m/s
            // - SRV or simulated trams: hide speed label
            bool isSimulated = drawnTramIds.Contains(vehicle.Label);
            if (!isSrv && !isSimulated)
            {
                if (vehicle.Speed == null)
                {
                    vehicle.Speed = new TextBlock
                    {
                        Text = "",
                        Foreground = color ?? Brushes.Black,
                        FontWeight = FontWeights.Bold,
                        Tag = "Tram",
                        IsHitTestVisible = false
                    };
                    TileCanvas.Children.Add(vehicle.Speed);
                }

                if (speed.HasValue)
                    vehicle.Speed.Text = $"{speed.Value:F1} m/s";

                vehicle.Speed.Foreground = color ?? vehicle.Speed.Foreground;

                StyleVehicleLabel(vehicle.Speed, vehicle.Speed.Foreground);
                PositionVehicleLabel(vehicle.Speed, newPos, -5);

                Panel.SetZIndex(vehicle.Speed, 1100);
                if (!TileCanvas.Children.Contains(vehicle.Speed))
                    TileCanvas.Children.Add(vehicle.Speed);
            }
            else
            {
                if (vehicle.Speed != null)
                {
                    TileCanvas.Children.Remove(vehicle.Speed);
                    vehicle.Speed = null;
                }
            }

            if (_liveAccuracyTextById.TryGetValue(vehicle.Label, out var accText))
            {
                StyleVehicleLabel(accText, Brushes.Gray);
                PositionVehicleLabel(accText, newPos, 12);
                Panel.SetZIndex(accText, 1100);

                if (!TileCanvas.Children.Contains(accText))
                    TileCanvas.Children.Add(accText);
            }

            vehicle.Position = newPos;

            CheckActivationZones(vehicle.Position, vehicle.Label);

            // only drawn trams: record movement for manual recording
            if (isRecording && (vehicle.Label == drawnTramIds[0] || vehicle.Label == drawnTramIds[1]))
            {
                vehicle.MovementFrames.Add(new MovementFrame
                {
                    Timestamp = DateTime.Now - recordingStartTime,
                    Position = newPos
                });
            }

        }

        /// <summary>
        /// Ensures that the visual elements for a map point (vehicle) are created and added to the canvas.
        /// </summary>
        /// <param name="vehicle">The vehicle map point to update.</param>
        /// <param name="color">The color to use for the vehicle visuals.</param>
        /// <param name="isSrv">Indicates if the vehicle is an SRV.</param>
        private void EnsureMapPointVisuals(MapPoint vehicle, Brush color, bool isSrv)
        {
            if (vehicle == null) return;

            if (vehicle.Ellipse == null)
            {
                vehicle.Ellipse = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = isSrv ? Brushes.Red : (color ?? Brushes.Black),
                    Tag = isSrv ? "Srv" : "Tram",
                    IsHitTestVisible = false
                };
                TileCanvas.Children.Add(vehicle.Ellipse);
                Panel.SetZIndex(vehicle.Ellipse, 1000);
            }

            if (vehicle.Text == null)
            {
                vehicle.Text = new TextBlock
                {
                    Text = vehicle.Label ?? "",
                    Foreground = isSrv ? Brushes.Black : (color ?? Brushes.Black),
                    FontWeight = FontWeights.Bold,
                    Tag = isSrv ? "Srv" : "Tram",
                    IsHitTestVisible = false
                };
                TileCanvas.Children.Add(vehicle.Text);
                Panel.SetZIndex(vehicle.Text, 1000);
            }
        }

        /// <summary>
        /// Updates or adds vehicle data in the table.
        /// </summary>
        /// <param name="vehicleId">The ID of the vehicle.</param>
        /// <param name="speed">The speed of the vehicle.</param>
        /// <param name="messageTime">The time of the message.</param>
        /// <param name="isReplay">Indicates if the data is from a replay.</param>
        private void UpdateOrAddVehicleData(string vehicleId, double speed, DateTime messageTime, bool isReplay = false)
        {
            DateTime displayLocalTime = TimeZoneInfo.ConvertTimeFromUtc(messageTime.ToUniversalTime(), czechTimeZone);
            string camTimeStr = displayLocalTime.ToString("HH:mm:ss");

            if (isReplay)
            {
                var existingReplay = TramTable.FirstOrDefault(t => t.VehicleId == vehicleId);
                if (existingReplay == null)
                {
                    TramTable.Add(new TramInfo
                    {
                        VehicleId = vehicleId,
                        Speed = speed,
                        LastCamTime = camTimeStr,
                        SecondsSinceLastCam = 0,
                        LastMessageTimestamp = null
                    });
                }
                else
                {
                    existingReplay.Speed = speed;
                    existingReplay.LastCamTime = camTimeStr;
                    existingReplay.LastMessageTimestamp = null;
                }
                return;
            }

            DateTime lastMsgStamp = DateTime.Now; // arrival time on live feed
            var existing = TramTable.FirstOrDefault(t => t.VehicleId == vehicleId);
            if (existing == null)
            {
                TramTable.Add(new TramInfo
                {
                    VehicleId = vehicleId,
                    Speed = speed,
                    LastCamTime = camTimeStr,
                    SecondsSinceLastCam = 0,
                    LastMessageTimestamp = lastMsgStamp
                });
            }
            else
            {
                existing.Speed = speed;
                existing.LastCamTime = camTimeStr;
                existing.SecondsSinceLastCam = 0;
                existing.LastMessageTimestamp = lastMsgStamp;
            }
        }

        /// <summary>
        /// Starts the cleanup timer for old vehicles.
        /// </summary>
        private void StartCleanupTimer()
        {
            if (cleanupTimer != null)
                return;

            cleanupTimer = new DispatcherTimer();
            cleanupTimer.Interval = TimeSpan.FromSeconds(1);
            cleanupTimer.Tick += CleanupOldVehicles;
            cleanupTimer.Start();
        }

        /// <summary>
        /// Handles property changes for an activation zone.
        /// </summary>
        /// <param name="sender">The activation zone that changed.</param>
        /// <param name="e">The property change event arguments.</param>
        private void ActivationZone_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (isUpdatingActivationZone) return;
            isUpdatingActivationZone = true;

            try
            {
                var zone = sender as ActivationZone;
                if (zone == null) return;

                // Handle polyline segment changes
                if (zone.IsPolylineSegment)
                {
                    HandlePolylineSegmentPropertyChange(zone, e.PropertyName ?? string.Empty);
                    isUpdatingActivationZone = false;
                    return;
                }

                // Original rectangle zone handling
                if (zone.Rectangle == null) return;

                double mpp = MetersPerPixel(latitude, zoom);

                if (e.PropertyName == nameof(ActivationZone.Width) || e.PropertyName == nameof(ActivationZone.Height))
                {
                    double widthPx = zone.Width / mpp;
                    double heightPx = zone.Height / mpp;

                    zone.Rectangle.Width = widthPx;
                    zone.Rectangle.Height = heightPx;

                    UpdateRectanglePositionFromStartPoint(zone);
                    UpdateActivationZoneBounds(zone);

                    var baseCenterPoint = new Point(
                        Canvas.GetLeft(zone.Rectangle) + zone.Rectangle.Width / 2.0,
                        Canvas.GetTop(zone.Rectangle) + zone.Rectangle.Height);
                    var latlon = CanvasPixelsToLatLon(baseCenterPoint, latitude, longitude, zoom);
                    zone.Latitude = latlon.Y;
                    zone.Longitude = latlon.X;

                    try { EnsureZoneArrow(zone); } catch { }
                }
                else if (e.PropertyName == nameof(ActivationZone.Azimuth))
                {
                    ApplyZoneRotation(zone);
                    UpdateActivationZoneBounds(zone);
                    RevalidateZoneActiveState(zone);

                    try { EnsureZoneArrow(zone); } catch { }
                }
                else if (e.PropertyName == nameof(ActivationZone.Color))
                {
                    try
                    {
                        var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;
                        zone.Rectangle.Stroke = brush;
                        EnsureZoneArrow(zone);
                    }
                    catch { }
                }
                else if (e.PropertyName == nameof(ActivationZone.Latitude) ||
                         e.PropertyName == nameof(ActivationZone.Longitude))
                {
                    var (x, y) = ConvertLatLonToCanvasXY(zone.Latitude, zone.Longitude);
                    zone.StartPoint = new Point(x, y);

                    UpdateRectanglePositionFromStartPoint(zone);
                    UpdateActivationZoneBounds(zone);

                    try { EnsureZoneArrow(zone); } catch { }

                    isDirty = true;
                }
            }
            finally
            {
                isUpdatingActivationZone = false;
            }
        }

        /// <summary>
        /// Handles property changes for a polyline segment.
        /// </summary>
        /// <param name="segment">The polyline segment that changed.</param>
        /// <param name="propertyName">The name of the property that changed.</param>
        private void HandlePolylineSegmentPropertyChange(ActivationZone segment, string propertyName)
        {
            if (!segment.PolylineId.HasValue) return;

            var polyline = _polylineToSegmentZones
                .FirstOrDefault(kvp => kvp.Value.Any(z => z.PolylineId == segment.PolylineId))
                .Key;

            if (polyline == null) return;

            var allSegments = _polylineToSegmentZones[polyline]
                .OrderBy(s => s.SegmentIndex)
                .ToList();

            // *** ZMĚNA MainZone  automatická změna barvy ***
            if (propertyName == nameof(ActivationZone.MainZone))
            {
                string newColor = GetColorForMainZone(segment.MainZone, IsSwitchMode());

                // Nastavit barvu (to vyvolá další PropertyChanged pro Color)
                if (segment.Color != newColor)
                {
                    segment.Color = newColor;
                    Console.WriteLine($"[POLYLINE] MainZone{segment.MainZone}  Color={newColor} for seg {segment.SegmentIndex}");
                }

                if (_polylineGeoPoints.TryGetValue(polyline, out var geoPoints))
                {
                    var points = geoPoints.Select(gp =>
                    {
                        var (x, y) = ConvertLatLonToCanvasXY(gp.lat, gp.lon);
                        return new Point(x, y);
                    }).ToList();

                    RebuildPolylineZoneWithVariableWidths(polyline, points);
                    UpdatePolylineVertexPositions(polyline, points);
                }

                isDirty = true;
                return;
            }

            if (propertyName == nameof(ActivationZone.Color))
            {
                if (_polylineGeoPoints.TryGetValue(polyline, out var geoPoints))
                {
                    var points = geoPoints.Select(gp =>
                    {
                        var (x, y) = ConvertLatLonToCanvasXY(gp.lat, gp.lon);
                        return new Point(x, y);
                    }).ToList();

                    RebuildPolylineZoneWithVariableWidths(polyline, points);
                    UpdatePolylineVertexPositions(polyline, points);
                }

                Console.WriteLine($"[POLYLINE] Color changed to {segment.Color} for segment {segment.SegmentIndex}");
            }
            else if (propertyName == nameof(ActivationZone.Width))
            {
                if (_polylineGeoPoints.TryGetValue(polyline, out var geoPoints))
                {
                    var points = geoPoints.Select(gp =>
                    {
                        var (x, y) = ConvertLatLonToCanvasXY(gp.lat, gp.lon);
                        return new Point(x, y);
                    }).ToList();

                    RebuildPolylineZoneWithVariableWidths(polyline, points);
                    UpdatePolylineVertexPositions(polyline, points);
                }

                Console.WriteLine($"[POLYLINE] Width changed to {segment.Width}m for segment {segment.SegmentIndex}");
            }
            else if (propertyName == nameof(ActivationZone.Height))
            {
                if (_polylineGeoPoints.TryGetValue(polyline, out var geoPoints) &&
                    segment.SegmentIndex >= 0 &&
                    segment.SegmentIndex < geoPoints.Count - 1)
                {
                    var (lat1, lon1) = geoPoints[segment.SegmentIndex];
                    var newEndPoint = CalculateEndPoint(lat1, lon1, segment.Azimuth, segment.Height);
                    geoPoints[segment.SegmentIndex + 1] = newEndPoint;

                    for (int i = segment.SegmentIndex + 1; i < allSegments.Count; i++)
                    {
                        var seg = allSegments[i];
                        var (startLat, startLon) = geoPoints[i];
                        var (endLat, endLon) = geoPoints[i + 1];

                        seg.Latitude = (startLat + endLat) / 2;
                        seg.Longitude = (startLon + endLon) / 2;
                        seg.Height = HaversineMeters(startLat, startLon, endLat, endLon);
                    }

                    var points = geoPoints.Select(gp =>
                    {
                        var (x, y) = ConvertLatLonToCanvasXY(gp.lat, gp.lon);
                        return new Point(x, y);
                    }).ToList();

                    RebuildPolylineZoneWithVariableWidths(polyline, points);

                    polyline.Points.Clear();
                    foreach (var pt in points)
                        polyline.Points.Add(pt);

                    UpdatePolylineVertexPositions(polyline, points);
                }

                Console.WriteLine($"[POLYLINE] Height changed to {segment.Height}m for segment {segment.SegmentIndex}");
            }
            else if (propertyName == nameof(ActivationZone.Azimuth))
            {
                if (_polylineGeoPoints.TryGetValue(polyline, out var geoPoints) &&
                    segment.SegmentIndex >= 0 &&
                    segment.SegmentIndex < geoPoints.Count - 1)
                {
                    var (lat1, lon1) = geoPoints[segment.SegmentIndex];
                    var newEndPoint = CalculateEndPoint(lat1, lon1, segment.Azimuth, segment.Height);
                    geoPoints[segment.SegmentIndex + 1] = newEndPoint;

                    segment.Latitude = (lat1 + newEndPoint.lat) / 2;
                    segment.Longitude = (lon1 + newEndPoint.lon) / 2;

                    var points = geoPoints.Select(gp =>
                    {
                        var (x, y) = ConvertLatLonToCanvasXY(gp.lat, gp.lon);
                        return new Point(x, y);
                    }).ToList();

                    RebuildPolylineZoneWithVariableWidths(polyline, points);

                    polyline.Points.Clear();
                    foreach (var pt in points)
                        polyline.Points.Add(pt);
                    UpdatePolylineDirectionArrows(polyline, points);
                    UpdatePolylineVertexPositions(polyline, points);
                }

                Console.WriteLine($"[POLYLINE] Azimuth changed to {segment.Azimuth} for segment {segment.SegmentIndex}");
            }
            else if (propertyName == nameof(ActivationZone.Latitude) ||
                     propertyName == nameof(ActivationZone.Longitude))
            {
                if (_polylineGeoPoints.TryGetValue(polyline, out var geoPoints) &&
                    segment.SegmentIndex >= 0)
                {
                    double halfLength = segment.Height / 2.0;

                    var startPoint = CalculateEndPoint(segment.Latitude, segment.Longitude,
                        (segment.Azimuth + 180) % 360, halfLength);
                    var endPoint = CalculateEndPoint(segment.Latitude, segment.Longitude,
                        segment.Azimuth, halfLength);

                    geoPoints[segment.SegmentIndex] = startPoint;
                    if (segment.SegmentIndex + 1 < geoPoints.Count)
                    {
                        geoPoints[segment.SegmentIndex + 1] = endPoint;
                    }

                    var points = geoPoints.Select(gp =>
                    {
                        var (x, y) = ConvertLatLonToCanvasXY(gp.lat, gp.lon);
                        return new Point(x, y);
                    }).ToList();

                    RebuildPolylineZoneWithVariableWidths(polyline, points);

                    polyline.Points.Clear();
                    foreach (var pt in points)
                        polyline.Points.Add(pt);

                    UpdatePolylineVertexPositions(polyline, points);
                }

                Console.WriteLine($"[POLYLINE] Position changed to ({segment.Latitude}, {segment.Longitude}) for segment {segment.SegmentIndex}");
            }

            isDirty = true;
        }

        /// <summary>
        /// Updates the positions of the vertex dots for a given polyline.
        /// </summary>
        /// <param name="polyline">The polyline whose vertex dots need to be updated.</param>
        /// <param name="points">The list of points representing the polyline's vertices.</param>
        private void UpdatePolylineVertexPositions(Polyline polyline, List<Point> points)
        {
            // Find all vertex dots belonging to this polyline
            var vertexDots = _polylineVertexMap
                .Where(kvp => kvp.Value.polyline == polyline)
                .OrderBy(kvp => kvp.Value.pointIndex)
                .ToList();

            foreach (var kvp in vertexDots)
            {
                var dot = kvp.Key;
                var pointIndex = kvp.Value.pointIndex;

                if (pointIndex >= 0 && pointIndex < points.Count)
                {
                    var newPos = points[pointIndex];

                    // Update vertex dot position
                    Canvas.SetLeft(dot, newPos.X - dot.Width / 2);
                    Canvas.SetTop(dot, newPos.Y - dot.Height / 2);

                    // Update associated circle if exists
                    if (_polylineVertexToCircle.TryGetValue(dot, out var circle))
                    {
                        Canvas.SetLeft(circle, newPos.X - circle.Width / 2);
                        Canvas.SetTop(circle, newPos.Y - circle.Height / 2);
                    }
                }
            }
        }

        /// <summary>
        /// Calculates the endpoint coordinates given a start point, azimuth, and distance.
        /// </summary>
        /// <param name="startLat">The starting latitude.</param>
        /// <param name="startLon">The starting longitude.</param>
        /// <param name="azimuthDeg">The azimuth in degrees.</param>
        /// <param name="distanceMeters">The distance in meters.</param>
        /// <returns>The calculated endpoint coordinates as a tuple (latitude, longitude).</returns>
        private (double lat, double lon) CalculateEndPoint(double startLat, double startLon, int azimuthDeg, double distanceMeters)
        {
            const double EarthRadiusMeters = 6371000.0;

            double azimuthRad = azimuthDeg * Math.PI / 180.0;
            double latRad = startLat * Math.PI / 180.0;
            double lonRad = startLon * Math.PI / 180.0;

            double angularDistance = distanceMeters / EarthRadiusMeters;

            double newLatRad = Math.Asin(
                Math.Sin(latRad) * Math.Cos(angularDistance) +
                Math.Cos(latRad) * Math.Sin(angularDistance) * Math.Cos(azimuthRad)
            );

            double newLonRad = lonRad + Math.Atan2(
                Math.Sin(azimuthRad) * Math.Sin(angularDistance) * Math.Cos(latRad),
                Math.Cos(angularDistance) - Math.Sin(latRad) * Math.Sin(newLatRad)
            );

            double newLat = newLatRad * 180.0 / Math.PI;
            double newLon = newLonRad * 180.0 / Math.PI;

            return (newLat, newLon);
        }

        /// <summary>
        /// Finds the polyline that contains a given segment.
        /// </summary>
        /// <param name="segment">The segment to search for.</param>
        /// <returns>The polyline containing the segment, or null if not found.</returns>
        private Polyline? FindPolylineContainingSegment(ActivationZone? segment)
        {
            if (segment == null) return null;

            foreach (var kvp in _polylineGeoPoints)
            {
                var polyline = kvp.Key;
                var geoPoints = kvp.Value;

                for (int i = 0; i < geoPoints.Count - 1; i++)
                {
                    var (lat1, lon1) = geoPoints[i];
                    var (lat2, lon2) = geoPoints[i + 1];

                    double centerLat = (lat1 + lat2) / 2;
                    double centerLon = (lon1 + lon2) / 2;

                    if (Math.Abs(segment.Latitude - centerLat) < 0.00001 &&
                        Math.Abs(segment.Longitude - centerLon) < 0.00001)
                    {
                        return polyline;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Writes the camera recording file with the current map center and zoom.
        /// </summary>
        /// <param name="filePath">The file path to write the camera recording file.</param>
        /// <param name="camLines">The lines representing the camera recording data.</param>
        private void WriteCamrecWithCenter(string filePath, IEnumerable<string> camLines)
        {
            using var sw = new StreamWriter(filePath, false, Encoding.UTF8);
            // Header: current map center and zoom
            sw.WriteLine($"#CENTER {latitude.ToString(CultureInfo.InvariantCulture)} {longitude.ToString(CultureInfo.InvariantCulture)} {zoom}");
            foreach (var cam in camLines)
                sw.WriteLine(cam);
        }

        // ===== Methods for rotating rectangles around their base center =====

        /// <summary>
        /// Rotates a rectangle around its start point.
        /// </summary>
        /// <param name="rect">The rectangle to rotate.</param>
        /// <param name="angleDegrees">The rotation angle in degrees.</param>
        /// <param name="startPointCanvas">The start point on the canvas.</param>
        /// <param name="rectWidth">The width of the rectangle.</param>
        /// <param name="rectHeight">The height of the rectangle.</param>
        private void RotateRectangleAroundStartPoint(Rectangle rect, double angleDegrees, Point startPointCanvas, double rectWidth, double rectHeight)
        {
            if (rect == null) return;
            rect.RenderTransform = new RotateTransform(angleDegrees, rect.Width / 2.0, rect.Height);
        }

        /// <summary>
        /// Transforms longitude to tile X coordinate at a given zoom level.
        /// </summary>
        /// <param name="lon">The longitude to transform.</param>
        /// <param name="zoom">The zoom level.</param>
        /// <returns>The tile X coordinate.</returns>
        private double LonToTileX(double lon, int zoom)
        {
            return (lon + 180.0) / 360.0 * (1 << zoom);
        }

        /// <summary>
        /// Transforms latitude to tile Y coordinate at a given zoom level.
        /// </summary>
        /// <param name="lat">The latitude to transform.</param>
        /// <param name="zoom">The zoom level.</param>
        /// <returns>The tile Y coordinate.</returns>
        private double LatToTileY(double lat, int zoom)
        {
            double latRad = lat * Math.PI / 180.0;
            return (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * (1 << zoom);
        }

        /// <summary>
        /// Transforms tile X coordinate to longitude at a given zoom level.
        /// </summary>
        /// <param name="x">The tile X coordinate.</param>
        /// <param name="zoom">The zoom level.</param>
        /// <returns>The longitude.</returns>
        private double TileXToLon(double x, int zoom)
        {
            return x / Math.Pow(2, zoom) * 360.0 - 180.0;
        }

        /// <summary>
        /// Transforms tile Y coordinate to latitude at a given zoom level.
        /// </summary>
        /// <param name="y">The tile Y coordinate.</param>
        /// <param name="zoom">The zoom level.</param>
        /// <returns>The latitude.</returns>
        private double TileYToLat(double y, int zoom)
        {
            double n = Math.PI - (2.0 * Math.PI * y) / Math.Pow(2, zoom);
            return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
        }

        /// <summary>
        /// Converts canvas pixel coordinates to latitude and longitude.
        /// </summary>
        /// <param name="pixelPoint">The pixel point on the canvas.</param>
        /// <param name="centerLat">The latitude of the map center.</param>
        /// <param name="centerLon">The longitude of the map center.</param>
        /// <param name="zoom">The zoom level.</param>
        /// <returns>The latitude and longitude as a Point.</returns>
        private Point CanvasPixelsToLatLon(Point pixelPoint, double centerLat, double centerLon, int zoom)
        {
            // Convert canvas pixel to world pixel
            double worldX = pixelPoint.X + cameraX;
            double worldY = pixelPoint.Y + cameraY;

            // Convert world pixel to tile coordinates (fractional)
            double tileX = worldX / TileSize;
            double tileY = worldY / TileSize;

            // Convert tile coordinates to lat/lon
            double lon = TileXToLon(tileX, zoom);
            double lat = TileYToLat(tileY, zoom);

            return new Point(lon, lat);
        }

        /// <summary>
        /// Applies rotation to the specified activation zone.
        /// </summary>
        /// <param name="zone">The activation zone to rotate.</param>
        public void ApplyZoneRotation(ActivationZone zone)
        {
            if (zone?.Rectangle == null) return;

            var rect = zone.Rectangle;
            double w = rect.Width;
            double h = rect.Height;

            // If there's a TransformGroup and it contains a ScaleTransform (our hover), keep it and set/update the RotateTransform child.
            if (rect.RenderTransform is TransformGroup tg)
            {
                // find scale (if any) and rotate (if any)
                var scale = tg.Children.OfType<ScaleTransform>().FirstOrDefault();
                var rotate = tg.Children.OfType<RotateTransform>().FirstOrDefault();

                if (rotate != null)
                {
                    // update existing rotate transform
                    rotate.Angle = zone.Azimuth;
                    rotate.CenterX = w / 2.0;
                    rotate.CenterY = h;
                }
                else
                {
                    // create new rotate and add it after scale (so rotate applies after scale)
                    var newRotate = new RotateTransform(zone.Azimuth, w / 2.0, h);
                    if (scale != null)
                        tg.Children.Add(newRotate);
                    else
                    {
                        // no scale present, insert at front (behaviour same as before)
                        tg.Children.Add(newRotate);
                    }
                }

                rect.RenderTransform = tg;
                EnsureZoneArrow(zone);
            }
            else if (rect.RenderTransform is ScaleTransform existingScale)
            {
                // If there's only a ScaleTransform (unlikely) keep it and add rotate
                var group = new TransformGroup();
                group.Children.Add(existingScale);
                group.Children.Add(new RotateTransform(zone.Azimuth, w / 2.0, h));
                rect.RenderTransform = group;
            }
            else
            {
                // simple case - set rotate transform (no hover scale currently)
                rect.RenderTransform = new RotateTransform(zone.Azimuth, w / 2.0, h);
                EnsureZoneArrow(zone);
            }
            try { EnsureZoneArrow(zone); } catch { }
        }
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||

        /// <summary>
        /// Loads stops from OpenStreetMap (OSM) using the Overpass API.
        /// </summary>
        /// <returns>A list of stops.</returns>
        async Task<List<Stop>> LoadStopsFromOSM()
        {
            string query = @"
                [out:json][timeout:25];
                (
                  node[""railway""=""tram_stop""](49.70,18.05,49.90,18.40);
                  node[""public_transport""=""platform""][""tram""=""yes""](49.70,18.05,49.90,18.40);
                  node[""tram""=""yes""](49.70,18.05,49.90,18.40);
                );
                out body;
                ";

            using var client = new HttpClient();

            client.DefaultRequestHeaders.UserAgent.ParseAdd("V2XController/1.0");
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json")
            );

            string body = "data=" + Uri.EscapeDataString(query);

            using var content = new StringContent(
                body,
                Encoding.UTF8,
                "application/x-www-form-urlencoded"
            );

            using var response = await client.PostAsync(
                "https://overpass-api.de/api/interpreter",
                content
            );

            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Overpass chyba: {(int)response.StatusCode}\n\n{responseText}");
                return new List<Stop>();
            }

            using var doc = JsonDocument.Parse(responseText);
            var elements = doc.RootElement.GetProperty("elements");

            Console.WriteLine($"OSM elements: {elements.GetArrayLength()}");

            List<Stop> stops = new List<Stop>();

            foreach (var el in elements.EnumerateArray())
            {
                if (!el.TryGetProperty("lat", out var latEl)) continue;
                if (!el.TryGetProperty("lon", out var lonEl)) continue;

                string name = "Bez názvu";

                if (el.TryGetProperty("tags", out var tags))
                {
                    if (tags.TryGetProperty("name", out var stopNameEl))
                    {
                        name = stopNameEl.GetString() ?? "Bez názvu";
                    }
                }

                stops.Add(new Stop
                {
                    StopName = name,
                    Latitude = latEl.GetDouble(),
                    Longitude = lonEl.GetDouble()
                });
            }

            return stops;
        }

        /// <summary>
        /// Draws stops on the canvas safely, ensuring thread safety.
        /// </summary>
        //private void DrawStopsOnCanvasSafe()
        //{
        //    if (TileCanvas == null) return;

        //    Dispatcher.Invoke(() =>
        //    {
        //        if (stops == null || stops.Count == 0)
        //            return;

        //        // Remove previous stop visuals
        //        foreach (var el in TileCanvas.Children.OfType<FrameworkElement>()
        //                     .Where(el => Equals(el.Tag, "Stop"))
        //                     .ToList())
        //        {
        //            TileCanvas.Children.Remove(el);
        //        }

        //        foreach (var stop in stops)
        //        {
        //            var (x, y) = ConvertLatLonToCanvasXY(stop.Latitude, stop.Longitude);

        //            var stopMarker = new Ellipse
        //            {
        //                Width = 7,
        //                Height = 7,
        //                Fill = Brushes.Red,
        //                Stroke = Brushes.White,
        //                StrokeThickness = 1,
        //                Tag = "Stop",
        //                IsHitTestVisible = false
        //            };
        //            Canvas.SetLeft(stopMarker, x - 4);
        //            Canvas.SetTop(stopMarker, y - 4);
        //            Panel.SetZIndex(stopMarker, 500);
        //            TileCanvas.Children.Add(stopMarker);

        //            var stopLabel = new TextBlock
        //            {
        //                Text = stop.StopName,
        //                FontWeight = FontWeights.Bold,
        //                Foreground = Brushes.Black,
        //                FontSize = 11,
        //                Tag = "Stop",
        //                IsHitTestVisible = false
        //            };
        //            Canvas.SetLeft(stopLabel, x + 6);
        //            Canvas.SetTop(stopLabel, y - 6);
        //            Panel.SetZIndex(stopLabel, 101);
        //            TileCanvas.Children.Add(stopLabel);
        //        }
        //    });
        //}

        /// <summary>
        /// Transforms latitude and longitude to pixel X and Y coordinates on the map (px) for a given zoom level.
        /// </summary>
        /// <param name="lat">The latitude in degrees.</param>
        /// <param name="lon">The longitude in degrees.</param>
        /// <param name="zoom">The zoom level.</param>
        /// <returns>A tuple containing the pixel X and Y coordinates.</returns>
        private static (double pixelX, double pixelY) LatLonToPixelXY(double lat, double lon, int zoom)
        {
            double tileSize = 256.0;
            double scale = Math.Pow(2, zoom);

            double pixelX = (lon + 180.0) / 360.0 * tileSize * scale;
            double latRad = lat * Math.PI / 180.0;
            double pixelY = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * tileSize * scale;

            return (pixelX, pixelY);
        }

        //////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Handler for latitude text box changes. Parses the input, 
        /// updates the map center, and refreshes the map. Clamps latitude to Web Mercator bounds.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>

        private void LatitudeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var raw = LatitudeBox.Text?.Trim() ?? string.Empty;
            raw = raw.Replace(',', '.'); // allow CZ comma
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
            {
                lat = Math.Clamp(lat, -85.05112878, 85.05112878); // Web Mercator bounds
                SetMapCenter(lat, longitude, updateTextBoxes: false); // don't rewrite while user is typing
                RefreshMap();
                _ = EnsureLocalAreaAltitudeAsync(force: true);
            }
        }

        /// <summary>
        /// Handler for latitude text box changes. Parses the input, 
        /// updates the map center, and refreshes the map. Clamps latitude to Web Mercator bounds.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LongitudeBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var raw = LongitudeBox.Text?.Trim() ?? string.Empty;
            raw = raw.Replace(',', '.'); // allow CZ comma
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
            {
                lon = Math.Clamp(lon, -180.0, 180.0);
                SetMapCenter(latitude, lon, updateTextBoxes: false); // don't rewrite while user is typing
                RefreshMap();
                _ = EnsureLocalAreaAltitudeAsync(force: true);
            }
        }

        /// <summary>
        /// Handler for rectangle button click. Activates rectangle drawing mode.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void rectButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[RECTANGLE] Rectangle drawing mode activated");
            isSelectionMode = false;
            currentDrawingMode = DrawingMode.Rectangle;

            if (PolylineWidthPanel != null)
                PolylineWidthPanel.Visibility = Visibility.Collapsed;

            Keyboard.Focus(this);
        }

        /// <summary>
        /// Handler for stop drawing button click. Deactivates current drawing mode and returns to selection mode.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void StopDrawing_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[STOP DRAWING] Stop drawing clicked");

            SetSelectionMode();
            isSelectionMode = true;

            if (PolylineWidthPanel != null)
                PolylineWidthPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Handler for polyline button click. Activates polyline drawing mode.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PolylineButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[POLYLINE] Polyline drawing mode activated");

            isSelectionMode = false;
            currentDrawingMode = DrawingMode.Polyline;
            _isDrawingPolyline = false;

            currentPolyline = null;
            polylinePoints.Clear();
            polylineVertexDots.Clear();
            _currentPolylineCircles.Clear();
            _currentPolylineSegments.Clear();
            _polylineCommittedPointsCount = 0;
            _currentPolylineCircleGeoPoints.Clear();

            if (PolylineWidthPanel != null)
                PolylineWidthPanel.Visibility = Visibility.Visible;

            Keyboard.Focus(this);
        }

        /// <summary>
        /// Handler for draw points button click. Activates tram simulation mode.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DrawPoints_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[DRAW POINTS] Tram simulation mode activated");
            isSelectionMode = false;
            currentDrawingMode = DrawingMode.Point;

            if (PolylineWidthPanel != null)
                PolylineWidthPanel.Visibility = Visibility.Collapsed;

            Keyboard.Focus(this);
        }

        /// <summary>
        /// Redraws playback to specific time, used for both timeline scrubbing and auto-playback. 
        /// Updates vehicle positions based on replay frames, applies filtering,.
        /// </summary>
        /// <param name="time">The time to which playback should be redrawn.</param>
        private void RedrawPlaybackToTime(TimeSpan time)
        {
            playbackElapsedTime = time;
            _playbackIndex = GetIndexForTime(time);

            // Cleanup lingering manual visuals during replay
            if (_playbackLoaded)
            {
                for (int i = 0; i < drawnTrams.Length; i++)
                {
                    var tram = drawnTrams[i];
                    if (tram == null) continue;

                    if (tram.Ellipse != null) { TileCanvas.Children.Remove(tram.Ellipse); tram.Ellipse = null; }
                    if (tram.Text != null) { TileCanvas.Children.Remove(tram.Text); tram.Text = null; }
                    if (tram.Speed != null) { TileCanvas.Children.Remove(tram.Speed); tram.Speed = null; }

                    if (drawnTramTrails[i] != null) { TileCanvas.Children.Remove(drawnTramTrails[i]); drawnTramTrails[i] = null; }

                    if (tram.TrailDots != null)
                    {
                        foreach (var d in tram.TrailDots.ToList()) TileCanvas.Children.Remove(d);
                        tram.TrailDots.Clear();
                    }
                }
            }

            // Determine filter
            var selectedFilter = TramBox?.SelectedItem as string;
            bool filtering = !string.IsNullOrEmpty(selectedFilter) && !string.Equals(selectedFilter, "All", StringComparison.OrdinalIgnoreCase);

            // iterate replay frames and draw only matching vehicles when filtering
            foreach (var kv in _replayFrames)
            {
                var id = kv.Key;
                if (string.IsNullOrEmpty(id)) continue;

                if (filtering)
                {
                    if (!IsReplayFilterMatch(id))
                    {
                        // ensure any leftover accuracy circle for this id is removed when we skip drawing the vehicle
                        RemoveReplayAccuracyEllipse(id);
                        continue; // skip non-selected trams entirely
                    }
                }

                var frames = kv.Value;

                // Compute step-frame availability first
                bool hasFrame = TryGetStepPosition(frames, time, out var curFrame, out var prevFrame);

                // Always remove existing visuals first (so rewinding before first frame hides the vehicle)
                if (_replayVehicles.TryGetValue(id, out var existing))
                {
                    if (existing.Ellipse != null) TileCanvas.Children.Remove(existing.Ellipse);
                    if (existing.Text != null) TileCanvas.Children.Remove(existing.Text);
                    if (existing.Speed != null) TileCanvas.Children.Remove(existing.Speed);

                    var old = TileCanvas.Children.OfType<Polyline>()
                        .Where(pl => string.Equals(pl.Tag as string, $"replay_trail_{id}", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var l in old) TileCanvas.Children.Remove(l);

                    if (existing.TrailDots != null)
                    {
                        foreach (var d in existing.TrailDots.ToList()) TileCanvas.Children.Remove(d);
                        existing.TrailDots.Clear();
                    }
                    _replayVehicles.Remove(id);
                }
                if (_replayBoxes.TryGetValue(id, out var oldBox))
                {
                    TileCanvas.Children.Remove(oldBox);
                    _replayBoxes.Remove(id);
                }

                if (!hasFrame)
                {
                    // no frame at this time => remove any stale accuracy circle for this id
                    RemoveReplayAccuracyEllipse(id);
                    continue;
                }

                // HIDE vehicles with last CAM older than 23s at this timeline position
                var age = time - curFrame.Timestamp;
                if (age > ReplayVisibilityTimeout)
                {
                    RemoveReplayAccuracyEllipse(id);
                    continue;
                }

                if (FilterReplayByAltitude(id, curFrame.Timestamp))
                {
                    RemoveReplayAccuracyEllipse(id);
                    continue;
                }

                var pos = curFrame.Position;

                if (!vehicleColorMap.TryGetValue(id, out Brush? color))
                {
                    int index = vehicleColorMap.Count % vehicleColors.Count;
                    color = vehicleColors[index];
                    vehicleColorMap[id] = color;
                }

                // Heading based on previous -> current step (or 0 if first)
                double headingDeg = 0.0;
                if (prevFrame != null)
                {
                    string keyHead = $"{id}|{curFrame.Timestamp.Ticks}";
                    if (_playbackHeadingByIdAndTs.TryGetValue(keyHead, out var h))
                        headingDeg = h;
                    else
                        headingDeg = CalculateAzimuth(prevFrame.Position, curFrame.Position);
                }

                // Flip 180° to cancel +180 inside UpdateOrCreateBox so front points to travel direction
                var replayHeadingAdj = (headingDeg - 180 + 360) % 360;
                UpdateOrCreateReplayBox(id, new Point(pos.X, pos.Y), color, replayHeadingAdj);

                var oldAccs = TileCanvas.Children.OfType<Ellipse>()
                    .Where(el => el.Tag is string s && s.Equals($"replay_acc_{id}", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var a in oldAccs) TileCanvas.Children.Remove(a);

                // Try draw accuracy circle for this frame only when Accuracy checkbox is checked
                if (AccuracyCB?.IsChecked == true)
                {
                    string keyAccForFrame = $"{id}|{curFrame.Timestamp.Ticks}";
                    if (_playbackAccuracyByIdAndTs.TryGetValue(keyAccForFrame, out var accuracyMeters) && accuracyMeters > 0)
                    {
                        // Obtain latitude for meters-per-pixel computation
                        var lonlat = CanvasPixelsToLatLon(new Point(pos.X, pos.Y), latitude, longitude, zoom);
                        double localLat = lonlat.Y;
                        double mpp = MetersPerPixel(localLat, zoom);
                        double radiusPx = accuracyMeters / Math.Max(1e-6, mpp);

                        // 20% translucent fill based on vehicle color
                        SolidColorBrush fillBrush;
                        if (color is SolidColorBrush scb)
                        {
                            var c = scb.Color;
                            fillBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(0.20 * 255), c.R, c.G, c.B));
                        }
                        else
                        {
                            var c = Colors.Black;
                            fillBrush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(0.20 * 255), c.R, c.G, c.B));
                        }
                        fillBrush.Freeze();

                        var accEllipse = new Ellipse
                        {
                            Width = radiusPx * 2,
                            Height = radiusPx * 2,
                            Fill = fillBrush,
                            Stroke = null,
                            IsHitTestVisible = false,
                            Tag = $"replay_acc_{id}"
                        };

                        Canvas.SetLeft(accEllipse, pos.X - radiusPx);
                        Canvas.SetTop(accEllipse, pos.Y - radiusPx);
                        TileCanvas.Children.Add(accEllipse);
                        Panel.SetZIndex(accEllipse, 995); // below vehicle but above tiles
                    }
                }

                var ellipse = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = color,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ellipse, pos.X - 6);
                Canvas.SetTop(ellipse, pos.Y - 6);
                TileCanvas.Children.Add(ellipse);

                var text = new TextBlock
                {
                    Text = id?.Length > 4 ? id[^4..] : id,
                    Foreground = color,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    IsHitTestVisible = false
                };
                StyleVehicleLabel(text, color);
                PositionVehicleLabel(text, pos, -22);
                TileCanvas.Children.Add(text);
                var played = frames.Where(f => f.Timestamp <= time).ToList();
                int maxPts = Math.Max(2, _maxTrailLength + 1); // at least two points to form one segment
                var lastN = played.Skip(Math.Max(0, played.Count - maxPts)).ToList();

                if (lastN.Count > 1)
                {
                    var trail = new Polyline
                    {
                        Stroke = color,
                        StrokeThickness = 2,
                        IsHitTestVisible = false,
                        Tag = $"replay_trail_{id}"
                    };
                    foreach (var f in lastN)
                        trail.Points.Add(f.Position);
                    TileCanvas.Children.Add(trail);
                    Panel.SetZIndex(trail, 999);
                }

                var trailDots = new List<Ellipse>();
                for (int i = 0; i < lastN.Count - 1; i++)
                {
                    var dp = lastN[i].Position;
                    var dot = new Ellipse { Width = 5, Height = 5, Fill = Brushes.Black, IsHitTestVisible = false };
                    Canvas.SetLeft(dot, dp.X - 2.5);
                    Canvas.SetTop(dot, dp.Y - 2.5);
                    TileCanvas.Children.Add(dot);
                    Panel.SetZIndex(dot, 1001);
                    trailDots.Add(dot);
                }

                var speedTb = new TextBlock
                {
                    Text = "",
                    Foreground = color,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    IsHitTestVisible = false
                };
                string keySpeed = $"{id}|{curFrame.Timestamp.Ticks}";
                if (_playbackSpeedByIdAndTs.TryGetValue(keySpeed, out var spd))
                    speedTb.Text = $"{spd:F1} m/s";
                StyleVehicleLabel(speedTb, color);
                PositionVehicleLabel(speedTb, pos, -5);
                TileCanvas.Children.Add(speedTb);
                Panel.SetZIndex(speedTb, 1100);

                _replayVehicles[id] = new MapPoint
                {
                    Position = pos,
                    Label = id,
                    Ellipse = ellipse,
                    Text = text,
                    Speed = speedTb,
                    TrailDots = trailDots,
                    MovementFrames = frames,
                    IsRecorded = true,
                    LastUpdate = DateTime.Now
                };

                //ApplyTextHalo(text);
                //ApplyTextHalo(speedTb);

                CheckActivationZones(pos, id);
                Panel.SetZIndex(ellipse, 5000);
                Panel.SetZIndex(text, 5001);
            }

            // Keep existing behavior for 'points' (unrelated to CAM replay). Remove if you also want them snapped.
            foreach (var pt in points)
            {
                if (pt.MovementFrames.Count == 0) continue;

                var nextFrame = pt.MovementFrames.FirstOrDefault(f => f.Timestamp > time);
                if (nextFrame == null)
                {
                    var last = pt.MovementFrames.Last();
                    UpdatePointPosition(pt, last.Position);
                    CheckActivationZones(last.Position, pt.Label ?? string.Empty);
                    continue;
                }

                var prevFrame = pt.MovementFrames.LastOrDefault(f => f.Timestamp <= time);
                if (prevFrame != null)
                {
                    double progress = (time - prevFrame.Timestamp).TotalMilliseconds /
                                      (nextFrame.Timestamp - prevFrame.Timestamp).TotalMilliseconds;

                    var interpolated = new Point(
                        Lerp(prevFrame.Position.X, nextFrame.Position.X, progress),
                        Lerp(prevFrame.Position.Y, nextFrame.Position.Y, progress)
                    );

                    UpdatePointPosition(pt, interpolated);
                    CheckActivationZones(interpolated, pt.Label);
                }
            }

            foreach (var kv in _replaySrvPoints.ToList())
            {
                if (kv.Value.Ellipse != null) TileCanvas.Children.Remove(kv.Value.Ellipse);
                if (kv.Value.Text != null) TileCanvas.Children.Remove(kv.Value.Text);
            }
            _replaySrvPoints.Clear();

            foreach (var kv in _replaySrvFramesById)
            {
                var id = kv.Key;
                var frames = kv.Value;
                if (frames == null || frames.Count == 0) continue;

                int idx = -1;
                for (int i = 0; i < frames.Count; i++)
                {
                    if (frames[i].ts <= time) idx = i;
                    else break;
                }
                if (idx < 0) continue;

                var fr = frames[idx];
                var (sx, sy) = ConvertLatLonToCanvasXY(fr.lat, fr.lon);

                var ellipse = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = Brushes.Red,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ellipse, sx - 6);
                Canvas.SetTop(ellipse, sy - 6);
                TileCanvas.Children.Add(ellipse);

                var text = new TextBlock
                {
                    Text = id,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.Black,
                    IsHitTestVisible = false
                };
                StyleVehicleLabel(text, Brushes.Black);
                PositionVehicleLabel(text, new Point(sx, sy), -22);
                TileCanvas.Children.Add(text);

                ApplyTextHalo(text);

                Panel.SetZIndex(ellipse, 1000);
                Panel.SetZIndex(text, 1000);

                _replaySrvPoints[id] = new MapPoint
                {
                    Position = new Point(sx, sy),
                    Label = id,
                    Ellipse = ellipse,
                    Text = text,
                    LastUpdate = DateTime.Now
                };

                // update SRV center for radius circle
                srvLatitude = fr.lat;
                srvLongitude = fr.lon;
            }

            // draw radius if enabled
            if (CircleCheckBox?.IsChecked == true && srvLatitude.HasValue && srvLongitude.HasValue)
                DrawRadiusCircle();

            bool allPointsDone = points.All(p => time > p.MovementFrames.LastOrDefault()?.Timestamp);
            bool allReplayDone = _replayFrames.Values.All(fr => fr.Count == 0 || time >= fr[^1].Timestamp);
            bool allTramsDone = allReplayDone;

            if (allPointsDone && allTramsDone)
            {
                if (playbackTimer != null && playbackTimer.IsEnabled) { }
                else
                {
                    isPlaying = false;
                    _isPlaybackSessionActive = false;
                }
            }

            UpdateTramSignalsForReplay(time);
            UpdateTramSignalPositions();

            // Update table according to filter
            SyncTramTableForReplay(time);

            if (!isReplaySliderDragging)
                ReplaySlider.Value = _playbackIndex;
        }

        /// <summary>
        /// Updates replay statistics for a given time. Called during timeline
        /// scrubbing and auto-playback to keep the stats table in sync with 
        /// the replayed positions. Applies filtering and altitude checks to
        /// ensure stats reflect only visible vehicles.
        /// </summary>
        /// <param name="t">The time for which to update replay statistics.</param>
        private void UpdateReplayStatsForTime(TimeSpan t)
        {
            if (_replayFrames == null || _replayFrames.Count == 0) return;

            // Convert relative timeline to absolute UTC for display
            var msgUtc = _replayStartUtc?.Add(t) ?? DateTime.UtcNow;

            string? lastShortId = null;

            foreach (var kv in _replayFrames)
            {
                var id = kv.Key;
                var frames = kv.Value;

                if (FilterReplayByAltitude(id, t))
                    continue;

                // only frames exactly at time 't' (step behavior)
                var frame = frames.FirstOrDefault(f => f.Timestamp == t);
                if (frame == null) continue;

                // Stats and last-id label
                IncrementCamOkCount();
                lastShortId = !string.IsNullOrEmpty(id) && id.Length > 4 ? id[^4..] : id;

                // Speed (m/s) for the exact frame (if available)
                double speed = 0.0;
                var key = $"{id}|{t.Ticks}";
                if (_playbackSpeedByIdAndTs.TryGetValue(key, out var spd))
                    speed = spd;

                // Update row’s LastCamTime/Speed without touching countdown or LastMessageTimestamp
                UpdateOrAddVehicleData(lastShortId, speed, msgUtc, isReplay: true);
            }
        }

        /// <summary>
        /// Handler for refresh button, refreshes map.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
    }
}
