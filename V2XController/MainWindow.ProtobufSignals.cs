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
    // Protobuf, loading overlay, terminal and tram signal handling
    public partial class MainWindow
    {
        private void OpenProtobuf_Click(object sender, RoutedEventArgs e)
        {
            if (_protobufWindow == null || !_protobufWindow.IsLoaded)
            {
                _protobufWindow = new ProtobufWindow { Owner = this };

                _protobufWindow.Closed += (s, args) =>
                {
                    _protobufWindow = null;
                    this.Show();
                    this.Activate();
                };

                this.Hide();
                _protobufWindow.Show();
            }
            else
            {
                if (_protobufWindow.WindowState == WindowState.Minimized)
                    _protobufWindow.WindowState = WindowState.Normal;
                _protobufWindow.Activate();
            }
        }

        /// <summary>
        /// Shows a loading overlay over the map canvas with a determinate progress bar.
        /// Safe to call from any thread.
        /// </summary>
        private void ShowLoadingOverlay(string message = "Loading replay...")
        {
            Dispatcher.Invoke(() =>
            {
                if (_loadingOverlay != null) return;

                _loadingProgressBar = new ProgressBar
                {
                    Width = 300,
                    Height = 16,
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0,
                    Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 215))
                };

                var label = new TextBlock
                {
                    Text = message,
                    Foreground = Brushes.White,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 10)
                };

                var inner = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                inner.Children.Add(label);
                inner.Children.Add(_loadingProgressBar);

                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(235, 28, 28, 28)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(32, 20, 32, 20),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = inner
                };

                double w = TileCanvas.ActualWidth > 0 ? TileCanvas.ActualWidth : 800;
                double h = TileCanvas.ActualHeight > 0 ? TileCanvas.ActualHeight : 600;

                _loadingOverlay = new Border
                {
                    Width = w,
                    Height = h,
                    Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                    IsHitTestVisible = true,
                    Child = card
                };

                Canvas.SetLeft(_loadingOverlay, 0);
                Canvas.SetTop(_loadingOverlay, 0);
                Panel.SetZIndex(_loadingOverlay, int.MaxValue - 1);
                TileCanvas.Children.Add(_loadingOverlay);
            });
        }

        /// <summary>
        /// Updates the loading overlay progress bar (0–100). Safe to call from any thread.
        /// </summary>
        private void UpdateLoadingProgress(double percent)
        {
            if (_loadingProgressBar == null) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_loadingProgressBar != null)
                    _loadingProgressBar.Value = Math.Clamp(percent, 0, 100);
            });
        }

        /// <summary>
        /// Hides and removes the loading overlay. Safe to call multiple times or from any thread.
        /// </summary>
        private void HideLoadingOverlay()
        {
            Dispatcher.Invoke(() =>
            {
                if (_loadingOverlay == null) return;
                if (TileCanvas.Children.Contains(_loadingOverlay))
                    TileCanvas.Children.Remove(_loadingOverlay);
                _loadingOverlay = null;
                _loadingProgressBar = null;
            });
        }

        /// <summary>
        /// Detects if a line contains a Protobuf message (Base64 or Hex)
        /// </summary>
        private bool IsProtobufMessage(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return false;

            line = line.Trim();

            // Skip XML messages
            if (line.StartsWith("<") || line.Contains("<CAM") || line.Contains("<SRV"))
                return false;

            // Base64 detection: valid chars + correct padding
            if (line.Length >= 8 && line.Length % 4 == 0)
            {
                if (line.All(c => (c >= 'A' && c <= 'Z') ||
                                 (c >= 'a' && c <= 'z') ||
                                 (c >= '0' && c <= '9') ||
                                 c == '+' || c == '/' || c == '='))
                {
                    return true;
                }
            }

            // Hex detection: even length, only hex chars
            if (line.Length >= 16 && line.Length % 2 == 0)
            {
                if (line.All(c => (c >= '0' && c <= '9') ||
                                 (c >= 'A' && c <= 'F') ||
                                 (c >= 'a' && c <= 'f')))
                {
                    return true;
                }
            }

            return false;
        }

        private int Mod(int a, int b)
        {
            int r = a % b;
            if (r < 0)
            {
                r += b;
            }
            return r;
        }

        private void PolylineWidthTB_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = PolylineWidthTB?.Text?.Trim() ?? "";
            text = text.Replace(',', '.');

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) && width > 0)
            {
                _polylineZoneWidthMeters = Math.Clamp(width, 1.0, 500.0);
                Console.WriteLine($"[POLYLINE] Zone width set to {_polylineZoneWidthMeters:F1} m (±{_polylineZoneWidthMeters / 2:F1} m from line)");
            }
        }

        private void PolylineZonesCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
            {
                foreach (ActivationZone zone in e.NewItems)
                {
                    _polylineRows.Add(zone);
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                foreach (ActivationZone zone in e.OldItems)
                {
                    _polylineRows.Remove(zone);
                }
            }
        }

        private void AddPolylineSegmentToTable(Point p1, Point p2, double widthMeters)
        {
            if (currentPolyline == null) return;

            var (lat1, lon1) = ConvertCanvasXYToLatLon(p1.X, p1.Y, zoom);
            var (lat2, lon2) = ConvertCanvasXYToLatLon(p2.X, p2.Y, zoom);

            double centerLat = (lat1 + lat2) / 2.0;
            double centerLon = (lon1 + lon2) / 2.0;
            double lengthMeters = HaversineMeters(lat1, lon1, lat2, lon2);
            int azimuth = CalculateAzimuth(p1, p2);

            int segmentIndex = polylinePoints.Count - 2;
            bool isRtvMode = IsSwitchMode();

            int mainZone = 0;
            int subZone = 0;

            // First check current polyline's own segments
            if (_polylineToSegmentZones.TryGetValue(currentPolyline, out var existingSegments) && existingSegments.Count > 0)
            {
                var lastSegment = existingSegments.OrderByDescending(s => s.MainZone).ThenByDescending(s => s.SubZone).First();
                mainZone = lastSegment.MainZone;
                subZone = lastSegment.SubZone + 1;
            }
            else if (PolylineZonesCollection.Count > 0)
            {
                // New polyline - continue from last zone in collection (regardless of type)
                var lastGlobal = PolylineZonesCollection
                    .OrderByDescending(z => z.MainZone)
                    .ThenByDescending(z => z.SubZone)
                    .First();

                Console.WriteLine($"[SEGMENT] New polyline - continuing from last global: Main={lastGlobal.MainZone}, Sub={lastGlobal.SubZone}, Type={lastGlobal.SegmentType}");

                mainZone = lastGlobal.MainZone;
                subZone = lastGlobal.SubZone + 1;
            }

            // Advance with wrapping
            if (isRtvMode)
            {
                if (subZone > 6)
                {
                    mainZone++;
                    subZone = 0;
                    if (mainZone > 4)
                    {
                        mainZone = 4;
                        subZone = 6;
                        Console.WriteLine($"[SEGMENT] WARNING: Maximum RTV zones reached (4/6)!");
                    }
                }
            }
            else
            {
                if (subZone > 4)
                {
                    mainZone++;
                    subZone = 0;
                    if (mainZone > 3)
                    {
                        mainZone = 3;
                        subZone = 4;
                        Console.WriteLine($"[SEGMENT] WARNING: Maximum WLC zones reached (3/4)!");
                    }
                }
            }

            string color = GetColorForMainZone(mainZone, isRtvMode);
            string zoneName = GeneratePolylineZoneName(mainZone, subZone, isRtvMode);

            Console.WriteLine($"[SEGMENT] Adding segment {segmentIndex}: Name={zoneName}, Main={mainZone}, Sub={subZone}, Color={color}, Mode={(isRtvMode ? "RTV" : "WLC")}");

            var zone = new ActivationZone
            {
                Name = zoneName,
                Latitude = centerLat,
                Longitude = centerLon,
                Width = widthMeters,
                Height = lengthMeters,
                Azimuth = azimuth,
                Color = color,
                PolylineId = Guid.Empty,
                SegmentIndex = segmentIndex,
                SegmentType = isRtvMode ? "RTV" : "WLC",
                MainZone = mainZone,
                SubZone = subZone,
                LastTramId = "-"
            };

            if (!_polylineToSegmentZones.ContainsKey(currentPolyline))
                _polylineToSegmentZones[currentPolyline] = new List<ActivationZone>();

            _polylineToSegmentZones[currentPolyline].Add(zone);
            zone.PropertyChanged += ActivationZone_PropertyChanged;

            _polylineRows.Add(zone);

            if (!_suspendPolylineZoneLiveSort)
            {
                SetPolylineZonesLiveSorting(false);
                _suspendPolylineZoneLiveSort = true;
            }

            PolylineZonesCollection.Add(zone);
        }

        private static string GeneratePolylineZoneName(int mainZone, int subZone, bool isRtvMode)
        {
            // interně 0-based, zobrazujeme 1..7
            int sub = Math.Clamp(subZone, 0, 6) + 1;

            if (isRtvMode)
            {
                if (mainZone <= 1)
                {
                    // Přibližovací: P1-X (main=0) nebo P2-X (main=1)
                    int adjustedMain = mainZone + 1;
                    return $"P{adjustedMain}-{sub}";
                }
                else if (mainZone == 2)
                {
                    // Blokovací: B1..B7
                    return $"B{sub}";
                }
                else
                {
                    // Vzdalovací: V1-X (main=3->1), V2-X (main=4->2)
                    int adjustedMain = mainZone - 2;
                    return $"V{adjustedMain}-{sub}";
                }
            }

            // WLC / default naming
            return $"Z{mainZone + 1}-{sub}";
        }

        private static string GetColorForMainZone(int mainZone, bool isRtvMode)  // ← OPRAVENO: přidán parametr
        {
            if (isRtvMode)
            {
                // RTV: 0-1 = Zelená (P), 2 = Červená (B), 3-4 = Modrá (V)
                if (mainZone <= 1)
                    return "#008000"; // green
                if (mainZone == 2)
                    return "#FF0000"; // red
                                      // main >=3
                return "#0000FF"; // blue
            }
            else
            {
                // WLC: 0 = Červená, 1 = Zelená, 2 = Modrá, 3 = Magenta
                return mainZone switch
                {
                    0 => "#FFFF0000", // Red
                    1 => "#008000",     // Green
                    2 => "#FF0000FF", // Blue
                    3 => "#FFFF00FF", // Magenta
                    _ => "#FFFF0000"  // Default Red
                };
            }
        }

        private void UpdatePolylineDirectionArrows(Polyline polyline, List<Point> points)
        {
            if (polyline == null) return;

            if (!_polylineDirectionArrows.TryGetValue(polyline, out var arrows))
            {
                arrows = new List<System.Windows.Shapes.Line>();
                _polylineDirectionArrows[polyline] = arrows;
            }

            // Remove existing arrow shapes
            foreach (var line in arrows.ToList())
            {
                if (TileCanvas.Children.Contains(line))
                    TileCanvas.Children.Remove(line);
            }
            arrows.Clear();

            if (points == null || points.Count < 2)
                return;

            // Arrow density: ~desiredArrows per polyline
            int desiredArrows = 36;
            int step = Math.Max(1, (points.Count - 1) / desiredArrows);

            // Arrow visual parameters (in canvas pixels)
            double headLen = 10.0;
            double headWidth = 10.0;
            var stroke = Brushes.Black; // <- wings/arrow color forced to black
            double thickness = Math.Max(1.0, polyline?.StrokeThickness ?? 2.0);

            for (int i = 0; i < points.Count - 1; i += step)
            {
                var p1 = points[i];
                var p2 = points[i + 1];

                var dir = p2 - p1;
                double len = dir.Length;
                if (len < 0.5) continue;
                dir.Normalize();

                // midpoint of segment
                var mid = new Point((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5);

                // perpendicular vector
                var perp = new Vector(-dir.Y, dir.X);

                // Tip is on the center line; arrow "wings" go backwards
                var tip = mid;
                var left = tip - dir * headLen + perp * (headWidth * 0.5);
                var right = tip - dir * headLen - perp * (headWidth * 0.5);

                // Create two Line shapes (tip->left and tip->right) with black stroke
                var l1 = new System.Windows.Shapes.Line
                {
                    X1 = tip.X,
                    Y1 = tip.Y,
                    X2 = left.X,
                    Y2 = left.Y,
                    Stroke = stroke,
                    StrokeThickness = thickness,
                    IsHitTestVisible = false
                };
                var l2 = new System.Windows.Shapes.Line
                {
                    X1 = tip.X,
                    Y1 = tip.Y,
                    X2 = right.X,
                    Y2 = right.Y,
                    Stroke = stroke,
                    StrokeThickness = thickness,
                    IsHitTestVisible = false
                };

                TileCanvas.Children.Add(l1);
                TileCanvas.Children.Add(l2);
                Panel.SetZIndex(l1, 10000000);
                Panel.SetZIndex(l2, 10000000);

                arrows.Add(l1);
                arrows.Add(l2);
            }
        }

        private void SetPolylineZonesLiveSorting(bool enabled)
        {
            var view = CollectionViewSource.GetDefaultView(PolylineZonesCollection);
            if (view is ICollectionViewLiveShaping live)
            {
                live.IsLiveSorting = enabled;
            }
        }

        /// <summary>
        /// Handles decoded Protobuf messages and displays them on the map
        /// </summary>
        public void HandleProtobufMessage(string decodedJson)
        {
            try
            {
                bool isCamMessage = decodedJson.Contains("nearby_vehicle_detection");
                bool isSrvMessage = decodedJson.Contains("heartbeat");
                bool isIntersection = decodedJson.Contains("intersection_status")
                                   || decodedJson.Contains("intersection_pass_request_status")
                                   || decodedJson.Contains("intersection_request")
                                   || decodedJson.Contains("empty_response");

                if (isCamMessage)
                {
                    Console.WriteLine($"[PROTO] Detected: CAM (nearby_vehicle_detection)\n{decodedJson}");
                    HandleProtobufCamMessage(decodedJson);
                }
                else if (isSrvMessage)
                {
                    Console.WriteLine($"[PROTO] Detected: SRV (heartbeat)\n{decodedJson}");
                    HandleProtobufSrvMessage(decodedJson);
                }
                else if (isIntersection)
                {
                    Console.WriteLine($"[PROTO] Intersection message\n{decodedJson}");
                    HandleIntersectionStatus(decodedJson);
                }
                else
                {
                    Console.WriteLine($"[PROTO] Unknown Protobuf message type\n{decodedJson}");
                    IncrementCamErrorCount();
                }
            }
            catch (Exception ex)
            {
                IncrementCamErrorCount();
                Console.WriteLine($"[PROTO] Message handling failed - {ex.Message}");
            }
        }

        /// <summary>
        /// Handles Protobuf CAM messages (Nearby Vehicle Detection)
        /// </summary>
        private void HandleProtobufCamMessage(string decodedJson)
        {
            try
            {
                var protoCam = ProtoCam.ParseFromJson(decodedJson);
                if (protoCam == null)
                {
                    Console.WriteLine("[PROTO CAM] Failed to parse message");
                    IncrementCamErrorCount();
                    return;
                }

                if (string.IsNullOrWhiteSpace(protoCam.VehicleId))
                {
                    Console.WriteLine($"[PROTO CAM] Skipping message with missing/invalid vehicle_id");
                    return;
                }

                var v2xMsg = protoCam.ToV2XMessage();
                double accuracy = protoCam.AccuracyInMeters ?? 0.0;

                var fakeXml = $"<vehPt lat=\"{v2xMsg.Latitude}\" lon=\"{v2xMsg.Longitude}\" speed=\"{v2xMsg.Speed}\" heading=\"{v2xMsg.Heading}\" accuracy=\"{accuracy}\" altitude=\"0\" />";

                var shortId = v2xMsg.VehicleID.Length > 4 ? v2xMsg.VehicleID[^4..] : v2xMsg.VehicleID;
                Console.WriteLine($"[PROTO] Detected: CAM (nearby_vehicle_detection)\n{decodedJson}");
                Console.WriteLine($"PROTO CAM {shortId} lat={v2xMsg.Latitude:F6} lon={v2xMsg.Longitude:F6} spd={v2xMsg.Speed:F1} hdg={v2xMsg.Heading:F0}");

                if (protoCam.AccuracyInMeters.HasValue && protoCam.AccuracyInMeters.Value > 0)
                    Console.WriteLine($"  acc={protoCam.AccuracyInMeters.Value:F1} m");

                if (_timeshiftEnabled && _timeshiftPaused)
                    return;

                if (_isPlaybackSessionActive || _isTimeshiftPlaybackActive)
                    return;

                HandleV2XMessage(v2xMsg, fakeXml);
                IncrementCamOkCount();
            }
            catch (Exception ex)
            {
                IncrementCamErrorCount();
                Console.WriteLine($"[PROTO CAM] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles Protobuf SRV messages (Heartbeat)
        /// </summary>
        private void HandleProtobufSrvMessage(string decodedJson)
        {
            try
            {
                var protoSrv = ProtoSrv.ParseFromJson(decodedJson);
                if (protoSrv == null)
                {
                    Console.WriteLine("[PROTO SRV] Failed to parse message");
                    IncrementSrvErrorCount();
                    return;
                }

                var srvMsg = protoSrv.ToSrvMessage();

                Console.WriteLine($"PROTO SRV dev={protoSrv.DeviceId ?? "?"} lat={srvMsg.Latitude:F6} lon={srvMsg.Longitude:F6}");

                if (protoSrv.AccuracyInMeters.HasValue && protoSrv.AccuracyInMeters.Value > 0)
                    Console.WriteLine($"  acc={protoSrv.AccuracyInMeters.Value:F1} m");

                if (protoSrv.Altitude.HasValue)
                    Console.WriteLine($"  alt={protoSrv.Altitude.Value:F1} m");

                if (_timeshiftEnabled && _timeshiftPaused)
                    return;

                if (_isPlaybackSessionActive || _isTimeshiftPlaybackActive)
                    return;

                if (srvMsg.Latitude != 0 && srvMsg.Longitude != 0)
                {
                    bool positionChanged = !srvLatitude.HasValue ||
                                           Math.Abs(srvLatitude.Value - srvMsg.Latitude) > 1e-6 ||
                                           !srvLongitude.HasValue ||
                                           Math.Abs(srvLongitude.Value - srvMsg.Longitude) > 1e-6;

                    srvLatitude = srvMsg.Latitude;
                    srvLongitude = srvMsg.Longitude;

                    if (positionChanged)
                        _ = EnsureLocalAreaAltitudeAsync(force: true);

                    string logicalId = srvMsg.LogicalId;
                    _lastLatLon[logicalId] = (srvMsg.Latitude, srvMsg.Longitude);

                    // Build RSU tag: "RSU" + numeric part of logicalId, max 10 chars
                    string numPart = new string(logicalId.Where(char.IsDigit).ToArray());
                    if (string.IsNullOrEmpty(numPart)) numPart = logicalId;
                    string rsuTag = "RSU" + numPart;
                    if (rsuTag.Length > 6) rsuTag = rsuTag[..6];

                    var (canvasX, canvasY) = ConvertLatLonToCanvasXY(srvMsg.Latitude, srvMsg.Longitude);

                    var existingEllipse = TileCanvas.Children.OfType<Ellipse>()
                        .FirstOrDefault(e => e.Tag is string t && t == rsuTag);

                    if (existingEllipse != null)
                    {
                        Canvas.SetLeft(existingEllipse, canvasX - existingEllipse.Width / 2);
                        Canvas.SetTop(existingEllipse, canvasY - existingEllipse.Height / 2);
                    }
                    else
                    {
                        var point = new Ellipse
                        {
                            Width = 10,
                            Height = 10,
                            Fill = Brushes.Red,
                            Stroke = Brushes.Black,
                            StrokeThickness = 1,
                            Tag = rsuTag
                        };
                        Canvas.SetLeft(point, canvasX - point.Width / 2);
                        Canvas.SetTop(point, canvasY - point.Height / 2);
                        TileCanvas.Children.Add(point);
                    }

                    var existingLabel = TileCanvas.Children.OfType<TextBlock>()
                        .FirstOrDefault(tb => tb.Tag is string t && t == rsuTag);

                    if (existingLabel != null)
                    {
                        Canvas.SetLeft(existingLabel, canvasX + 7);
                        Canvas.SetTop(existingLabel, canvasY - 8);
                    }
                    else
                    {
                        var label = new TextBlock
                        {
                            Text = logicalId,
                            Foreground = Brushes.Red,
                            FontSize = 10,
                            FontWeight = FontWeights.Bold,
                            Tag = rsuTag
                        };
                        Canvas.SetLeft(label, canvasX + 7);
                        Canvas.SetTop(label, canvasY - 8);
                        TileCanvas.Children.Add(label);
                    }

                    if (CircleCheckBox?.IsChecked == true)
                        DrawRadiusCircle();
                }

                if (_timeshiftEnabled)
                {
                    var fakeXml = $@"<SRV lat=""{srvMsg.Latitude}"" lon=""{srvMsg.Longitude}"" />";
                    AddSrvToBuffer(fakeXml);
                }

                IncrementSrvOkCount();
            }
            catch (Exception ex)
            {
                IncrementSrvErrorCount();
                Console.WriteLine($"[PROTO SRV] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// DEPRECATED
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        //private void TestProtobuf_Click(object sender, RoutedEventArgs e)
        //{
        //    try
        //    {
        //        // První kliknutí - dekódovat a uložit zprávu
        //        if (string.IsNullOrEmpty(_protobufTestDecoded))
        //        {
        //            string testMessage = "COvDAxILCJrg8c8GEKTWiiFShwEKCwia4PHPBhCk1oohEhAKCjIzNTg5NDUwMTcQPBgKUjoKCQkAAACgmRlsQBEBP/Tu2etIQBnCNdKtMkgyQFIbUB6iAQUN4XqkQJIDDgoFDeF6pEASBQ3heqRAXTMzP0FlZuaqQ6UBuKeAQ/IBGQgUogEEMTMzOfIBBDIwMDSABQDCDAPwAQA=";

        //            if (!ProtobufParser.TryDecodeProtobufFromHex(testMessage, out _protobufTestDecoded))
        //            {
        //                Console.WriteLine("ERROR: Failed to decode");
        //                return;
        //            }

        //            Console.WriteLine("Click to toggle tram position");
        //            _protobufTestLatOffset = 0.0;
        //            _protobufTestDirection = true;
        //        }

        //        // Parsovat zprávu
        //        var protoCam = ProtoCam.ParseFromJson(_protobufTestDecoded);
        //        if (protoCam == null)
        //        {
        //            Console.WriteLine("ERROR: Parse failed");
        //            return;
        //        }

        //        // Přepnout směr
        //        _protobufTestDirection = !_protobufTestDirection;

        //        double jumpMeters = 50.0;
        //        double latDelta = jumpMeters / 111000.0;

        //        if (_protobufTestDirection)
        //        {
        //            _protobufTestLatOffset += latDelta;
        //        }
        //        else
        //        {
        //            _protobufTestLatOffset -= latDelta;
        //        }

        //        // Aplikovat offset
        //        protoCam.Latitude = (protoCam.Latitude ?? 0.0) + _protobufTestLatOffset;
        //        protoCam.Timestamp = DateTime.UtcNow;
        //        protoCam.Speed = 15.0;
        //        protoCam.Heading = _protobufTestDirection ? 0.0 : 180.0;

        //        // Převést a vykreslit
        //        var v2xMsg = protoCam.ToV2XMessage();
        //        v2xMsg.MessageType = "CAM";

        //        var fakeXml = $@"<vehPt lat=""{v2xMsg.Latitude}"" lon=""{v2xMsg.Longitude}"" speed=""{v2xMsg.Speed}"" heading=""{v2xMsg.Heading}"" accuracy=""{protoCam.AccuracyInMeters ?? 0.0}"" />";

        //        var shortId = v2xMsg.VehicleID.Length > 4 ? v2xMsg.VehicleID[^4..] : v2xMsg.VehicleID;
        //        var arrow = _protobufTestDirection ? "▲" : "▼";
        //        Console.WriteLine($"{arrow} JUMP {jumpMeters}m | {shortId} | Lat={v2xMsg.Latitude:F6} | Lon={v2xMsg.Longitude} | Total delta={_protobufTestLatOffset * 111000:F0}m");

        //        // Vykreslit na mapu
        //        HandleV2XMessage(v2xMsg, fakeXml);
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine($"ERROR: {ex.Message}", Brushes.Red);
        //    }
        //}

        private void SelectZoneInTable(ActivationZone zone)
        {
            var switchGrid = this.FindName("SwitchZonesDataGrid") as DataGrid;

            if (IsSwitchZone(zone))
            {
                ActivationZonesDataGrid.SelectedItem = null;
                if (switchGrid != null)
                {
                    switchGrid.SelectedItem = zone;
                    switchGrid.ScrollIntoView(zone);
                }
            }
            else
            {
                if (switchGrid != null)
                    switchGrid.SelectedItem = null;
                ActivationZonesDataGrid.SelectedItem = zone;
                ActivationZonesDataGrid.ScrollIntoView(zone);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        private static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DrawMenuBar(IntPtr hWnd);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private const uint SC_CLOSE = 0xF060;
        private const uint MF_BYCOMMAND = 0x00000000;

        private bool _consoleAllocated = false;
        private IntPtr _consoleHwnd = IntPtr.Zero;

        private void DebugTerminal_Click(object sender, RoutedEventArgs e)
        {
            if (!_consoleAllocated)
            {
                if (!AllocConsole())
                    return;

                _consoleAllocated = true;
                _consoleHwnd = GetConsoleWindow();

                DisableConsoleCloseButton();

                var stdOut = new StreamWriter(Console.OpenStandardOutput())
                {
                    AutoFlush = true
                };

                var stdErr = new StreamWriter(Console.OpenStandardError())
                {
                    AutoFlush = true
                };

                Console.SetOut(stdOut);
                Console.SetError(stdErr);

                Console.Title = "V2X Controller – Debug mode";

                Console.WriteLine("=== V2X Controller Debug Mode ===");
                Console.WriteLine("Close button is disabled for simplicity.");
                Console.WriteLine("Heartbeat period: 3s");
            }
            else
            {
                ToggleConsole();
            }
        }

        private void DisableConsoleCloseButton()
        {
            if (_consoleHwnd == IntPtr.Zero)
                return;

            IntPtr hMenu = GetSystemMenu(_consoleHwnd, false);

            if (hMenu != IntPtr.Zero)
            {
                DeleteMenu(hMenu, SC_CLOSE, MF_BYCOMMAND);
                DrawMenuBar(_consoleHwnd);
            }
        }

        private void ToggleConsole()
        {
            if (_consoleHwnd == IntPtr.Zero)
                _consoleHwnd = GetConsoleWindow();

            if (_consoleHwnd == IntPtr.Zero)
                return;

            ShowWindow(_consoleHwnd, SW_HIDE);
        }

        private void ClearZoneTableSelection()
        {
            ActivationZonesDataGrid.SelectedItem = null;
            if (this.FindName("SwitchZonesDataGrid") is DataGrid switchGrid)
                switchGrid.SelectedItem = null;
        }

        private void EnsureTramSignals()
        {
            foreach (var signal in _tramSignals)
            {
                if (signal.Control != null)
                    continue;

                UserControl control;

                if (signal.Side == TramSignalSide.Left)
                {
                    control = new TramSignalControlLeft();
                }
                else
                {
                    control = new TramSignalControlRight();
                }

                var tb = new TextBlock
                {
                    Tag = "Signal",
                    Text = signal.Title,
                    Foreground = Brushes.Black,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                };

                signal.SignalLabel = tb;

                signal.Control = control;

                control.Loaded += (s, e) => UpdateTramSignalPosition(signal);

                TileCanvas.Children.Add(control);
                Panel.SetZIndex(control, 1055);
                TileCanvas.Children.Add(signal.SignalLabel);
                Panel.SetZIndex(signal.SignalLabel, 1056);

                Dispatcher.BeginInvoke(
                    () => UpdateTramSignalPosition(signal),
                    DispatcherPriority.Background);
            }
        }

        private void UpdateTramSignalPositions()
        {
            foreach (var signal in _tramSignals)
            {
                UpdateTramSignalPosition(signal);
            }
        }

        private void UpdateTramSignalPosition(TramSignalInstance signal)
        {
            if (signal.Control == null)
                return;

            var (x, y) = ConvertLatLonToCanvasXY(signal.Latitude, signal.Longitude);

            double w = signal.Control.ActualWidth;
            double h = signal.Control.ActualHeight;

            if (w <= 0)
                w = signal.Control.Width;

            if (h <= 0)
                h = signal.Control.Height;

            double signalScale = Math.Pow(2, zoom - 18);
            signalScale = Math.Clamp(signalScale, 0.35, 1.0);

            double scaledW = w * signalScale;
            double scaledH = h * signalScale;

            Canvas.SetLeft(signal.Control, x - scaledW / 2.0);
            Canvas.SetTop(signal.Control, y - scaledH / 2.0);

            signal.Control.RenderTransformOrigin = new Point(0.5, 0.5);

            var controlTransform = new TransformGroup();
            controlTransform.Children.Add(new ScaleTransform(signalScale, signalScale));
            controlTransform.Children.Add(new RotateTransform(signal.RotationDeg));
            signal.Control.RenderTransform = controlTransform;

            if (signal.SignalLabel != null)
            {
                signal.SignalLabel.Text = signal.Title;
                signal.SignalLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                double textWidth = signal.SignalLabel.DesiredSize.Width;
                double textHeight = signal.SignalLabel.DesiredSize.Height;

                double labelScale = signalScale;

                double scaledTextWidth = textWidth * labelScale;
                double scaledTextHeight = textHeight * labelScale;

                double angleRad = signal.RotationDeg * Math.PI / 180.0;

                double offset = scaledH / 2.0 + scaledTextHeight / 2.0 + 4;

                double dx = -Math.Sin(angleRad) * offset;
                double dy = Math.Cos(angleRad) * offset;

                Canvas.SetLeft(signal.SignalLabel, x + dx - scaledTextWidth / 2.0);
                Canvas.SetTop(signal.SignalLabel, y + dy - scaledTextHeight / 2.0);

                var labelTransform = new TransformGroup();
                labelTransform.Children.Add(new ScaleTransform(labelScale, labelScale));
                labelTransform.Children.Add(new RotateTransform(signal.RotationDeg));

                signal.SignalLabel.RenderTransformOrigin = new Point(0.5, 0.5);
                signal.SignalLabel.RenderTransform = labelTransform;
            }

        }

        private void HandleIntersectionStatus(string decodedJson)
        {
            int intersectionId;
            TramSignalDirection direction;

            if (!TryParseIntersectionDirection(decodedJson, out intersectionId, out direction))
            {
                direction = ParseIntersectionDirection(decodedJson);

                if (direction == TramSignalDirection.None)
                {
                    Console.WriteLine("[SIGNAL LIVE] Intersection message found but direction=None");
                    return;
                }

                intersectionId = 640; // stejné správné návěstidlo jako v replay fallbacku
                Console.WriteLine("[SIGNAL LIVE] intersection_id not found, using fallback 640");
            }

            Console.WriteLine($"[SIGNAL LIVE] Intersection {intersectionId}  {direction}");

            Dispatcher.Invoke(() =>
            {
                EnsureTramSignals();

                var signal = _tramSignals.FirstOrDefault(s => s.IntersectionId == intersectionId);

                if (signal == null)
                {
                    Console.WriteLine($"[SIGNAL LIVE] No tram signal registered for intersection_id={intersectionId}");
                    return;
                }

                if (signal.Control is TramSignalControlLeft left)
                {
                    left.Left = direction;
                }

                if (signal.Control is TramSignalControlRight right)
                {
                    right.Right = direction;
                }
            });
        }

        private static bool TryParseIntersectionDirection(
            string decodedJson,
            out int intersectionId,
            out TramSignalDirection direction)
        {
            intersectionId = -1;
            direction = TramSignalDirection.None;

            try
            {
                using var doc = JsonDocument.Parse(decodedJson);
                var root = doc.RootElement;

                JsonElement? statusEl = null;
                foreach (var k in new[] { "intersection_status", "intersectionStatus" })
                {
                    if (root.TryGetProperty(k, out var el))
                    {
                        statusEl = el;
                        break;
                    }
                }

                if (!statusEl.HasValue)
                {
                    Console.WriteLine("[SIGNAL PARSE] No intersection_status key found");
                    return false;
                }

                var status = statusEl.Value;

                if (!TryReadInt(status, out intersectionId, "intersection_id", "intersectionId"))
                {
                    if (!TryReadInt(root, out intersectionId, "intersection_id", "intersectionId"))
                    {
                        Console.WriteLine("[SIGNAL PARSE] No intersection_id found");
                        return false;
                    }
                }

                direction = ParseIntersectionDirection(decodedJson);

                if (direction == TramSignalDirection.None)
                {
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SIGNAL PARSE] Failed: {ex.Message}");
                return false;
            }
        }

        private static bool TryReadInt(JsonElement element, out int value, params string[] keys)
        {
            value = -1;

            foreach (var key in keys)
            {
                if (!element.TryGetProperty(key, out var el))
                {
                    continue;
                }

                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value))
                {
                    return true;
                }

                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Parses <c>intersection_status.movement_states[].signal_state</c> and maps it
        /// to <see cref="TramSignalDirection"/> using the authoritative
        /// <c>TrafficLightSignalStateEnum</c> values.
        /// Lane 21 = straight, lane 23 = left. Green direction is lane-aware;
        /// non-directional states (amber, stop) apply to any lane.
        /// </summary>
        private static TramSignalDirection ParseIntersectionDirection(string decodedJson)
        {
            const int LaneIdStraight = 21;
            const int LaneIdLeft = 23;

            try
            {
                using var doc = JsonDocument.Parse(decodedJson);
                var root = doc.RootElement;

                Console.WriteLine($"[SIGNAL PARSE] Top-level keys: {string.Join(", ", root.EnumerateObject().Select(p => p.Name))}");

                // Try both snake_case and camelCase wrappers
                JsonElement? statusEl = null;
                foreach (var k in new[] { "intersection_status", "intersectionStatus" })
                {
                    if (root.TryGetProperty(k, out var el)) { statusEl = el; break; }
                }

                if (!statusEl.HasValue)
                {
                    Console.WriteLine("[SIGNAL PARSE] No intersection_status key found — skipping");
                    return TramSignalDirection.None;
                }

                var status = statusEl.Value;
                Console.WriteLine($"[SIGNAL PARSE] intersection_status keys: {string.Join(", ", status.EnumerateObject().Select(p => p.Name))}");

                JsonElement? array = null;
                foreach (var k in new[] { "signal_group_states", "signalGroupStates", "movement_states", "movementStates" })
                {
                    if (status.TryGetProperty(k, out var el) && el.ValueKind == JsonValueKind.Array)
                    {
                        array = el;
                        Console.WriteLine($"[SIGNAL PARSE] Found array under key '{k}', length={el.GetArrayLength()}");
                        break;
                    }
                }

                if (!array.HasValue || array.Value.GetArrayLength() == 0)
                {
                    Console.WriteLine("[SIGNAL PARSE] No signal_group_states / movement_states array found or empty");
                    return TramSignalDirection.None;
                }

                // Collect per-lane states keyed by signal_group_id
                string straightState = string.Empty;
                string leftState = string.Empty;
                string rightState = string.Empty;
                var allStates = new List<string>(array.Value.GetArrayLength());

                foreach (var ms in array.Value.EnumerateArray())
                {
                    // Resolve signal_group_id
                    int groupId = -1;
                    foreach (var gk in new[] { "signal_group_id", "signalGroupId" })
                    {
                        if (ms.TryGetProperty(gk, out var gidEl) && gidEl.TryGetInt32(out var gid))
                        {
                            groupId = gid;
                            break;
                        }
                    }

                    // Resolve state string
                    string stateVal = string.Empty;
                    foreach (var stateKey in new[] { "current_state", "currentState", "signal_state", "signalState" })
                    {
                        if (ms.TryGetProperty(stateKey, out var sigState))
                        {
                            var s = sigState.GetString();
                            if (!string.IsNullOrEmpty(s)) { stateVal = s; break; }
                        }
                    }

                    if (string.IsNullOrEmpty(stateVal)) continue;
                    allStates.Add(stateVal);

                    if (groupId == LaneIdStraight) straightState = stateVal;
                    else if (groupId == LaneIdLeft) leftState = stateVal;
                }

                Console.WriteLine($"[SIGNAL PARSE] lane {LaneIdStraight} (straight)={straightState}, lane {LaneIdLeft} (left)={leftState}");
                Console.WriteLine($"[SIGNAL PARSE] All states ({allStates.Count}): {string.Join(", ", allStates)}");

                if (allStates.Count == 0) return TramSignalDirection.None;

                // Green is lane-aware: which specific lane is green determines direction
                bool straightGreen = !string.IsNullOrEmpty(straightState) && IsSignalGreen(straightState);
                bool leftGreen = !string.IsNullOrEmpty(leftState) && IsSignalGreen(leftState);
                bool rightGreen = !string.IsNullOrEmpty(rightState) && IsSignalGreen(rightState);

                if (straightGreen) return TramSignalDirection.Straight;
                if (leftGreen) return TramSignalDirection.Left;
                if (rightGreen) return TramSignalDirection.Right;

                // Non-directional states: apply to whichever lane matches
                if (allStates.Any(IsSignalPreMovement)) return TramSignalDirection.PreMovement;
                if (allStates.Any(IsSignalStop)) return TramSignalDirection.Stop;

                Console.WriteLine("[SIGNAL PARSE] No matching state  None");
                return TramSignalDirection.None;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SIGNAL PARSE] Failed: {ex.Message}");
                return TramSignalDirection.None;
            }
        }

        // PERMISSIVE_MOVEMENT_ALLOWED (60) or PROTECTED_MOVEMENT_ALLOWED (70)
        private static bool IsSignalGreen(string s) =>
            s == "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PERMISSIVE_MOVEMENT_ALLOWED" ||
            s == "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PROTECTED_MOVEMENT_ALLOWED";

        // PRE_MOVEMENT (50) — red + amber phase
        private static bool IsSignalPreMovement(string s) =>
            s == "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PRE_MOVEMENT";

        // STOP_AND_REMAIN (40), STOP_THEN_PROCEED (30)
        private static bool IsSignalStop(string s) =>
            s == "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_STOP_AND_REMAIN" ||
            s == "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_STOP_THEN_PROCEED";

        private void UpdateTramSignalsForReplay(TimeSpan time)
        {
            if (_replaySignalFrames.Count == 0)
                return;

            int idx = -1;

            for (int i = 0; i < _replaySignalFrames.Count; i++)
            {
                if (_replaySignalFrames[i].ts <= time)
                    idx = i;
                else
                    break;
            }

            if (idx < 0)
                return;

            var frame = _replaySignalFrames[idx];

            EnsureTramSignals();

            var signal = _tramSignals.FirstOrDefault(s => s.IntersectionId == frame.intersectionId);

            if (signal == null)
            {
                Console.WriteLine($"[SIGNAL REPLAY] No tram signal registered for intersection_id={frame.intersectionId}");
                return;
            }

            if (signal.Control is TramSignalControlLeft left)
                left.Left = frame.direction;

            if (signal.Control is TramSignalControlRight right)
                right.Right = frame.direction;
        }

        /// <summary>
        /// Extracts intersection id from json
        /// </summary>
        /// <param name="decodedJson">Json we want to extract id from</param>
        /// <param name="intersectionId">Out int ID of an intersection</param>
        /// <returns>Intersection ID</returns>
        private static bool TryExtractIntersectionId(string decodedJson, out int intersectionId)
        {
            intersectionId = -1;

            try
            {
                using var doc = JsonDocument.Parse(decodedJson);

                return TryFindIntRecursive(
                    doc.RootElement,
                    out intersectionId,
                    "intersection_id",
                    "intersectionId",
                    "id");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Recursively finds Int from message
        /// </summary>
        /// <param name="element">Element from json that from where we are finding Int</param>
        /// <param name="value">Out value</param>
        /// <param name="names">Array of names (Ints) that that are returned</param>
        /// <returns>True: if there is Int present inside JSON; False: otherwise</returns>
        private static bool TryFindIntRecursive(JsonElement element, out int value, params string[] names)
        {
            value = -1;

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (names.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number &&
                            prop.Value.TryGetInt32(out value))
                            return true;

                        if (prop.Value.ValueKind == JsonValueKind.String &&
                            int.TryParse(prop.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                            return true;
                    }

                    if (TryFindIntRecursive(prop.Value, out value, names))
                        return true;
                }
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindIntRecursive(item, out value, names))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Applies halo to desired text
        /// </summary>
        /// <param name="tb">Desired textblock for highlighting</param>
        private static void ApplyTextHalo(TextBlock tb)
        {
            tb.Effect = new DropShadowEffect
            {

                Color = Colors.White,
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 1
            };
        }

        /// <summary>
        /// Vehicle lable styling (default bg color RGBA (220, 255, 255, 255))
        /// </summary>
        /// <param name="text">Text you want to style</param>
        /// <param name="color">Desired color</param>
        private void StyleVehicleLabel(TextBlock text, Brush color)
        {
            text.Foreground = color;
            text.Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
            text.Padding = new Thickness(4, 1, 4, 1);
            text.FontWeight = FontWeights.SemiBold;
            text.FontSize = 12;
            text.IsHitTestVisible = false;

            text.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 3,
                ShadowDepth = 0,
                Opacity = 0.45
            };

            Panel.SetZIndex(text, int.MaxValue);
        }

        /// <summary>
        /// Positions vehicle label on canvas
        /// </summary>
        /// <param name="text">Text you want to position</param>
        /// <param name="tramCenter">Tram center point</param>
        /// <param name="yOffset">Y offset from tram center</param>
        private void PositionVehicleLabel(TextBlock text, Point tramCenter, double yOffset)
        {
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Canvas.SetLeft(text, tramCenter.X + 22);
            Canvas.SetTop(text, tramCenter.Y + yOffset);
        }

        /// <summary>
        /// Positions all vehicle labels together (speed, VehID)
        /// </summary>
        /// <param name="idText"></param>
        /// <param name="speedText"></param>
        /// <param name="tramCenter"></param>
        private void PositionVehicleLabelsTogether(
            TextBlock? idText,
            TextBlock? speedText,
            Point tramCenter)
        {
            double left = tramCenter.X + 24;
            double top = tramCenter.Y - 22;

            if (idText != null)
            {
                idText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                Canvas.SetLeft(idText, left);
                Canvas.SetTop(idText, top);
                Panel.SetZIndex(idText, 1200);

                if (speedText != null)
                {
                    speedText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                    Canvas.SetLeft(speedText, left);
                    Canvas.SetTop(speedText, top + idText.DesiredSize.Height + 2);
                    Panel.SetZIndex(speedText, 1200);
                }
            }
            else if (speedText != null)
            {
                Canvas.SetLeft(speedText, left);
                Canvas.SetTop(speedText, top + 16);
                Panel.SetZIndex(speedText, 1200);
            }
        }

        private void ConnectionTypeComboBox_SelectionChanged(
    object sender,
    SelectionChangedEventArgs e)
        {
            bool ethernetSelected =
                ConnectionTypeComboBox?.SelectedIndex == 1;

            _connectionType = ethernetSelected
                ? ConnectionType.Ethernet
                : ConnectionType.Serial;

            if (SerialConnectionPanel != null)
            {
                SerialConnectionPanel.Visibility = ethernetSelected
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            if (EthernetConnectionPanel != null)
            {
                EthernetConnectionPanel.Visibility = ethernetSelected
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (RefreshComPorts != null)
            {
                RefreshComPorts.Visibility = ethernetSelected
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }
    }
}
