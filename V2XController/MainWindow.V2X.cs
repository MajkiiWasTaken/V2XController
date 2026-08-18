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
    // CAM/SRV processing, CRC, vehicle trails and activation checks
    public partial class MainWindow
    {
        private void HandleV2XMessage(V2XMessage msg, string rawXml)
        {
            if (msg.IsManual ?? false)
                return;

            if (msg.MessageType == "CAM" && ShouldFilterLiveById(msg.VehicleID))
                return;

            if (msg.MessageType == "CAM")
            {
                if (TryFilterCamByAltitude(rawXml))
                    return;

                var sel = TramBox?.SelectedItem as string;
                bool filtering = !string.IsNullOrEmpty(sel) && !string.Equals(sel, "All", StringComparison.OrdinalIgnoreCase);

                if (filtering)
                {
                    if (string.IsNullOrEmpty(msg.VehicleID) || !IsReplayFilterMatch(msg.VehicleID))
                        return;
                }

                var liveSel = FilterTram?.SelectedItem as string;
                bool liveFiltering = !string.IsNullOrEmpty(liveSel) && !string.Equals(liveSel, "All", StringComparison.OrdinalIgnoreCase);

                if (liveFiltering)
                {
                    if (string.IsNullOrEmpty(msg.VehicleID) || !string.Equals(msg.VehicleID.Length > 4 ? msg.VehicleID[^4..] : msg.VehicleID, liveSel, StringComparison.Ordinal))
                        return;
                }

                double accuracyMeters = 0.0;
                try
                {
                    int vehPtStart = rawXml.IndexOf("<vehPt", StringComparison.OrdinalIgnoreCase);
                    if (vehPtStart >= 0)
                    {
                        int tagEnd = rawXml.IndexOf('>', vehPtStart);
                        if (tagEnd > vehPtStart)
                        {
                            var tag = rawXml.Substring(vehPtStart, tagEnd - vehPtStart);
                            var accAttrNames = new[] { "accuracy", "acc", "accuracy_m", "accuracyMeters", "hacc" };
                            foreach (var an in accAttrNames)
                            {
                                var idxAttr = tag.IndexOf(an + "=\"", StringComparison.OrdinalIgnoreCase);
                                if (idxAttr >= 0)
                                {
                                    int vStart = idxAttr + an.Length + 2;
                                    int vEnd = tag.IndexOf('"', vStart);
                                    if (vEnd > vStart)
                                    {
                                        var accStr = tag.Substring(vStart, vEnd - vStart);
                                        if (double.TryParse(accStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAcc))
                                        {
                                            accuracyMeters = parsedAcc;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(msg.VehicleID))
                    {
                        if (accuracyMeters > 0)
                            _lastLiveAccuracyById[msg.VehicleID] = accuracyMeters;
                        else
                            _lastLiveAccuracyById[msg.VehicleID] = null;
                    }
                }
                catch { }

                if (!vehicleColorMap.TryGetValue(msg.VehicleID, out Brush tramColor))
                {
                    int colorIndex = vehicleColorMap.Count % vehicleColors.Count;
                    tramColor = vehicleColors[colorIndex];
                    vehicleColorMap[msg.VehicleID] = tramColor;
                }

                var oldLiveAcc = TileCanvas.Children.OfType<Ellipse>()
                    .Where(e => e.Tag is string s && s.Equals($"live_acc_{msg.VehicleID}", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var a in oldLiveAcc) TileCanvas.Children.Remove(a);

                // FIX: Only reset vehicle instance when cleanup is NOT already in progress.
                if (activeVehicles.TryGetValue(msg.VehicleID, out var existing))
                {
                    bool cleanupInProgress = vehicleTrailCleanupTokens.ContainsKey(msg.VehicleID);
                    if (!cleanupInProgress)
                    {
                        bool tableHasRow = IsTramTableRowPresentForId(msg.VehicleID);
                        bool tooOld = (DateTime.Now - existing.LastUpdate) > TableRowTimeout;
                        if (!tableHasRow || tooOld)
                            ResetVehicleInstance(msg.VehicleID);
                    }
                }

                if (!(msg.VehicleID?.StartsWith("000000") ?? false) || isPlaying == true)
                {
                    var (x, y) = ConvertLatLonToCanvasXY(msg.Latitude, msg.Longitude);

                    if (AccuracyCB?.IsChecked == true && accuracyMeters >= 4)
                    {
                        double mpp = MetersPerPixel(msg.Latitude ?? 0.0, zoom);
                        double radiusPx = accuracyMeters / Math.Max(1e-6, mpp);

                        SolidColorBrush fillBrush;
                        if (tramColor is SolidColorBrush scb)
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
                            Tag = $"live_acc_{msg.VehicleID}"
                        };
                        Canvas.SetLeft(accEllipse, x - radiusPx);
                        Canvas.SetTop(accEllipse, y - radiusPx);
                        TileCanvas.Children.Add(accEllipse);
                        Panel.SetZIndex(accEllipse, 995);
                    }

                    if (_liveAccuracyTextById.TryGetValue(msg.VehicleID, out var accTb))
                    {
                        if (_lastLiveAccuracyById.TryGetValue(msg.VehicleID, out var accVal) && accVal.HasValue && accVal.Value > 0)
                            accTb.Text = "";
                        else
                            accTb.Text = "no acc";

                        if (accVal.HasValue && accVal.Value > 0 && accVal.Value < 4)
                            accTb.Text = "acc < 4 m";

                        Canvas.SetLeft(accTb, x + 5);
                        Canvas.SetTop(accTb, y + 20);
                        if (!TileCanvas.Children.Contains(accTb)) TileCanvas.Children.Add(accTb);
                        Panel.SetZIndex(accTb, 1100);
                    }

                    if (activeVehicles.TryGetValue(msg.VehicleID, out var point))
                    {
                        // Cancel live vehicle cleanup token
                        if (vehicleTrailCleanupTokens.TryGetValue(msg.VehicleID, out var existingCts))
                        {
                            existingCts.Cancel();
                            existingCts.Dispose();
                            vehicleTrailCleanupTokens.Remove(msg.VehicleID);

                            if (point.Ellipse != null) point.Ellipse.Visibility = Visibility.Visible;
                            if (point.Text != null) point.Text.Visibility = Visibility.Visible;
                            if (point.Speed != null) point.Speed.Visibility = Visibility.Visible;
                            if (_vehicleBoxes.TryGetValue(msg.VehicleID, out var restoredBox))
                                restoredBox.Visibility = Visibility.Visible;
                            if (_liveAccuracyTextById.TryGetValue(msg.VehicleID, out var restoredAcc))
                                restoredAcc.Visibility = Visibility.Visible;
                        }

                        // FIX: Cancel drawn tram cleanup AND refresh its LastUpdate so CleanupOldVehicles
                        // doesn't immediately restart the cleanup on the next timer tick.
                        if (drawnTramIds != null && drawnTrams != null)
                        {
                            for (int i = 0; i < drawnTramIds.Length; i++)
                            {
                                if (drawnTramIds[i] == msg.VehicleID)
                                {
                                    string drawnKey = $"drawn_{i}_trail";
                                    if (vehicleTrailCleanupTokens.TryGetValue(drawnKey, out var drawnCts))
                                    {
                                        drawnCts.Cancel();
                                        drawnCts.Dispose();
                                        vehicleTrailCleanupTokens.Remove(drawnKey);
                                    }

                                    // Refresh LastUpdate so CleanupOldVehicles won't restart immediately
                                    if (i < drawnTrams.Length && drawnTrams[i] != null)
                                    {
                                        drawnTrams[i].LastUpdate = DateTime.Now;

                                        if (drawnTrams[i].Ellipse != null) drawnTrams[i].Ellipse.Visibility = Visibility.Visible;
                                        if (drawnTrams[i].Text != null) drawnTrams[i].Text.Visibility = Visibility.Visible;
                                        if (drawnTrams[i].Speed != null) drawnTrams[i].Speed.Visibility = Visibility.Visible;
                                    }

                                    break;
                                }
                            }
                        }

                        point.Position = new Point(x, y);
                        point.LastUpdate = DateTime.Now;
                        UpdateVehicleCanvasPosition(point, new Point(x, y), tramColor, false, point.Label, msg.Speed);

                        _lastLatLon[msg.VehicleID] = (msg.Latitude ?? 0.0, msg.Longitude ?? 0.0);
                        _lastHeadingLive[msg.VehicleID] = msg.Heading ?? 0.0;

                        var topCenter = new Point(x, y);
                        var liveHeadingAdj = ((msg.Heading ?? 0.0) - 180 + 360) % 360;
                        UpdateOrCreateVehicleBox(msg.VehicleID, topCenter, tramColor, liveHeadingAdj);
                    }
                    else
                    {
                        var ellipse = new Ellipse
                        {
                            Width = 12,
                            Height = 12,
                            Fill = tramColor,
                            Tag = "Tram",
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(ellipse, x - ellipse.Width / 2);
                        Canvas.SetTop(ellipse, y - ellipse.Height / 2);

                        var text = new TextBlock
                        {
                            Text = msg.VehicleID,
                            Foreground = tramColor,
                            FontWeight = FontWeights.Bold,
                            Tag = "Tram",
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(text, x + 8);
                        Canvas.SetTop(text, y - 6);

                        var speedText = new TextBlock
                        {
                            Text = $"{msg.Speed:F1} km/h",
                            Foreground = tramColor,
                            FontWeight = FontWeights.Bold,
                            Tag = "Tram",
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(speedText, x + 8);
                        Canvas.SetTop(speedText, y + 6);

                        var accText = new TextBlock
                        {
                            Text = "",
                            Foreground = tramColor,
                            FontWeight = FontWeights.Bold,
                            FontSize = 11,
                            Tag = "Tram",
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(accText, x + 5);
                        Canvas.SetTop(accText, y + 20);
                        TileCanvas.Children.Add(accText);

                        _liveAccuracyTextById[msg.VehicleID] = accText;

                        var newPoint = new MapPoint
                        {
                            Position = new Point(x, y),
                            Label = msg.VehicleID,
                            Ellipse = ellipse,
                            Text = text,
                            Speed = speedText,
                            LastUpdate = DateTime.Now,
                            VehicleColor = tramColor,
                            TrailDots = new List<Ellipse>(),
                            MovementFrames = new List<MovementFrame>(),
                            TrailGeoPoints = new List<(double lat, double lon)>()
                        };

                        activeVehicles[msg.VehicleID] = newPoint;

                        TileCanvas.Children.Add(ellipse);
                        TileCanvas.Children.Add(text);
                        TileCanvas.Children.Add(speedText);

                        if (FilterTram != null)
                            FilterTram.Dispatcher.BeginInvoke(new Action(PopulateLiveTramBoxFromActiveVehicles));

                        _lastLatLon[msg.VehicleID] = (msg.Latitude ?? 0.0, msg.Longitude ?? 0.0);
                        _lastHeadingLive[msg.VehicleID] = msg.Heading ?? 0.0;

                        var topCenterNew = new Point(x, y);
                        var liveHeadingAdj = ((msg.Heading ?? 0.0) - 180 + 360) % 360;
                        UpdateOrCreateVehicleBox(msg.VehicleID, topCenterNew, tramColor, liveHeadingAdj);
                    }
                }

                if (lastCamTimes.TryGetValue(msg.VehicleID, out var lastTime))
                    prevCamTimes[msg.VehicleID] = lastTime;
                lastCamTimes[msg.VehicleID] = msg.Timestamp ?? DateTime.UtcNow;

                string statId = string.IsNullOrEmpty(msg.VehicleID) ? "-" : msg.VehicleID;
                string camIdShort = statId.Length > 4 ? statId[^4..] : statId;

                if (!filtering || IsReplayFilterMatch(msg.VehicleID))
                    UpdateOrAddVehicleData(camIdShort, msg.Speed ?? 0.0, msg.Timestamp ?? DateTime.UtcNow);

                if (!(msg.VehicleID?.StartsWith("000000") ?? false))
                    UpdateVehicleTrail(msg);

                CheckStopArrivalsDepartures(msg);
            }
            else if (msg.MessageType == "SRV")
            {
                SRVMessage srvMsg = null;
                bool isValid = false;
                string logicalId = "-";
                string lastRecTime = "-";

                try
                {
                    srvMsg = SRVMessage.ParseSrvMessage(rawXml);
                    if (srvMsg != null)
                    {
                        logicalId = string.IsNullOrWhiteSpace(srvMsg.LogicalId) ? "RSU" : srvMsg.LogicalId;
                        isValid = IsValidSrvMessage(rawXml);

                        var local = TimeZoneInfo.ConvertTimeFromUtc(srvMsg.Dt.ToUniversalTime(), czechTimeZone);
                        lastRecTime = local.ToString("HH:mm:ss");
                    }
                }
                catch
                {
                    IncrementSrvErrorCount();
                }

                if (isValid) IncrementSrvOkCount();
                else IncrementSrvErrorCount();

                if (!string.IsNullOrEmpty(logicalId))
                {
                    var srvV2xMsg = new V2XMessage
                    {
                        VehicleID = logicalId,
                        Timestamp = srvMsg?.Dt ?? DateTime.Now,
                        Latitude = srvMsg?.Latitude ?? 0,
                        Longitude = srvMsg?.Longitude ?? 0,
                        MessageType = "SRV"
                    };

                    if (srvV2xMsg.Latitude != 0 && srvV2xMsg.Longitude != 0)
                    {
                        srvLatitude = srvV2xMsg.Latitude;
                        srvLongitude = srvV2xMsg.Longitude;
                        _ = EnsureLocalAreaAltitudeAsync(force: true);

                        _lastLatLon[srvV2xMsg.VehicleID] = (srvV2xMsg.Latitude ?? 0.0, srvV2xMsg.Longitude ?? 0.0);

                        if (CircleCheckBox?.IsChecked == true)
                            DrawRadiusCircle();
                    }

                    if (activeVehicles.TryGetValue(srvV2xMsg.VehicleID, out var point))
                    {
                        point.Position = new Point(srvV2xMsg.Longitude ?? 0.0, srvV2xMsg.Latitude ?? 0.0);
                        point.LastUpdate = DateTime.Now;
                    }
                    else
                    {
                        var ellipse = new Ellipse
                        {
                            Width = 12,
                            Height = 12,
                            Fill = Brushes.Red,
                            Tag = "Srv",
                            IsHitTestVisible = false
                        };

                        var text = new TextBlock
                        {
                            Text = srvV2xMsg.VehicleID,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brushes.Black,
                            Tag = "Srv",
                            IsHitTestVisible = false
                        };

                        var newPoint = new MapPoint
                        {
                            Position = new Point(srvV2xMsg.Longitude ?? 0.0, srvV2xMsg.Latitude ?? 0.0),
                            Label = srvV2xMsg.VehicleID,
                            Ellipse = ellipse,
                            Text = text,
                            LastUpdate = DateTime.Now,
                            TrailGeoPoints = new List<(double lat, double lon)>()
                        };

                        activeVehicles[srvV2xMsg.VehicleID] = newPoint;

                        TileCanvas.Children.Add(ellipse);
                        TileCanvas.Children.Add(text);
                    }

                    UpdateVehicleTrail(srvV2xMsg);
                }
            }
        }

        /// <summary>
        /// Validates an SRV message by computing the CRC over the <service .../> tag.
        /// </summary>
        /// <param name="rawXml">The raw XML representation of the SRV message.</param>
        /// <returns>True if the SRV message is valid; otherwise, false.</returns>
        private static bool IsValidSrvMessage(string rawXml)
        {
            try
            {
                int svcStart = rawXml.IndexOf("<service", StringComparison.OrdinalIgnoreCase);
                if (svcStart < 0) return false;

                int crcTag = rawXml.IndexOf("<crc>", StringComparison.OrdinalIgnoreCase);
                if (crcTag < 0) return false;

                // take the <service .../> tag up to its closing '>' that appears before <crc>
                int svcTagEnd = rawXml.LastIndexOf('>', crcTag - 1);
                if (svcTagEnd < svcStart) return false;

                string crcInput = rawXml.Substring(svcStart, svcTagEnd - svcStart + 1);

                int crcValueStart = crcTag + 5;
                int crcValueEnd = rawXml.IndexOf("</crc>", crcValueStart, StringComparison.OrdinalIgnoreCase);
                if (crcValueEnd < 0) return false;
                string provided = rawXml.Substring(crcValueStart, crcValueEnd - crcValueStart).Trim();

                ushort computed = ComputeCRC(crcInput);
                return provided == computed.ToString() || provided == computed.ToString("D4");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Updates the trail of a vehicle on the map based on the received V2X message.
        /// </summary>
        /// <param name="msg">The V2X message containing the vehicle's position and other information.</param>
        private void UpdateVehicleTrail(V2XMessage msg)
        {
            var isSrv = msg.MessageType == "SRV";
            if (msg.IsManual ?? false) return;

            Brush tramColor;
            if (isSrv)
            {
                tramColor = Brushes.Red; // RSU always red
            }
            else
            {
                if (!vehicleColorMap.TryGetValue(msg.VehicleID, out tramColor))
                {
                    int colorIndex = vehicleColorMap.Count % vehicleColors.Count;
                    Brush candidateColor = vehicleColors[colorIndex];
                    if (candidateColor == Brushes.Red || candidateColor == Brushes.Black)
                    {
                        colorIndex = (colorIndex + 1) % vehicleColors.Count;
                        candidateColor = vehicleColors[colorIndex];
                    }
                    tramColor = candidateColor;
                    vehicleColorMap[msg.VehicleID] = tramColor;
                }
            }

            if (!activeVehicles.TryGetValue(msg.VehicleID, out var vehicle))
            {
                var ellipse = new Ellipse { Width = 12, Height = 12, Fill = tramColor, IsHitTestVisible = false };
                var text = new TextBlock
                {
                    Text = isSrv ? msg.VehicleID : (msg.VehicleID?.Length > 4 ? msg.VehicleID[^4..] : msg.VehicleID),
                    FontWeight = FontWeights.Bold,
                    Foreground = isSrv ? Brushes.Black : tramColor,
                    IsHitTestVisible = false
                };
                var speedtext = new TextBlock
                {
                    Text = "",
                    FontWeight = FontWeights.Bold,
                    Foreground = isSrv ? Brushes.Black : tramColor,
                    IsHitTestVisible = false
                };

                TileCanvas.Children.Add(ellipse);
                TileCanvas.Children.Add(text);
                TileCanvas.Children.Add(speedtext);

                vehicle = new MapPoint
                {
                    Label = msg.VehicleID,
                    Ellipse = ellipse,
                    Speed = speedtext,
                    Text = text,
                    MovementFrames = new List<MovementFrame>(),
                    TrailDots = new List<Ellipse>(),
                    TrailGeoPoints = new List<(double lat, double lon)>() // Initialize geo trail
                };
                ApplyTextHalo(text);
                ApplyTextHalo(speedtext);

                activeVehicles[msg.VehicleID] = vehicle;
                vehicle.LastUpdate = DateTime.Now;
            }

            // Add geo coordinates to trail FIRST (before converting to canvas)
            vehicle.TrailGeoPoints ??= new List<(double lat, double lon)>();
            if (msg.Latitude.HasValue && msg.Longitude.HasValue)
                vehicle.TrailGeoPoints.Add((msg.Latitude.Value, msg.Longitude.Value));

            // Cap geo points to at most (_maxTrailLength + 1) points => _maxTrailLength segments
            while (vehicle.TrailGeoPoints.Count > _maxTrailLength + 1)
                vehicle.TrailGeoPoints.RemoveAt(0);

            // Convert current position to canvas
            var (x, y) = ConvertLatLonToCanvasXY(msg.Latitude, msg.Longitude);

            // Keep MovementFrames for compatibility (but we'll use TrailGeoPoints for rendering)
            var frame = new MovementFrame { Timestamp = msg.Timestamp?.TimeOfDay ?? TimeSpan.Zero, Position = new Point(x, y) };
            vehicle.MovementFrames ??= new List<MovementFrame>();
            vehicle.MovementFrames.Add(frame);
            while (vehicle.MovementFrames.Count > _maxTrailLength + 1)
                vehicle.MovementFrames.RemoveAt(0);

            vehicle.TrailDots ??= new List<Ellipse>();

            // Remove old trail dots completely and recreate from geo points
            foreach (var dot in vehicle.TrailDots.ToList())
            {
                TileCanvas.Children.Remove(dot);
            }
            vehicle.TrailDots.Clear();

            // Create dots from geo points (skip the last point which is current position)
            if (!isSrv && vehicle.TrailGeoPoints.Count > 1)
            {
                for (int i = 0; i < vehicle.TrailGeoPoints.Count - 1; i++)
                {
                    var (lat, lon) = vehicle.TrailGeoPoints[i];
                    var (dotX, dotY) = ConvertLatLonToCanvasXY(lat, lon);

                    var trailDot = new Ellipse
                    {
                        Width = 5,
                        Height = 5,
                        Fill = Brushes.Black,
                        IsHitTestVisible = false,
                        Tag = $"trail_dot_{msg.VehicleID}_{i}" // Add tag for tracking
                    };
                    Canvas.SetLeft(trailDot, dotX - 2.5);
                    Canvas.SetTop(trailDot, dotY - 2.5);
                    vehicle.TrailDots.Add(trailDot);
                    TileCanvas.Children.Add(trailDot);
                    Panel.SetZIndex(trailDot, 1001);
                }
            }

            UpdateVehicleCanvasPosition(vehicle, new Point(x, y), tramColor, isSrv, msg.VehicleID, msg.Speed);
            vehicle.LastUpdate = DateTime.Now;

            if (!isSrv)
            {
                // Remove old trail polyline
                var oldLines = TileCanvas.Children.OfType<Polyline>()
                    .Where(l => l.Tag is string tag && tag == $"trail_{msg.VehicleID}")
                    .ToList();
                foreach (var l in oldLines) TileCanvas.Children.Remove(l);

                // Create NEW polyline from geo points (not MovementFrames!)
                if (vehicle.TrailGeoPoints.Count > 1)
                {
                    var polyline = new Polyline
                    {
                        Stroke = tramColor,
                        StrokeThickness = 2,
                        Tag = $"trail_{msg.VehicleID}", // Changed tag to match UpdateActiveVehiclesPositions
                        IsHitTestVisible = false
                    };

                    // Convert all geo points to canvas coordinates
                    foreach (var (lat, lon) in vehicle.TrailGeoPoints)
                    {
                        var (px, py) = ConvertLatLonToCanvasXY(lat, lon);
                        polyline.Points.Add(new Point(px, py));
                    }

                    TileCanvas.Children.Add(polyline);
                    Panel.SetZIndex(polyline, 999);
                }
            }

            Panel.SetZIndex(vehicle.Ellipse, 2000);
            Panel.SetZIndex(vehicle.Text, 2001);
        }

        /// <summary>
        /// Draws an SRV point on the map based on the received SRV message.
        /// </summary>
        /// <param name="msg">The SRV message containing the point's position and other information.</param>
        private void DrawSrvPoint(SRVMessage msg)
        {
            if (msg == null) return;

            var (canvasX, canvasY) = ConvertLatLonToCanvasXY(msg.Latitude, msg.Longitude);

            Ellipse point = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Brushes.Red,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                Tag = msg.LogicalId
            };

            Canvas.SetLeft(point, canvasX - point.Width / 2);
            Canvas.SetTop(point, canvasY - point.Height / 2);

            TileCanvas.Children.Add(point);
        }

        /// <summary>
        /// Validates a CAM message by computing the CRC over the <vehPt .../> tag.
        /// </summary>
        /// <param name="rawXml">The raw XML representation of the CAM message.</param>
        /// <returns>True if the CAM message is valid; otherwise, false.</returns>
        public static bool IsValidCamMessage(string rawXml)
        {
            try
            {
                // Find beginning of <vehPt> tag
                int vehPtStart = rawXml.IndexOf("<vehPt");
                if (vehPtStart < 0) return false;

                // find <crc> tag
                int crcTag = rawXml.IndexOf("<crc>");
                if (crcTag < 0) return false;

                //Find last '>' before <crc>
                int vehPtEnd = rawXml.LastIndexOf('>', crcTag - 1);
                if (vehPtEnd < vehPtStart) return false;

                // Take substring for crc including <>
                string crcInput = rawXml.Substring(vehPtStart, vehPtEnd - vehPtStart + 1);

                // Debug: write the CRC input to console
                Console.WriteLine($"[DEBUG] CRC input: '{crcInput}'");

                // get value of crc from xml
                int crcValueStart = crcTag + 5;
                int crcValueEnd = rawXml.IndexOf("</crc>", crcValueStart);
                if (crcValueEnd < 0) return false;
                string providedCrc = rawXml.Substring(crcValueStart, crcValueEnd - crcValueStart).Trim();

                Console.WriteLine($"[DEBUG] Provided CRC: '{providedCrc}'");

                // calculate crc
                ushort computed = ComputeCRC(crcInput);

                Console.WriteLine($"[DEBUG] Computed CRC: {computed}");

                // Check if provided CRC matches computed CRC
                return providedCrc == computed.ToString() || providedCrc == computed.ToString("D4");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DEBUG] CRC Exception: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Computes the CRC for the given data using the MODBUS algorithm.
        /// </summary>
        /// <param name="data">The data for which to compute the CRC.</param>
        /// <returns>The computed CRC value.</returns>
        public static ushort ComputeCRC(string data)
        {
            ushort crc = 0xFFFF; // MODBUS start value
            byte[] bytes = Encoding.UTF8.GetBytes(data);

            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x0001) != 0)
                        crc = (ushort)((crc >> 1) ^ 0xA001); // 0xA001 is bit reversed 0x8005
                    else
                        crc >>= 1;
                }
            }
            return crc;
        }

        /// <summary>
        /// Logic for recieved serial port data. We expect SRV messages here, 
        /// which we parse and then draw as red points on the map. We also validate 
        /// the SRV messages using a CRC check and log any errors to the terminal.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            string line = serialPort.ReadLine();
            SRVMessage msg = SRVMessage.ParseSrvMessage(line);

            if (msg != null)
            {
                Dispatcher.Invoke(() =>
                {
                    DrawSrvPoint(msg);
                });
            }

        }

        /// <summary>
        /// Sends point as CAM message while simulating tram.
        /// </summary>
        /// <param name="vehicleId">The ID of the vehicle.</param>
        /// <param name="latitude">The latitude of the vehicle.</param>
        /// <param name="longitude">The longitude of the vehicle.</param>
        /// <param name="speed">The speed of the vehicle.</param>
        /// <param name="heading">The heading of the vehicle.</param>
        /// <param name="altitude">The altitude of the vehicle.</param>
        /// <param name="dist">The distance traveled by the vehicle.</param>
        /// <param name="type">The type of the vehicle.</param>
        /// <param name="typeEx">The extended type of the vehicle.</param>
        /// <param name="lineNum">The line number of the vehicle.</param>
        /// <param name="vehNum">The vehicle number.</param>
        /// <param name="embarkation">The embarkation status of the vehicle.</param>
        /// <param name="suppressLocalRender">Whether to suppress local rendering of the vehicle.</param>
        private void SendPointAsCamMessage(
        string vehicleId,
        double latitude,
        double longitude,
        double speed = 0,
        double heading = 0,
        double altitude = 0,
        double dist = 0,
        string type = "Tram",
        string typeEx = "",
        int lineNum = -1,
        int vehNum = -1,
        int embarkation = 0,
        bool suppressLocalRender = false)
        {
            string vehPt =
                $"<vehPt statId=\"{vehicleId}\" type=\"{type}\" typeEx=\"{typeEx}\" lat=\"{latitude.ToString(CultureInfo.InvariantCulture)}\" lng=\"{longitude.ToString(CultureInfo.InvariantCulture)}\" alt=\"{altitude.ToString(CultureInfo.InvariantCulture)}\" speed=\"{speed.ToString(CultureInfo.InvariantCulture)}\" heading=\"{heading.ToString(CultureInfo.InvariantCulture)}\" lastRec=\"{DateTime.UtcNow:o}\" dist=\"{dist.ToString(CultureInfo.InvariantCulture)}\" lineNum=\"{lineNum}\" vehNum=\"{vehNum}\" embarkation=\"{embarkation}\" />";

            ushort crc = ComputeCRC(vehPt);
            string xml = $"<CAM>{vehPt}<crc>{crc}</crc></CAM>";
            Console.WriteLine(xml);
            try
            {
                if (serialPort != null && serialPort.IsOpen)
                {
                    lock (_serialIoLock)
                    {
                        SendTransportLine(xml);
                        SendTransportLine(serialPort.NewLine);
                    }

                    // record what we wrote so the listener can ignore echoes
                    lock (_recentLocalWritesLock)
                    {
                        _recentLocalWrites.Add(xml);
                        if (_recentLocalWrites.Count > RecentLocalWritesMax)
                            _recentLocalWrites.RemoveAt(0);
                    }
                }
                else
                {
                    Console.WriteLine("[TX] Serial port is not open. Skipping CAM transmit.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TX] Error sending CAM over serial: " + ex.Message);
            }

            var msg = V2XMessageParser.ParseV2XMessage(xml);
            if (suppressLocalRender) msg.IsManual = true;
            HandleV2XMessage(msg, xml);

            if (isRecording && (vehicleId == drawnTramIds[0] || vehicleId == drawnTramIds[1]))
            {
                recordedManualCamMessages.Add(xml);
            }
        }

        /// <summary>
        /// Checks if the given position is within any of the defined 
        /// activation zones. If it is, activates the zone 
        /// (change color and store last tram ID) and sets a timer to deactivate after 0.5 seconds.
        /// </summary>
        /// <param name="pos">The position to check.</param>
        /// <param name="vehicleId">The ID of the vehicle.</param>
        /// <param name="heading">The heading of the vehicle.</param>
        private void CheckActivationZones(Point pos, string vehicleId, double heading = double.NaN)
        {
            string shortId = string.IsNullOrEmpty(vehicleId) ? "-" :
                             (vehicleId.Length > 4 ? vehicleId[^4..] : vehicleId);

            if (double.IsNaN(heading) && _lastHeadingLive.TryGetValue(vehicleId, out var cachedHeading))
                heading = cachedHeading;

            var nowInZones = new HashSet<ActivationZone>();

            // 1) klasické obdélníkové zóny
            foreach (var zone in activationZones.Values)
            {
                if (!zone.Bounds.Contains(pos))
                    continue;

                if (!IsPointInRotatedRectangle(pos, zone))
                    continue;

                nowInZones.Add(zone);
            }

            foreach (var kvp in _polylineToSegmentZones)
            {
                Polyline polyline = kvp.Key;
                List<ActivationZone> segments = kvp.Value;

                ActivationZone? bestZone = FindBestPolylineSegmentZone(pos, polyline, segments);

                if (bestZone != null)
                {
                    nowInZones.Add(bestZone);
                }
            }

            if (!_vehicleActiveZones.TryGetValue(vehicleId, out var previousZones))
                previousZones = new HashSet<ActivationZone>();

            foreach (var leftZone in previousZones)
            {
                if (nowInZones.Contains(leftZone))
                    continue;

                if (_zoneDeactivateTimers.TryGetValue(leftZone, out var t))
                {
                    t.Stop();
                    _zoneDeactivateTimers.Remove(leftZone);
                }

                leftZone.IsActive = false;
                SetActivationZoneVisual(leftZone, false);

                _vehicleZoneValidEntry.Remove((vehicleId, leftZone));
            }

            foreach (var zone in nowInZones)
            {
                var key = (vehicleId, zone);
                bool wasInZone = previousZones.Contains(zone);

                if (!wasInZone)
                {
                    bool validDirection = IsValidEntryDirection(pos, zone, heading);
                    _vehicleZoneValidEntry[key] = validDirection;

                    Console.WriteLine($"[ZONE] {shortId} entered zone '{zone.Name}' | heading={heading:F0} | valid={validDirection}");

                    if (!validDirection)
                        continue;
                }
                else
                {
                    if (_vehicleZoneValidEntry.TryGetValue(key, out bool hadValid) && !hadValid)
                        continue;
                }

                zone.LastTramId = shortId;

                if (!zone.IsActive)
                {
                    zone.IsActive = true;
                    SetActivationZoneVisual(zone, true);
                }

                if (_zoneDeactivateTimers.TryGetValue(zone, out var existing))
                {
                    existing.Stop();
                    existing.Start();
                }
                else
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };

                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        _zoneDeactivateTimers.Remove(zone);

                        zone.IsActive = false;
                        SetActivationZoneVisual(zone, false);
                    };

                    _zoneDeactivateTimers[zone] = timer;
                    timer.Start();
                }
            }

            _vehicleActiveZones[vehicleId] = nowInZones;
        }

        /// <summary>
        /// Checks if the vehicle entered the activation zone from a valid direction (roughly towards the center of the rectangle).
        /// </summary>
        private bool IsValidEntryDirection(Point entryPos, ActivationZone zone, double heading)
        {
            if (double.IsNaN(heading))
                return true;

            double zoneAzimuth = zone.Azimuth;

            double diff = Math.Abs(heading - zoneAzimuth);
            if (diff > 180.0) diff = 360.0 - diff;

            bool valid = diff <= 80.0;

            Console.WriteLine($"[ZONE DIR] heading={heading:F0} | zoneAz={zoneAzimuth:F0} | diff={diff:F0} | valid={valid}");

            return valid;
        }

        private ActivationZone? FindBestPolylineSegmentZone(
            Point pos,
            Polyline polyline,
            List<ActivationZone> zones)
        {
            ActivationZone? bestZone = null;
            double bestDistance = double.MaxValue;

            foreach (var zone in zones)
            {
                if (!TryGetPolylineSegmentDistance(pos, polyline, zone, out double distance))
                    continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestZone = zone;
                }
            }

            return bestZone;
        }

        private bool TryGetPolylineSegmentDistance(
    Point pos,
    Polyline polyline,
    ActivationZone zone,
    out double distance)
        {
            distance = double.MaxValue;

            int i = zone.SegmentIndex;

            if (i < 0 || i + 1 >= polyline.Points.Count)
                return false;

            Point a = polyline.Points[i];
            Point b = polyline.Points[i + 1];

            double dx = b.X - a.X;
            double dy = b.Y - a.Y;

            double lenSq = dx * dx + dy * dy;

            if (lenSq <= 0.0001)
                return false;

            double t = ((pos.X - a.X) * dx + (pos.Y - a.Y) * dy) / lenSq;

            // důležité: žádné clampnutí sem
            // když je bod před začátkem / za koncem segmentu, segment se neaktivuje
            if (t < 0.0 || t > 1.0)
                return false;

            double closestX = a.X + t * dx;
            double closestY = a.Y + t * dy;

            double diffX = pos.X - closestX;
            double diffY = pos.Y - closestY;

            distance = Math.Sqrt(diffX * diffX + diffY * diffY);

            double mpp = MetersPerPixel(latitude, zoom);

            // zóna bude reagovat užší než její vykreslená šířka
            double halfWidthPx = (zone.Width / 2.0) / mpp;
            return distance <= halfWidthPx;
        }

        private bool IsPointInPolylineSegmentZone(Point pos, Polyline polyline, ActivationZone zone)
        {
            if (polyline == null)
                return false;

            int i = zone.SegmentIndex;

            if (i < 0 || i + 1 >= polyline.Points.Count)
                return false;

            Point a = polyline.Points[i];
            Point b = polyline.Points[i + 1];

            double dx = b.X - a.X;
            double dy = b.Y - a.Y;

            double lenSq = dx * dx + dy * dy;
            if (lenSq <= 0.0001)
                return false;

            double t = ((pos.X - a.X) * dx + (pos.Y - a.Y) * dy) / lenSq;

            double mpp = MetersPerPixel(latitude, zoom);

            double insetMeters = 5.0;
            double insetPx = insetMeters / mpp;

            double segmentLenPx = Math.Sqrt(lenSq);

            double minT = insetPx / segmentLenPx;
            double maxT = 1.0 - minT;

            if (minT > 0.45)
            {
                minT = 0.0;
                maxT = 1.0;
            }

            if (t < minT || t > maxT)
                return false;

            double closestX = a.X + t * dx;
            double closestY = a.Y + t * dy;

            double dist = Math.Sqrt(
                Math.Pow(pos.X - closestX, 2) +
                Math.Pow(pos.Y - closestY, 2)
            );

            double halfWidthPx = (zone.Width / 2.0) / mpp;
            halfWidthPx *= 0.75;

            return dist <= halfWidthPx;
        }

        private System.Windows.Shapes.Path? GetVisualPathForPolylineSegment(ActivationZone zone)
        {
            foreach (var kvp in _polylineToSegmentZones)
            {
                Polyline polyline = kvp.Key;
                List<ActivationZone> zones = kvp.Value;

                int index = zones.FindIndex(z => z == zone);
                if (index < 0)
                    continue;

                if (_polylineToSegments.TryGetValue(polyline, out var paths) &&
                    index >= 0 &&
                    index < paths.Count)
                {
                    return paths[index];
                }
            }

            return null;
        }

        private void SetPolylineSegmentVisual(ActivationZone zone, bool active)
        {
            if (!_segmentToVisualPath.TryGetValue(zone, out var path))
                return;

            var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;

            path.Fill = active
                ? MakeAlphaBrush(brush, 40)
                : Brushes.Transparent;

            path.Stroke = active ? MakeAlphaBrush(brush, 85) : null;
            path.StrokeThickness = active ? 1.5 : 0;

            Panel.SetZIndex(path, active ? 901 : 499);

            SetPolylineSegmentCircleVisual(zone, active);
        }

        private void SetPolylineSegmentCircleVisual(ActivationZone zone, bool active)
        {
            if (!_segmentToCircles.TryGetValue(zone, out var circles))
                return;

            var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;

            foreach (var circle in circles)
            {
                circle.Fill = active
                    ? MakeAlphaBrush(brush, 50)
                    : MakeAlphaBrush(brush, 20);

                circle.Stroke = active ? MakeAlphaBrush(brush, 85) : null;
                circle.StrokeThickness = active ? 1.5 : 0;

                Panel.SetZIndex(circle, active ? 902 : 500);
            }
        }

        private double DistancePointToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;

            if (dx == 0 && dy == 0)
                return Math.Sqrt(Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2));

            double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
            t = Math.Clamp(t, 0, 1);

            double closestX = a.X + t * dx;
            double closestY = a.Y + t * dy;

            return Math.Sqrt(Math.Pow(p.X - closestX, 2) + Math.Pow(p.Y - closestY, 2));
        }

        private void SetActivationZoneVisual(ActivationZone zone, bool active)
        {
            if (zone.Rectangle != null)
            {
                zone.Rectangle.StrokeThickness = active ? 6 : 2;
                return;
            }

            SetPolylineSegmentVisual(zone, active);
        }

        /// <summary>
        /// Determines if a given point is inside a rotated rectangle defined by an activation zone.
        /// </summary>
        /// <param name="point">The point to check.</param>
        /// <param name="zone">The activation zone.</param>
        /// <returns>True if the point is inside the rotated rectangle, false otherwise.</returns>
        private static bool IsPointInRotatedRectangle(Point point, ActivationZone zone)
        {
            var rect = zone.Rectangle;
            double left = Canvas.GetLeft(rect);
            double top = Canvas.GetTop(rect);
            double width = rect.Width;
            double height = rect.Height;
            double angle = zone.Azimuth;

            // Center of rotation (bottom center of rectangle)
            double centerX = left + width / 2.0;
            double centerY = top + height;

            // Rotate point back by -angle to get it into rectangle's local coordinate system
            double rad = -angle * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            double dx = point.X - centerX;
            double dy = point.Y - centerY;
            double localX = dx * cos - dy * sin + centerX;
            double localY = dx * sin + dy * cos + centerY;

            // Check if the point is inside the non-rotated rectangle
            return localX >= left && localX <= left + width &&
                   localY >= top && localY <= top + height;
        }

        /// <summary>
        /// Entry point for loading a playback file. Shows a loading overlay for the duration.
        /// </summary>
    }
}
