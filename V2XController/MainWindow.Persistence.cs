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
    // Status counters, XML/PNG persistence and recording
    public partial class MainWindow
    {
        // ===== METHODS FOR THE CAM AND SRV STATUSES =====

        /// <summary>
        /// Increments the count of CAM OK messages.
        /// </summary>
        private void IncrementCamOkCount()
        {
            return;
        }

        /// <summary>
        /// Increments the count of CAM error messages (CRC is not OK).
        /// </summary>
        private void IncrementCamErrorCount()
        {
            return;
        }

        /// <summary>
        /// Increments the count of SRV OK messages.
        /// </summary>
        private void IncrementSrvOkCount()
        {
            return;
        }

        /// <summary>
        /// Increments the count of SRV error messages.
        /// </summary>
        private void IncrementSrvErrorCount()
        {
            return;
        }

        // ===== METHODS FOR EXPORTING AND SAVING =====

        /// <summary>
        /// Exports the specified canvas to a PNG file.
        /// </summary>
        /// <param name="canvas">The canvas to export.</param>
        /// <param name="filePath">The file path to save the PNG.</param>
        private void ExportCanvasToPng(Canvas canvas, string filePath)
        {
            int width = (int)canvas.ActualWidth;
            int height = (int)canvas.ActualHeight;
            int dpi = 96;

            if (width == 0 || height == 0)
            {
                Console.WriteLine("[CANVAS] Canvas size is zero, export skipped.");
                return;
            }

            canvas.Measure(new Size(width, height));
            canvas.Arrange(new Rect(new Size(width, height)));
            canvas.UpdateLayout();

            RenderTargetBitmap rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);

            var dv = new DrawingVisual();

            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(canvas.RenderTransform);

                var vb = new VisualBrush(canvas);
                dc.DrawRectangle(vb, null, new Rect(new Point(), new Size(width, height)));

                dc.Pop();
            }

            rtb.Render(dv);

            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                encoder.Save(stream);
            }

            Console.WriteLine($"Export completed: {filePath}");
        }

        /// <summary>
        /// Saves the current map data to an XML file.
        /// </summary>
        /// <param name="filePath">The file path to save the XML.</param>
        private void SaveXML(string filePath)
        {
            XElement root = new XElement("MapData",
                new XAttribute("CenterLatitude", latitude.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("CenterLongitude", longitude.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("Zoom", zoom.ToString(CultureInfo.InvariantCulture))
            );

            XElement zonesElement = new XElement("ActivationZones");
            foreach (var kvp in activationZones)
            {
                var zone = kvp.Value;
                double startX = zone.StartPoint.X;
                double startY = zone.StartPoint.Y;
                var latlon = CanvasPixelsToLatLon(new Point(startX, startY), latitude, longitude, zoom);

                // Určit mode: Switch, RTV (P/B/V naming), nebo WLC
                string mode = zone.IsSwitchZone ? "Switch"
                    : (zone.Name?.StartsWith("P", StringComparison.Ordinal) == true ||
                       zone.Name?.StartsWith("B", StringComparison.Ordinal) == true ||
                       zone.Name?.StartsWith("V", StringComparison.Ordinal) == true) ? "RTV"
                    : "WLC";

                zonesElement.Add(new XElement("Zone",
                    new XAttribute("Name", zone.Name ?? string.Empty),
                    new XAttribute("Latitude", latlon.Y.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Longitude", latlon.X.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("StartX", startX.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("StartY", startY.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Width", zone.Width.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Height", zone.Height.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Azimuth", zone.Azimuth.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Color", zone.Color),
                    new XAttribute("MainZone", zone.MainZone.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("SubZone", zone.SubZone.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Mode", mode)
                ));
            }
            root.Add(zonesElement);

            XElement polylinesElement = new XElement("Polylines");

            foreach (var kvp in _polylineGeoPoints)
            {
                Polyline polyline = kvp.Key;
                List<(double lat, double lon)> geoPoints = kvp.Value;

                if (geoPoints.Count < 2)
                    continue;

                string polylineId = Guid.NewGuid().ToString();

                List<ActivationZone> segments = new List<ActivationZone>();
                if (_polylineToSegmentZones.TryGetValue(polyline, out var savedSegments))
                    segments = savedSegments;

                if (segments.Count > 0 && segments[0].PolylineId != Guid.Empty)
                    polylineId = segments[0].PolylineId.ToString();

                XElement polylineElement = new XElement("Polyline",
                    new XAttribute("Id", polylineId),
                    new XAttribute("Stroke", (polyline.Stroke as SolidColorBrush)?.Color.ToString() ?? "#FFFF0000"),
                    new XAttribute("StrokeThickness", polyline.StrokeThickness.ToString(CultureInfo.InvariantCulture))
                );

                XElement verticesElement = new XElement("Vertices");

                for (int i = 0; i < geoPoints.Count; i++)
                {
                    verticesElement.Add(new XElement("Vertex",
                        new XAttribute("Index", i.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("Latitude", geoPoints[i].lat.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("Longitude", geoPoints[i].lon.ToString(CultureInfo.InvariantCulture))
                    ));
                }

                polylineElement.Add(verticesElement);

                XElement segmentsElement = new XElement("Segments");

                foreach (var segment in segments.OrderBy(s => s.SegmentIndex))
                {
                    segmentsElement.Add(new XElement("Segment",
                        new XAttribute("Index", segment.SegmentIndex.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("Name", segment.Name ?? string.Empty),
                        new XAttribute("Latitude", segment.Latitude.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("Longitude", segment.Longitude.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("Width", segment.Width.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("Height", segment.Height.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("Azimuth", segment.Azimuth.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("Color", segment.Color ?? "#FF0000"),
                        new XAttribute("MainZone", segment.MainZone.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("SubZone", segment.SubZone.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("SegmentType", segment.SegmentType ?? string.Empty),
                        new XAttribute("IsSwitchZone", segment.IsSwitchZone.ToString(CultureInfo.InvariantCulture))
                    ));
                }

                polylineElement.Add(segmentsElement);
                polylinesElement.Add(polylineElement);
            }

            root.Add(polylinesElement);

            if (recordedCamMessages.Count > 0)
            {
                XElement camMessagesElement = new XElement("CamMessages");
                foreach (var cam in recordedCamMessages)
                    camMessagesElement.Add(XElement.Parse(cam));
                root.Add(camMessagesElement);
            }

            if (recordedSrvMessages.Count > 0)
            {
                XElement srvMessagesElement = new XElement("SrvMessages");
                foreach (var srv in recordedSrvMessages)
                    srvMessagesElement.Add(XElement.Parse(srv));
                root.Add(srvMessagesElement);
            }

            using (var writer = XmlWriter.Create(filePath, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true }))
            {
                root.Save(writer);
            }
        }

        /// <summary>
        /// Loads map data from an XML file.
        /// </summary>
        /// <param name="filePath">The file path of the XML to load.</param>
        private async void LoadXML(string filePath)
        {
            if (!File.Exists(filePath)) return;

            try
            {
                SilentClearAll();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[LoadXML] Failed clearing existing zones: " + ex.Message);
            }

            isPlaying = false;
            playbackTimer?.Stop();

            XElement root = XElement.Load(filePath);

            // In LoadXML, right after parsing CenterLatitude/CenterLongitude/Zoom and before LoadTiles(...)
            if (root.Attribute("CenterLatitude") != null)
                latitude = double.Parse(root.Attribute("CenterLatitude").Value, CultureInfo.InvariantCulture);
            if (root.Attribute("CenterLongitude") != null)
                longitude = double.Parse(root.Attribute("CenterLongitude").Value, CultureInfo.InvariantCulture);
            if (root.Attribute("Zoom") != null)
                zoom = int.Parse(root.Attribute("Zoom").Value, CultureInfo.InvariantCulture);

            // Sync Mapsettings and TextBoxes to loaded center without firing RefreshMap
            Mapsettings.Latitude = latitude;
            Mapsettings.Longitude = longitude;
            LatitudeBox.TextChanged -= LatitudeBox_TextChanged;
            LongitudeBox.TextChanged -= LongitudeBox_TextChanged;
            LatitudeBox.Text = latitude.ToString("F6", CultureInfo.InvariantCulture);
            LongitudeBox.Text = longitude.ToString("F6", CultureInfo.InvariantCulture);
            LatitudeBox.TextChanged += LatitudeBox_TextChanged;
            LongitudeBox.TextChanged += LongitudeBox_TextChanged;

            // Now load tiles for the loaded center/zoom
            var (centerX, centerY) = LatLonToTileXY(latitude, longitude, zoom);
            await LoadTilesSmoothAsync(centerX - TileCount / 2, centerY - TileCount / 2);
            UpdateLoadingProgress(15);

            _ = EnsureLocalAreaAltitudeAsync(force: true);

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
                        string mode = zoneElem.Attribute("Mode")?.Value ?? "WLC";
                        bool isSwitchZone = mode == "RTV";

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
                                    IsSwitchZone = isSwitchZone,
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
                                Console.WriteLine($"[LoadXML] UI creation for zone failed: {uiEx.Message}");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        // Log parse failure but continue with remaining zones
                        Console.WriteLine($"[LoadXML] Zone parse failed: {ex.Message}");
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

            bool detectedSwitchMode = zonesElement?.Elements("Zone").Any(z =>
            {
                var modeAttr = z.Attribute("Mode")?.Value;
                if (modeAttr != null)
                    return string.Equals(modeAttr, "RTV", StringComparison.OrdinalIgnoreCase);

                // Fallback pro staré soubory bez Mode atributu – Switch zóny mají P/B/V prefix
                var zoneName = z.Attribute("Name")?.Value ?? "";
                return zoneName.StartsWith("P1-", StringComparison.Ordinal)
                    || zoneName.StartsWith("P2-", StringComparison.Ordinal)
                    || zoneName.StartsWith("B", StringComparison.Ordinal)
                    || zoneName.StartsWith("V1-", StringComparison.Ordinal)
                    || zoneName.StartsWith("V2-", StringComparison.Ordinal);
            }) ?? false;

            _suppressModeSwitch = true;
            try
            {
                if (detectedSwitchMode)
                    SwitchRadio.IsChecked = true;
                else
                    ZoneRadio.IsChecked = true;
            }
            finally
            {
                _suppressModeSwitch = false;
            }

            LoadPolylinesFromXml(root);

            UpdateUiEnabledState();

            foreach (var vehicle in activeVehicles.Values)
            {
                CheckActivationZones(vehicle.Position, vehicle.Label);
            }

            Console.WriteLine($"Loaded file: {filePath}");

        }

        private void LoadPolylinesFromXml(XElement root)
        {
            XElement? polylinesElement = root.Element("Polylines");
            if (polylinesElement == null)
                return;

            foreach (var polylineElement in polylinesElement.Elements("Polyline"))
            {
                Guid polylineId = Guid.TryParse(polylineElement.Attribute("Id")?.Value, out var parsedId)
                    ? parsedId
                    : Guid.NewGuid();

                string strokeText = polylineElement.Attribute("Stroke")?.Value ?? "#FFFF0000";
                Brush strokeBrush = TryBrushFromColor(strokeText) ?? Brushes.Red;

                var polyline = new Polyline
                {
                    Stroke = strokeBrush,
                    StrokeThickness = 2,
                    Fill = null,
                    IsHitTestVisible = true
                };

                TileCanvas.Children.Add(polyline);
                Panel.SetZIndex(polyline, 100);

                var geoPoints = new List<(double lat, double lon)>();
                var canvasPoints = new List<Point>();

                XElement? verticesElement = polylineElement.Element("Vertices");
                if (verticesElement != null)
                {
                    foreach (var vertexElement in verticesElement.Elements("Vertex")
                                 .OrderBy(v => int.Parse(v.Attribute("Index")?.Value ?? "0", CultureInfo.InvariantCulture)))
                    {
                        double lat = double.Parse(vertexElement.Attribute("Latitude")?.Value ?? "0", CultureInfo.InvariantCulture);
                        double lon = double.Parse(vertexElement.Attribute("Longitude")?.Value ?? "0", CultureInfo.InvariantCulture);

                        geoPoints.Add((lat, lon));

                        var canvas = ConvertLatLonToCanvasXY(lat, lon);
                        var point = new Point(canvas.x, canvas.y);

                        canvasPoints.Add(point);
                        polyline.Points.Add(point);

                        AddLoadedPolylineVertexDot(polyline, point, canvasPoints.Count - 1);
                    }
                }

                if (canvasPoints.Count < 2)
                {
                    TileCanvas.Children.Remove(polyline);
                    continue;
                }

                _polylineGeoPoints[polyline] = geoPoints;
                _polylineToSegments[polyline] = new List<System.Windows.Shapes.Path>();

                var segmentZones = new List<ActivationZone>();

                XElement? segmentsElement = polylineElement.Element("Segments");
                if (segmentsElement != null)
                {
                    foreach (var segmentElement in segmentsElement.Elements("Segment"))
                    {
                        var segment = new ActivationZone
                        {
                            PolylineId = polylineId,
                            SegmentIndex = int.Parse(segmentElement.Attribute("Index")?.Value ?? "0", CultureInfo.InvariantCulture),
                            Name = segmentElement.Attribute("Name")?.Value ?? string.Empty,
                            Latitude = double.Parse(segmentElement.Attribute("Latitude")?.Value ?? "0", CultureInfo.InvariantCulture),
                            Longitude = double.Parse(segmentElement.Attribute("Longitude")?.Value ?? "0", CultureInfo.InvariantCulture),
                            Width = double.Parse(segmentElement.Attribute("Width")?.Value ?? "50", CultureInfo.InvariantCulture),
                            Height = double.Parse(segmentElement.Attribute("Height")?.Value ?? "0", CultureInfo.InvariantCulture),
                            Azimuth = int.Parse(segmentElement.Attribute("Azimuth")?.Value ?? "0", CultureInfo.InvariantCulture),
                            Color = segmentElement.Attribute("Color")?.Value ?? "#FF0000",
                            MainZone = int.Parse(segmentElement.Attribute("MainZone")?.Value ?? "0", CultureInfo.InvariantCulture),
                            SubZone = int.Parse(segmentElement.Attribute("SubZone")?.Value ?? "0", CultureInfo.InvariantCulture),
                            SegmentType = segmentElement.Attribute("SegmentType")?.Value ?? string.Empty,
                            IsSwitchZone = bool.TryParse(segmentElement.Attribute("IsSwitchZone")?.Value, out var isSwitch) && isSwitch
                        };

                        if (string.IsNullOrWhiteSpace(segment.Name))
                            segment.UpdateName();

                        _polylineRows.Add(segment);

                        if (segment.IsSwitchZone)
                            _switchRows.Add(segment);

                        ActivationZonesCollection.Add(segment);
                        segmentZones.Add(segment);
                    }
                }

                _polylineToSegmentZones[polyline] = segmentZones;

                RebuildPolylineZoneWithVariableWidths(polyline, canvasPoints);
                UpdatePolylineDirectionArrows(polyline, canvasPoints);

                mapRectangles.Add(new MapRectangle(polyline));

                var polylineData = new PolylineData
                {
                    PolylineId = polylineId,
                    CreatedAt = DateTime.Now,
                    ColorHex = strokeText,
                    Vertices = new List<PolylinePointData>()
                };

                for (int i = 0; i < geoPoints.Count; i++)
                {
                    polylineData.Vertices.Add(new PolylinePointData
                    {
                        VertexIndex = i,
                        CanvasPosition = canvasPoints[i],
                        Latitude = geoPoints[i].lat,
                        Longitude = geoPoints[i].lon,
                        Timestamp = DateTime.Now
                    });
                }

                foreach (var segment in segmentZones.OrderBy(s => s.SegmentIndex))
                {
                    polylineData.Segments.Add(new PolylineSegmentData
                    {
                        SegmentIndex = segment.SegmentIndex,
                        LengthMeters = segment.Height,
                        AzimuthDegrees = segment.Azimuth,
                        WidthMeters = segment.Width,
                        SegmentType = segment.SegmentType
                    });
                }

                _drawnPolylines.Add(polylineData);
            }
        }

        private void AddLoadedPolylineVertexDot(Polyline polyline, Point point, int index)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.Black,
                Stroke = Brushes.White,
                StrokeThickness = 1,
                IsHitTestVisible = true,
                Tag = "PolylineVertex"
            };

            Canvas.SetLeft(dot, point.X - 4);
            Canvas.SetTop(dot, point.Y - 4);

            dot.MouseEnter += PolylineVertex_MouseEnter;
            dot.MouseLeave += PolylineVertex_MouseLeave;

            TileCanvas.Children.Add(dot);
            Panel.SetZIndex(dot, 1000000);

            polylineVertexDots.Add(dot);
            _polylineVertexMap[dot] = (polyline, index);
        }

        /// <summary>
        /// Updates the bounds of the specified activation zone.
        /// </summary>
        /// <param name="zone">The activation zone to update.</param>
        public void UpdateActivationZoneBounds(ActivationZone zone)
        {
            var rect = zone.Rectangle;
            double left = Canvas.GetLeft(rect);
            double top = Canvas.GetTop(rect);
            double width = rect.Width;
            double height = rect.Height;
            double angle = zone.Azimuth;

            double centerX = left + width / 2.0;
            double centerY = top + height;

            var corners = new[]
            {
                new Point(left, top),
                new Point(left + width, top),
                new Point(left + width, top + height),
                new Point(left, top + height)
            };

            // Rotace kolem (centerX, centerY)
            double rad = angle * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            Point Rotate(Point p)
            {
                double dx = p.X - centerX;
                double dy = p.Y - centerY;
                double rx = dx * cos - dy * sin + centerX;
                double ry = dx * sin + dy * cos + centerY;
                return new Point(rx, ry);
            }

            var rotated = corners.Select(Rotate).ToArray();

            double minX = rotated.Min(pt => pt.X);
            double maxX = rotated.Max(pt => pt.X);
            double minY = rotated.Min(pt => pt.Y);
            double maxY = rotated.Max(pt => pt.Y);

            zone.Bounds = new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// Calculates a linear interpolation between two values a and b based on a parameter t (0 to 1).
        /// </summary>
        /// <param name="a">The start value.</param>
        /// <param name="b">The end value.</param>
        /// <param name="t">The interpolation parameter (0 to 1).</param>
        /// <returns>The interpolated value.</returns>
        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        /// <summary>
        /// Handles the playback timer tick event.
        /// </summary>
        /// <param name="sender">The sender of the event.</param>
        /// <param name="e">The event arguments.</param>
        private void PlaybackTimer_Tick(object? sender, EventArgs e)
        {
            playbackElapsedTime = TimeSpan.FromTicks((long)((DateTime.Now - playbackStartTime).Ticks * playbackSpeedFactor));
            int idx = GetIndexForTime(playbackElapsedTime);

            if (idx != _playbackIndex)
            {
                _playbackIndex = idx;
                var t = _keyframes[_playbackIndex];
                RedrawPlaybackToTime(t);
                UpdateTimerLabel();
                UpdateReplayTimerLabel();

                SendPlaybackCamForTime(t);
            }

            if (_playbackIndex >= _keyframes.Count - 1)
            {
                if (LoopCheckbox?.IsChecked == true && _keyframes.Count > 0)
                {
                    _playbackIndex = 0;
                    playbackElapsedTime = _keyframes[0];
                    playbackStartTime = DateTime.Now - playbackElapsedTime;

                    RedrawPlaybackToTime(playbackElapsedTime);
                    UpdateTimerLabel();
                    UpdateReplayTimerLabel();

                    // make sure we keep playing
                    _isPlaybackSessionActive = true;
                    isPlaying = true;
                    if (playbackTimer != null && !playbackTimer.IsEnabled)
                        playbackTimer.Start();

                    if (!isReplaySliderDragging)
                        ReplaySlider.Value = 0;
                }
                else
                {
                    playbackTimer.Stop();
                    isPlaying = false;
                    _isPlaybackSessionActive = true;
                    playbackElapsedTime = _keyframes[^1];
                    UpdateTimerLabel();
                    UpdateReplayTimerLabel();

                    // Keep last-frame visuals; clear only via Stop Replay
                    UpdateUiEnabledState();
                }
            }

            if (!isReplaySliderDragging)
                ReplaySlider.Value = _playbackIndex;
        }

        /// <summary>
        /// Starts recording CAM messages.
        /// </summary>
        private void StartRecording()
        {
            ResetStatusUi();

            MessageBox.Show("Recording started.");
            isRecording = true;
            recordingStartTime = DateTime.Now;
            foreach (var pt in points)
                pt.MovementFrames.Clear();

            // UI: Start off, Stop on
            UpdateUiEnabledState();
        }

        /// <summary>
        /// Stops recording CAM messages and prompts the user to save the recording.
        /// </summary>
        private void StopRecording()
        {
            isRecording = false;

            bool hasManual = recordedManualCamMessages.Count > 0;
            if (!hasManual)
            {
                // Fallback: nabídni uložení živého RS485 bufferu (timeshift)
                if (recordedCamMessages.Count > 0)
                {
                    var ask = MessageBox.Show(
                        "Do you want to save the live RS485 CAM buffer?",
                        "Save live buffer",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (ask == MessageBoxResult.Yes)
                    {
                        SaveLiveCamBuffer();
                        _savedRecording = true;
                    }
                }
                else
                {
                    MessageBox.Show("Nothing to save.");
                }

                UpdateUiEnabledState();
                return;
            }

            // původní uložení manuálního záznamu
            var ts = DateTime.Now.ToString("yyyy-MM-dd_HH_mm", CultureInfo.InvariantCulture);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"{ts}.camrec",
                DefaultExt = ".camrec",
                Filter = "CAM Recording (*.camrec)|*.camrec|All files (*.*)|*.*",
                Title = "Save manual CAM recording"
            };

            // Replace inside StopRecording() where manual .camrec is saved
            if (dlg.ShowDialog() == true)
            {
                WriteCamrecWithCenter(dlg.FileName, recordedManualCamMessages);
                MessageBox.Show("CAM recording saved to:\n" + dlg.FileName, "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            recordedManualCamMessages.Clear();
            UpdateUiEnabledState();
        }

        /// <summary>
        /// Saves the live RS485 CAM buffer to a file.
        /// </summary>
        private void SaveLiveCamBuffer()
        {
            if (recordedCamMessages.Count == 0)
            {
                MessageBox.Show("Live buffer is empty.");
                return;
            }

            var ts = DateTime.Now.ToString("yyyy-MM-dd_HH_mm", CultureInfo.InvariantCulture);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"{ts}_live.camrec",
                DefaultExt = ".camrec",
                Filter = "CAM Recording (*.camrec)|*.camrec|All files (*.*)|*.*",
                Title = "Save live RS485 CAM buffer"
            };

            if (dlg.ShowDialog() == true)
            {
                WriteCamrecWithCenter(dlg.FileName, recordedCamMessages);
                MessageBox.Show($"Saved {recordedCamMessages.Count} CAM messages to:\n{dlg.FileName}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Saves the current CAM recording to a file.
        /// </summary>
        /// <param name="filePath">The file path to save the CAM recording.</param>
        private void SaveCamRecording(string filePath)
        {
            WriteCamrecWithCenter(filePath, recordedCamMessages);
        }

        /// <summary>
        /// Updates the position of a map point on the canvas   .
        /// </summary>
        /// <param name="pt">The map point to update.</param>
        /// <param name="pos">The new position of the map point.</param>
        private void UpdatePointPosition(MapPoint pt, Point pos)
        {
            Canvas.SetLeft(pt.Ellipse, pos.X - pt.Ellipse.Width / 2);
            Canvas.SetTop(pt.Ellipse, pos.Y - pt.Ellipse.Height / 2);

            Canvas.SetLeft(pt.Text, pos.X + 5);
            Canvas.SetTop(pt.Text, pos.Y - 10);

            pt.Position = pos;
            RecalculateConnectionLine();
        }

    }
}
