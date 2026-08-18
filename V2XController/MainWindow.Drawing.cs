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
    // Drawing, polylines, mouse interaction, selection, trails and undo/redo
    public partial class MainWindow
    {
        // ===== METHODS FOR DRAWING AND MOUSE EVENTS =====

        /// <summary>
        /// Handles the mouse left button down event on the TileCanvas.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse button event data.</param>
        private void TileCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Block drawing while playback is running
            if (isPlaying)
            {
                e.Handled = true;
                return;
            }

            if (currentDrawingMode == DrawingMode.None && !isSelectionMode)
            {
                ClearZoneTableSelection();
            }

            var pos = e.GetPosition(TileCanvas);
            Console.WriteLine($"[MOUSE] Left button down at ({pos.X:F1}, {pos.Y:F1})");

            if (currentDrawingMode == DrawingMode.Point)
            {
                // OPRAVA 1: Kontrola MMB pro Point mode
                if (e.MiddleButton == MouseButtonState.Pressed || isMiddleMousePanning)
                {
                    return;
                }

                e.Handled = true;
                isDrawing = true;
                startPoint = pos;

                int idx = currentDrawnTramIndex;

                if (drawnTrams[idx] == null)
                {
                    // Vytvoření nové tramvaje
                    var ellipse = new Ellipse
                    {
                        Width = 12,
                        Height = 12,
                        Fill = drawnTramColors[idx],
                        IsHitTestVisible = false
                    };

                    var text = new TextBlock
                    {
                        Text = drawnTramNames[idx],
                        Foreground = drawnTramColors[idx],
                        FontWeight = FontWeights.Bold,
                        FontSize = 12,
                        IsHitTestVisible = false
                    };

                    TileCanvas.Children.Add(ellipse);
                    TileCanvas.Children.Add(text);

                    drawnTrams[idx] = new MapPoint
                    {
                        Position = startPoint,
                        Label = drawnTramIds[idx],
                        Ellipse = ellipse,
                        Text = text,
                        Speed = null,
                        TrailDots = new List<Ellipse>(),
                        IsRecorded = isRecording
                    };

                    // Trail polyline
                    drawnTramTrails[idx] = new Polyline
                    {
                        Stroke = drawnTramColors[idx],
                        StrokeThickness = 2,
                        IsHitTestVisible = false
                    };
                    TileCanvas.Children.Add(drawnTramTrails[idx]);
                }

                drawnTrams[idx].LastUpdate = DateTime.Now;

                var tram = drawnTrams[idx];

                // Trail tečka na předchozí pozici
                if (drawnTramTrailPoints[idx].Count > 0)
                {
                    var prevPos = drawnTramTrailPoints[idx].Last();
                    var dot = new Ellipse
                    {
                        Width = 5,
                        Height = 5,
                        Fill = Brushes.Black,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(dot, prevPos.X - 2.5);
                    Canvas.SetTop(dot, prevPos.Y - 2.5);

                    tram.TrailDots.Add(dot);
                    TileCanvas.Children.Add(dot);

                    while (tram.TrailDots.Count > _maxTrailLength)
                    {
                        TileCanvas.Children.Remove(tram.TrailDots[0]);
                        tram.TrailDots.RemoveAt(0);
                    }
                }

                // Aktualizace pozice tramvaje
                double computedHeading = 0.0;
                if (drawnTramTrailPoints[idx].Count > 0)
                {
                    var prevPt = drawnTramTrailPoints[idx].Last();
                    computedHeading = CalculateAzimuth(prevPt, pos);

                    _lastHeadingLive[drawnTramIds[idx]] = computedHeading;

                    var headingForBox = (computedHeading - 180 + 360) % 360;
                    UpdateOrCreateVehicleBox(drawnTramIds[idx], new Point(pos.X, pos.Y), drawnTramColors[idx], headingForBox);
                }
                else
                {
                    if (_vehicleBoxes.TryGetValue(drawnTramIds[idx], out var leftover))
                    {
                        TileCanvas.Children.Remove(leftover);
                        _vehicleBoxes.Remove(drawnTramIds[idx]);
                    }
                }

                UpdateVehicleCanvasPosition(tram, pos, drawnTramColors[idx], false, tram.Label, 0);

                if (isRecording)
                {
                    tram.MovementFrames.Add(new MovementFrame
                    {
                        Timestamp = DateTime.Now - recordingStartTime,
                        Position = pos
                    });
                }

                drawnTramTrailPoints[idx].Add(pos);
                if (drawnTramTrailPoints[idx].Count > _maxTrailLength + 1)
                    drawnTramTrailPoints[idx].RemoveAt(0);

                if (drawnTramTrails[idx] == null)
                {
                    drawnTramTrails[idx] = new Polyline
                    {
                        Stroke = drawnTramColors[idx],
                        StrokeThickness = 2,
                        IsHitTestVisible = false
                    };
                    TileCanvas.Children.Add(drawnTramTrails[idx]);
                    Panel.SetZIndex(drawnTramTrails[idx], 999);
                }

                drawnTramTrails[idx].Points.Clear();
                foreach (var p in drawnTramTrailPoints[idx])
                    drawnTramTrails[idx].Points.Add(p);

                // updating last tram
                UpdateVehicleCanvasPosition(tram, pos, drawnTramColors[idx], false, tram.Label);

                // updating time of the last activtiy
                tram.LastUpdate = DateTime.Now;

                // sending cam messages
                var latlon = CanvasPixelsToLatLon(pos, latitude, longitude, zoom);
                double latitudeWgs = latlon.Y;
                double longitudeWgs = latlon.X;

                drawnTramLat[idx] = latitudeWgs;
                drawnTramLon[idx] = longitudeWgs;

                drawnTramTrailGeoPoints[idx].Add((latitudeWgs, longitudeWgs));
                if (drawnTramTrailGeoPoints[idx].Count > _maxTrailLength + 1)
                    drawnTramTrailGeoPoints[idx].RemoveAt(0);

                SendPointAsCamMessage(drawnTramIds[idx], latitudeWgs, longitudeWgs, speed: _manualCamSpeedKmh, heading: computedHeading, suppressLocalRender: true);

                try
                {
                    var shortId = drawnTramIds[idx] != null && drawnTramIds[idx].Length > 4
                        ? drawnTramIds[idx][^4..]
                        : drawnTramIds[idx] ?? "-";
                }
                catch { /* ignore logging errors */ }

                return;
            }

            //selection mode
            if (isSelectionMode)
            {
                var clicked = e.OriginalSource as UIElement;
                var element = GetParentElementInCanvas(clicked);

                if (element != null) SelectElement(element);
                else DeselectElement();

                return;
            }

            isDrawing = true;
            startPoint = pos;

            if (currentDrawingMode == DrawingMode.Rectangle)
            {
                // OPRAVA 1: Kontrola MMB pro Rectangle mode
                if (e.MiddleButton == MouseButtonState.Pressed || isMiddleMousePanning)
                {
                    return;
                }

                // Capture target tab at the start of rectangle drawing
                if (rectPhase == RectangleDrawPhase.None)
                {
                    _drawToSwitchZones = IsSwitchMode();

                    int currentZoneCount = ActivationZonesCollection.Count(z => z.IsSwitchZone == _drawToSwitchZones);
                    int maxZones = _drawToSwitchZones ? 35 : 20;

                    if (currentZoneCount >= maxZones)
                    {
                        string modeName = _drawToSwitchZones ? "Switches mode" : "Activation Zones mode";
                        MessageBox.Show(
                            $"Maximum number of zones reached!\n\n" +
                            $"Mode: {modeName}\n" +
                            $"Current zones: {currentZoneCount}\n" +
                            $"Maximum allowed: {maxZones}\n\n" +
                            $"Please delete some zones before adding new ones.",
                            "Zone Limit Reached",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        currentDrawingMode = DrawingMode.None;
                        isDrawing = false;
                        return;
                    }
                }

                if (rectPhase == RectangleDrawPhase.None)
                {
                    if (startPointEllipse != null && ArePointsClose(pos, rectFirstPoint))
                        return;

                    rectFirstPoint = pos;

                    if (startPointEllipse != null)
                        TileCanvas.Children.Remove(startPointEllipse);

                    startPointEllipse = new Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Fill = Brushes.Red,
                        Stroke = Brushes.Black,
                        StrokeThickness = 1
                    };
                    Canvas.SetLeft(startPointEllipse, pos.X - 4);
                    Canvas.SetTop(startPointEllipse, pos.Y - 4);
                    TileCanvas.Children.Add(startPointEllipse);

                    tempHeightLine = new Line
                    {
                        Stroke = Brushes.Red,
                        StrokeThickness = 2,
                        X1 = pos.X,
                        Y1 = pos.Y,
                        X2 = pos.X,
                        Y2 = pos.Y
                    };
                    TileCanvas.Children.Add(tempHeightLine);

                    rectPhase = RectangleDrawPhase.HeightDefinition;
                }
                else if (rectPhase == RectangleDrawPhase.HeightDefinition)
                {
                    rectSecondPoint = pos;

                    tempHeightLine.X2 = pos.X;
                    tempHeightLine.Y2 = pos.Y;

                    if (secondPointEllipse != null)
                        TileCanvas.Children.Remove(secondPointEllipse);

                    secondPointEllipse = new Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Fill = Brushes.Green,
                        Stroke = Brushes.Black,
                        StrokeThickness = 1
                    };
                    Canvas.SetLeft(secondPointEllipse, pos.X - 4);
                    Canvas.SetTop(secondPointEllipse, pos.Y - 4);
                    TileCanvas.Children.Add(secondPointEllipse);

                    tempWidthLine = new Line
                    {
                        Stroke = Brushes.Green,
                        StrokeThickness = 2,
                        X1 = pos.X,
                        Y1 = pos.Y,
                        X2 = pos.X,
                        Y2 = pos.Y
                    };
                    TileCanvas.Children.Add(tempWidthLine);

                    rectPhase = RectangleDrawPhase.WidthDefinition;
                }
                else if (rectPhase == RectangleDrawPhase.WidthDefinition)
                {
                    rectWidthPoint = pos;

                    // compute rectangle geometry in px
                    var axis = rectSecondPoint - rectFirstPoint;
                    if (axis.Length == 0)
                        return;
                    double heightPx = axis.Length;
                    axis.Normalize();
                    var perp = new Vector(-axis.Y, axis.X);
                    double halfWidth = (rectWidthPoint - rectSecondPoint) * perp;
                    double widthPx = Math.Abs(halfWidth) * 2;

                    // center in px
                    var center = new Point(
                        (rectFirstPoint.X + rectSecondPoint.X) / 2,
                        (rectFirstPoint.Y + rectSecondPoint.Y) / 2);

                    // create rect
                    var rect = new Rectangle
                    {
                        Width = widthPx,
                        Height = heightPx,
                        Stroke = _strokeBrush,
                        StrokeThickness = 2,
                        Fill = Brushes.Transparent,
                        IsHitTestVisible = true
                    };

                    Canvas.SetLeft(rect, center.X - widthPx / 2);
                    Canvas.SetTop(rect, center.Y - heightPx / 2);

                    // rotation by azimuth
                    double angle = CalculateAzimuth(rectFirstPoint, rectSecondPoint);
                    var transformGroup = new TransformGroup();
                    transformGroup.Children.Add(new RotateTransform(angle, widthPx / 2, heightPx / 2));
                    rect.RenderTransform = transformGroup;

                    // choose indices based on target collection
                    int nextMain, nextSub;
                    if (_drawToSwitchZones)
                    {
                        var ns = GetNextSwitchZoneIndices();
                        nextMain = ns.main;
                        nextSub = ns.sub;
                    }
                    else
                    {
                        var nz = GetNextZoneIndices();
                        nextMain = nz.main;
                        nextSub = nz.sub;
                    }

                    TileCanvas.Children.Add(rect);
                    mapRectangles.Add(new MapRectangle(rect));
                    isDirty = true;

                    // meters per px
                    double mpp = MetersPerPixel(latitude, zoom);
                    double widthMeters = widthPx * mpp;
                    double heightMeters = heightPx * mpp;

                    // base start lat/lon
                    var latlon = CanvasPixelsToLatLon(rectFirstPoint, latitude, longitude, zoom);

                    var switchCount = ActivationZonesCollection.Count(z => IsSwitchZone(z));
                    var actCount = ActivationZonesCollection.Count(z => !IsSwitchZone(z));

                    var zone = new ActivationZone
                    {
                        Name = _drawToSwitchZones ? $"Switch {switchCount + 1}" : $"Zone {actCount + 1}",
                        Rectangle = rect,
                        Width = Math.Round(widthMeters, 2),
                        Height = Math.Round(heightMeters, 2),
                        LastTramId = "-",
                        Bounds = new Rect(center.X - widthPx / 2, center.Y - heightPx / 2, widthPx, heightPx),
                        StartPoint = rectFirstPoint,
                        Latitude = latlon.Y,
                        Longitude = latlon.X,
                        Azimuth = (int)angle,
                        MainZone = nextMain,
                        SubZone = nextSub,
                        IsSwitchZone = _drawToSwitchZones
                    };

                    Console.WriteLine($"[ZONE] Drawn zone -> Width: {zone.Width}, Height: {zone.Height}, Lat.: {zone.Latitude}, Lon.: {zone.Longitude}, Az.: {zone.Azimuth}");

                    // tag by type and add always to the single collection
                    if (_drawToSwitchZones)
                    {
                        rect.Tag = "SwitchZone";
                    }
                    else
                    {
                        rect.Tag = "DrawnRectangle";
                    }

                    // single table for both types
                    activationZones[rect] = zone;
                    zone.UpdateName();
                    ActivationZonesCollection.Add(zone);

                    zone.PropertyChanged += ActivationZone_PropertyChanged;

                    rect.MouseEnter += Rectangle_MouseEnter;
                    rect.MouseLeave += Rectangle_MouseLeave;
                    rect.MouseLeftButtonDown += Rectangle_MouseLeftButtonDown;

                    var rectToAdd = rect;
                    var zoneToAdd = zone;
                    var mapRectToAdd = new MapRectangle(rect);

                    AddUndoRedo(
                        undo: () =>
                        {
                            // Remove from canvas
                            if (TileCanvas.Children.Contains(rectToAdd))
                                TileCanvas.Children.Remove(rectToAdd);

                            // Remove from dictionaries and collections
                            activationZones.Remove(rectToAdd);
                            ActivationZonesCollection.Remove(zoneToAdd);

                            var mrToRemove = mapRectangles.FirstOrDefault(mr => mr.Shape == rectToAdd);
                            if (mrToRemove != null)
                                mapRectangles.Remove(mrToRemove);

                            // Unsubscribe event
                            zoneToAdd.PropertyChanged -= ActivationZone_PropertyChanged;

                            Console.WriteLine($"[ZONE] Undone zone -> Width: {zoneToAdd.Width}, Height: {zoneToAdd.Height}, Lat.: {zoneToAdd.Latitude}, Lon.: {zoneToAdd.Longitude}, Az.: {zoneToAdd.Azimuth}");

                            isDirty = true;
                        },
                        redo: () =>
                        {
                            // Add back to canvas
                            if (!TileCanvas.Children.Contains(rectToAdd))
                                TileCanvas.Children.Add(rectToAdd);

                            // Add back to dictionaries and collections
                            if (!activationZones.ContainsKey(rectToAdd))
                                activationZones[rectToAdd] = zoneToAdd;

                            if (!ActivationZonesCollection.Contains(zoneToAdd))
                                ActivationZonesCollection.Add(zoneToAdd);

                            if (!mapRectangles.Any(mr => mr.Shape == rectToAdd))
                                mapRectangles.Add(mapRectToAdd);

                            // Resubscribe event
                            zoneToAdd.PropertyChanged += ActivationZone_PropertyChanged;

                            // Restore event handlers
                            rectToAdd.MouseEnter += Rectangle_MouseEnter;
                            rectToAdd.MouseLeave += Rectangle_MouseLeave;
                            rectToAdd.MouseLeftButtonDown += Rectangle_MouseLeftButtonDown;

                            Console.WriteLine($"[ZONE] Redone zone -> Width: {zoneToAdd.Width}, Height: {zoneToAdd.Height}, Lat.: {zoneToAdd.Latitude}, Lon.: {zoneToAdd.Longitude}, Az.: {zoneToAdd.Azimuth}");

                            isDirty = true;
                        }
                    );

                    ClearTempElements();
                    rectPhase = RectangleDrawPhase.None;
                }
            }

            if (currentDrawingMode == DrawingMode.Polyline)
            {
                // OPRAVA 1: Kontrola MMB pro Polyline mode - MUSÍ být PŘED e.Handled = true
                if (e.MiddleButton == MouseButtonState.Pressed || isMiddleMousePanning)
                {
                    return;
                }

                e.Handled = true;
                var position = e.GetPosition(TileCanvas);
                _isDrawingPolyline = true;

                if (currentPolyline == null)
                {
                    var hit = VisualTreeHelper.HitTest(TileCanvas, position);
                    if (hit?.VisualHit is Ellipse clickedDot &&
                        clickedDot.Tag is string dotTag && dotTag == "PolylineVertex" &&
                        _polylineVertexMap.TryGetValue(clickedDot, out var vertexInfo))
                    {
                        var poly = vertexInfo.polyline;
                        var idx = vertexInfo.pointIndex;

                        bool isEndpoint = (idx == 0 || idx == poly.Points.Count - 1);

                        if (isEndpoint)
                        {
                            Console.WriteLine($"[POLYLINE] Continuing from existing polyline (point {idx})");
                            _isDrawingPolyline = true;
                            currentPolyline = poly;

                            Panel.SetZIndex(currentPolyline, 100);

                            polylinePoints.Clear();
                            polylineVertexDots.Clear();
                            _currentPolylineCircles.Clear();
                            _currentPolylineSegments.Clear();

                            // Load existing geo points
                            List<(double lat, double lon)> existingGeoPoints = new List<(double lat, double lon)>();
                            if (_polylineGeoPoints.TryGetValue(poly, out var savedGeo))
                            {
                                existingGeoPoints = new List<(double lat, double lon)>(savedGeo);
                            }

                            if (idx == 0)
                            {
                                // Reverse direction
                                for (int i = poly.Points.Count - 1; i >= 0; i--)
                                {
                                    polylinePoints.Add(poly.Points[i]);

                                    foreach (var kv in _polylineVertexMap)
                                    {
                                        if (kv.Value.polyline == poly && kv.Value.pointIndex == i)
                                        {
                                            polylineVertexDots.Add(kv.Key);
                                            // DON'T collect circles - leave them on canvas
                                            break;
                                        }
                                    }
                                }

                                if (existingGeoPoints.Count > 0)
                                {
                                    existingGeoPoints.Reverse();
                                }

                                poly.Points.Clear();
                                foreach (var pt in polylinePoints)
                                    poly.Points.Add(pt);

                                var oldMap = _polylineVertexMap.Where(kv => kv.Value.polyline == poly).ToList();
                                foreach (var kv in oldMap)
                                    _polylineVertexMap.Remove(kv.Key);

                                for (int i = 0; i < polylineVertexDots.Count; i++)
                                    _polylineVertexMap[polylineVertexDots[i]] = (poly, i);
                            }
                            else
                            {
                                // Continue from end
                                polylinePoints.AddRange(poly.Points);

                                foreach (var kv in _polylineVertexMap.Where(kv => kv.Value.polyline == poly).OrderBy(kv => kv.Value.pointIndex))
                                {
                                    polylineVertexDots.Add(kv.Key);
                                    // DON'T collect circles - leave them on canvas
                                }
                            }

                            // Restore geo points
                            _polylineGeoPoints[poly] = existingGeoPoints;
                            _currentPolylineCircleGeoPoints = new List<(double lat, double lon)>(existingGeoPoints);

                            // REMEMBER committed count
                            _polylineCommittedPointsCount = polylinePoints.Count;

                            // Remove OLD visual Path segments from canvas and rebuild them
                            if (_polylineToSegments.TryGetValue(poly, out var oldVisualSegs))
                            {
                                Console.WriteLine($"[POLYLINE] Removing {oldVisualSegs.Count} old visual segments");
                                foreach (var seg in oldVisualSegs)
                                {
                                    if (TileCanvas.Children.Contains(seg))
                                        TileCanvas.Children.Remove(seg);
                                }
                                oldVisualSegs.Clear();
                            }
                            else
                            {
                                _polylineToSegments[poly] = new List<System.Windows.Shapes.Path>();
                            }

                            if (_polylineToSegmentZones.TryGetValue(poly, out var existingTableSegments))
                            {
                                Console.WriteLine($"[POLYLINE] Found {existingTableSegments.Count} existing table segments for this polyline:");
                                foreach (var seg in existingTableSegments)
                                {
                                    Console.WriteLine($"[POLYLINE]   Segment {seg.SegmentIndex}: Width={seg.Width}m, InCollection={ActivationZonesCollection.Contains(seg)}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[POLYLINE] WARNING: No table segments found in _polylineToSegmentZones!");
                            }

                            // Rebuild visual zone for existing points (this will create new Path segments, but NOT circles since count > 1)
                            double mppx = MetersPerPixel(latitude, zoom);
                            double halfWidthPxx = (_polylineZoneWidthMeters / 2.0) / mppx;

                            if (polylinePoints.Count >= 2)
                            {
                                Console.WriteLine($"[POLYLINE] Rebuilding visual zone with variable widths for {polylinePoints.Count} points");

                                // Use RebuildPolylineZoneWithVariableWidths to respect table segment data
                                RebuildPolylineZoneWithVariableWidths(currentPolyline, polylinePoints);

                                Console.WriteLine($"[POLYLINE] After rebuild: _polylineToSegments has {(_polylineToSegments.ContainsKey(currentPolyline) ? _polylineToSegments[currentPolyline].Count : 0)} segments");

                                // Sync to _currentPolylineSegments
                                if (_polylineToSegments.TryGetValue(currentPolyline, out var rebuiltSegs))
                                {
                                    _currentPolylineSegments.Clear();
                                    _currentPolylineSegments.AddRange(rebuiltSegs);
                                    Console.WriteLine($"[POLYLINE] Synced {rebuiltSegs.Count} segments to _currentPolylineSegments");
                                }
                            }

                            var mr = mapRectangles.FirstOrDefault(m => m.Shape == poly);
                            if (mr != null)
                                mapRectangles.Remove(mr);

                            Console.WriteLine($"[POLYLINE] Resuming with {polylinePoints.Count} existing points (committed: {_polylineCommittedPointsCount})");
                            Console.WriteLine($"[POLYLINE] Loaded {existingGeoPoints.Count} geo points");
                            Console.WriteLine($"[POLYLINE] Old circles LEFT on canvas (not managed by RebuildPolylineZone)");
                            return;
                        }
                    }

                    currentPolyline = new Polyline
                    {
                        Stroke = _strokeBrush,
                        StrokeThickness = 2,
                        Fill = null,
                        IsHitTestVisible = true
                    };
                    TileCanvas.Children.Add(currentPolyline);
                    Panel.SetZIndex(currentPolyline, 100);

                    polylinePoints.Clear();
                    polylineVertexDots.Clear();
                    _currentPolylineCircles.Clear();
                    _currentPolylineSegments.Clear();
                    _polylineCommittedPointsCount = 0;
                    _currentPolylineCircleGeoPoints.Clear();

                    _polylineToSegments[currentPolyline] = new List<System.Windows.Shapes.Path>();

                    // Initialize geo points list
                    if (!_polylineGeoPoints.ContainsKey(currentPolyline))
                        _polylineGeoPoints[currentPolyline] = new List<(double, double)>();
                }

                var (geoLat, geoLon) = ConvertCanvasXYToLatLon(position.X, position.Y, zoom);

                // Add to geo storage
                if (!_polylineGeoPoints.ContainsKey(currentPolyline))
                    _polylineGeoPoints[currentPolyline] = new List<(double, double)>();
                _polylineGeoPoints[currentPolyline].Add((geoLat, geoLon));

                // ALSO store geo for circle
                _currentPolylineCircleGeoPoints.Add((geoLat, geoLon));

                // Convert back to canvas
                var canvasPos = ConvertLatLonToCanvasXY(geoLat, geoLon);
                var canvasPoint = new Point(canvasPos.x, canvasPos.y);

                Console.WriteLine($"[POLYLINE] Point {polylinePoints.Count + 1}: Geo({geoLat:F7}, {geoLon:F7})");

                polylinePoints.Add(canvasPoint);
                currentPolyline.Points.Add(canvasPoint);

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
                Canvas.SetLeft(dot, canvasPoint.X - 4);
                Canvas.SetTop(dot, canvasPoint.Y - 4);

                dot.MouseEnter += PolylineVertex_MouseEnter;
                dot.MouseLeave += PolylineVertex_MouseLeave;

                TileCanvas.Children.Add(dot);
                Panel.SetZIndex(dot, 1000000);
                polylineVertexDots.Add(dot);

                _polylineVertexMap[dot] = (currentPolyline, polylinePoints.Count - 1);

                if (polylinePoints.Count >= 2)
                {
                    if (_polylineCommittedPointsCount > 0 && _polylineToSegmentZones.ContainsKey(currentPolyline))
                    {
                        Console.WriteLine($"[POLYLINE] Rebuilding with VARIABLE widths (committed: {_polylineCommittedPointsCount})");
                        RebuildPolylineZoneWithVariableWidths(currentPolyline, polylinePoints);
                    }
                    else
                    {
                        Console.WriteLine($"[POLYLINE] Rebuilding with UNIFORM width");
                        double mppx = MetersPerPixel(latitude, zoom);
                        double halfWidthPxx = (_polylineZoneWidthMeters / 2.0) / mppx;
                        RebuildPolylineZone(currentPolyline, polylinePoints, halfWidthPxx);
                    }

                    // Sync _currentPolylineSegments
                    if (_polylineToSegments.TryGetValue(currentPolyline, out var currentSegs))
                    {
                        _currentPolylineSegments.Clear();
                        _currentPolylineSegments.AddRange(currentSegs);
                        Console.WriteLine($"[POLYLINE] Synced {currentSegs.Count} visual segments");
                    }
                }

                Console.WriteLine($"[POLYLINE] Total points: {polylinePoints.Count}");
                return;
            }
        }

        /// <summary>
        /// Rebuilds the visual representation of a polyline zone.
        /// </summary>
        /// <param name="polyline">The polyline to rebuild.</param>
        /// <param name="points">The points defining the polyline.</param>
        /// <param name="halfWidthPx">The half width of the polyline zone in pixels.</param>
        private void RebuildPolylineZone(Polyline polyline, List<Point> points, double halfWidthPx)
        {
            if (_polylineVisualGroups.TryGetValue(polyline, out var oldGroups))
            {
                foreach (var group in oldGroups.ToList())
                {
                    if (TileCanvas.Children.Contains(group))
                        TileCanvas.Children.Remove(group);
                }

                oldGroups.Clear();
            }

            if (_polylineToSegmentZones.TryGetValue(polyline, out var zones))
            {
                foreach (var zone in zones)
                {
                    _segmentToVisualPath.Remove(zone);
                    _segmentToCircles.Remove(zone);
                }
            }

            // Remove old zone shapes AND center lines
            if (_polylineToSegments.ContainsKey(polyline))
            {
                foreach (var shape in _polylineToSegments[polyline])
                {
                    if (TileCanvas.Children.Contains(shape))
                        TileCanvas.Children.Remove(shape);
                }
                _polylineToSegments[polyline].Clear();
            }
            else
            {
                _polylineToSegments[polyline] = new List<System.Windows.Shapes.Path>();
            }

            var oldCircles = _currentPolylineCircles.ToList();
            foreach (var circle in oldCircles)
            {
                if (TileCanvas.Children.Contains(circle))
                    TileCanvas.Children.Remove(circle);
            }
            _currentPolylineCircles.Clear();

            if (points.Count < 1) return;

            // Get all segments for this polyline to access individual colors
            List<ActivationZone> segmentZones = null;
            if (_polylineToSegmentZones.TryGetValue(polyline, out segmentZones))
            {
                segmentZones = segmentZones.OrderBy(s => s.SegmentIndex).ToList();
            }

            // Default brush
            SolidColorBrush defaultBrush = _strokeBrush as SolidColorBrush ?? new SolidColorBrush(Colors.Red);

            if (points.Count == 1)
            {
                SolidColorBrush zoneBrush = defaultBrush;
                if (segmentZones != null && segmentZones.Count > 0)
                {
                    var colorBrush = TryBrushFromColor(segmentZones[0].Color);
                    if (colorBrush != null) zoneBrush = colorBrush;
                }

                var circle = new Ellipse
                {
                    Width = halfWidthPx * 2,
                    Height = halfWidthPx * 2,
                    Fill = MakeAlphaBrush(zoneBrush, 60),
                    Stroke = null,
                    IsHitTestVisible = false,
                    Tag = "PolylineZoneCircle"
                };
                Canvas.SetLeft(circle, points[0].X - halfWidthPx);
                Canvas.SetTop(circle, points[0].Y - halfWidthPx);
                TileCanvas.Children.Add(circle);
                Panel.SetZIndex(circle, 50);
                _currentPolylineCircles.Add(circle);
                return;
            }

            // Group consecutive segments with the same color
            var colorGroups = new List<(int startIndex, int endIndex, string color)>();

            if (segmentZones != null && segmentZones.Count > 0)
            {
                string currentColor = segmentZones[0].Color;
                int startIdx = 0;

                for (int i = 1; i < segmentZones.Count; i++)
                {
                    if (segmentZones[i].Color != currentColor)
                    {
                        colorGroups.Add((startIdx, i - 1, currentColor));
                        currentColor = segmentZones[i].Color;
                        startIdx = i;
                    }
                }
                colorGroups.Add((startIdx, segmentZones.Count - 1, currentColor));
            }
            else
            {
                colorGroups.Add((0, points.Count - 2, defaultBrush.Color.ToString()));
            }

            // Draw zone areas
            foreach (var group in colorGroups)
            {
                var pathGeometry = new PathGeometry { FillRule = FillRule.Nonzero };

                for (int i = group.startIndex; i <= group.endIndex && i < points.Count - 1; i++)
                {
                    var p1 = points[i];
                    var p2 = points[i + 1];

                    var dir = p2 - p1;
                    double len = dir.Length;
                    if (len < 0.01) continue;

                    dir.Normalize();
                    var perp = new Vector(-dir.Y, dir.X);

                    var topLeft = p1 + perp * halfWidthPx;
                    var topRight = p2 + perp * halfWidthPx;
                    var bottomRight = p2 - perp * halfWidthPx;
                    var bottomLeft = p1 - perp * halfWidthPx;

                    var figure = new PathFigure { StartPoint = topLeft, IsClosed = true };
                    figure.Segments.Add(new LineSegment(topRight, true));
                    figure.Segments.Add(new LineSegment(bottomRight, true));
                    figure.Segments.Add(new LineSegment(bottomLeft, true));
                    pathGeometry.Figures.Add(figure);
                }

                for (int i = group.startIndex; i <= group.endIndex + 1 && i < points.Count; i++)
                {
                    bool needCap = false;

                    if (i == 0 || i == points.Count - 1)
                    {
                        needCap = true;
                    }
                    else if (i == group.startIndex && i > 0)
                    {
                        needCap = true;
                    }
                    else if (i == group.endIndex + 1 && i < points.Count - 1)
                    {
                        needCap = true;
                    }
                    else if (i > 0 && i < points.Count - 1)
                    {
                        var v1 = points[i] - points[i - 1];
                        var v2 = points[i + 1] - points[i];

                        if (v1.Length > 0.01 && v2.Length > 0.01)
                        {
                            v1.Normalize();
                            v2.Normalize();
                            double dotProduct = v1 * v2;
                            if (dotProduct < Math.Cos(0.1 * Math.PI / 180.0))
                                needCap = true;
                        }
                    }

                    if (needCap)
                    {
                        var capGeometry = new EllipseGeometry(points[i], halfWidthPx, halfWidthPx);
                        pathGeometry = Geometry.Combine(pathGeometry, capGeometry, GeometryCombineMode.Union, null);
                    }
                }

                SolidColorBrush groupBrush = TryBrushFromColor(group.color) ?? defaultBrush;

                var groupPath = new System.Windows.Shapes.Path
                {
                    Data = pathGeometry,
                    Fill = MakeAlphaBrush(groupBrush, 60),
                    Stroke = null,
                    IsHitTestVisible = false,
                    Tag = $"PolylineZone_{group.startIndex}_{group.endIndex}"
                };

                TileCanvas.Children.Add(groupPath);
                Panel.SetZIndex(groupPath, 500);
                if (!_polylineVisualGroups.ContainsKey(polyline))
                    _polylineVisualGroups[polyline] = new List<System.Windows.Shapes.Path>();

                _polylineVisualGroups[polyline].Add(groupPath);
            }

            // Draw center lines with matching colors
            foreach (var group in colorGroups)
            {
                SolidColorBrush groupBrush = TryBrushFromColor(group.color) ?? defaultBrush;

                // Create path for center line
                var lineGeometry = new PathGeometry();
                var lineFigure = new PathFigure
                {
                    StartPoint = points[group.startIndex],
                    IsClosed = false
                };

                for (int i = group.startIndex + 1; i <= group.endIndex + 1 && i < points.Count; i++)
                {
                    lineFigure.Segments.Add(new LineSegment(points[i], true));
                }

                lineGeometry.Figures.Add(lineFigure);

                var centerLinePath = new System.Windows.Shapes.Path
                {
                    Data = lineGeometry,
                    Stroke = groupBrush,
                    StrokeThickness = 2,
                    Fill = null,
                    IsHitTestVisible = false,
                    Tag = $"PolylineCenterLine_{group.startIndex}_{group.endIndex}"
                };

                TileCanvas.Children.Add(centerLinePath);
                Panel.SetZIndex(centerLinePath, 501);
                _polylineToSegments[polyline].Add(centerLinePath);
            }
        }

        /// <summary>
        /// Handles the mouse enter event on a polyline vertex.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse event data.</param>
        private void PolylineVertex_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse dot)
            {
                if (_hoveredVertex == dot)
                    return;

                if (_polylineVertexMap.TryGetValue(dot, out var vertexInfo))
                {
                    var poly = vertexInfo.polyline;
                    var idx = vertexInfo.pointIndex;

                    bool isEndpoint = (idx == 0 || idx == poly.Points.Count - 1);
                    if (!isEndpoint)
                        return;
                }

                _hoveredVertex = dot;

                var currentLeft = Canvas.GetLeft(dot);
                var currentTop = Canvas.GetTop(dot);

                dot.Width = 12;
                dot.Height = 12;
                dot.Fill = new SolidColorBrush(Color.FromRgb(255, 100, 0));
                dot.StrokeThickness = 2;

                Canvas.SetLeft(dot, currentLeft - 2);
                Canvas.SetTop(dot, currentTop - 2);

                TileCanvas.Cursor = Cursors.Hand;
            }
        }

        /// <summary>
        /// Handles the mouse leave event on a polyline vertex.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse event data.</param>
        private void PolylineVertex_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse dot && _hoveredVertex == dot)
            {
                var currentLeft = Canvas.GetLeft(dot);
                var currentTop = Canvas.GetTop(dot);

                dot.Width = 8;
                dot.Height = 8;
                dot.Fill = Brushes.Black;
                dot.StrokeThickness = 1;
                Canvas.SetLeft(dot, currentLeft + 2);
                Canvas.SetTop(dot, currentTop + 2);

                _hoveredVertex = null;
                TileCanvas.Cursor = Cursors.Arrow;
            }
        }

        /// <summary>
        /// Updates the position of a rectangle based on its start point.
        /// </summary>
        /// <param name="zone">The activation zone containing the rectangle.</param>
        private void UpdateRectanglePositionFromStartPoint(ActivationZone zone)
        {
            double width = zone.Rectangle.Width;
            double height = zone.Rectangle.Height;

            double left = zone.StartPoint.X - width / 2.0;
            double top = zone.StartPoint.Y - height;

            Canvas.SetLeft(zone.Rectangle, left);
            Canvas.SetTop(zone.Rectangle, top);

            // rotation around base of the rectangle
            ApplyZoneRotation(zone);

            try { EnsureZoneArrow(zone); } catch { }
        }

        /// <summary>
        /// Handles the mouse move event on the tile canvas.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse event data.</param>
        private void TileCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(TileCanvas);

            // OPRAVA: Reset panning flag pokud MMB není stisknuto
            if (isMiddleMousePanning && e.MiddleButton != MouseButtonState.Pressed)
            {
                Console.WriteLine("[MMB] MouseMove detected MMB released - clearing panning flag");
                isMiddleMousePanning = false;
                isDragging = false;
                TileCanvas.ReleaseMouseCapture();
            }

            if (_hoveredVertex != null && e.LeftButton == MouseButtonState.Released)
            {
                var hit = VisualTreeHelper.HitTest(TileCanvas, pos);

                if (hit?.VisualHit is not Ellipse hitEllipse || !ReferenceEquals(hitEllipse, _hoveredVertex))
                {
                    var currentLeft = Canvas.GetLeft(_hoveredVertex);
                    var currentTop = Canvas.GetTop(_hoveredVertex);

                    _hoveredVertex.Width = 8;
                    _hoveredVertex.Height = 8;
                    _hoveredVertex.Fill = Brushes.Black;
                    _hoveredVertex.StrokeThickness = 1;
                    Canvas.SetLeft(_hoveredVertex, currentLeft + 2);
                    Canvas.SetTop(_hoveredVertex, currentTop + 2);
                    _hoveredVertex = null;
                }
            }

            if (rectPhase == RectangleDrawPhase.HeightDefinition && tempHeightLine != null)
            {
                tempHeightLine.X2 = pos.X;
                tempHeightLine.Y2 = pos.Y;

                double heightPx = (pos - rectFirstPoint).Length;
                double widthPx = 0;

                double mpp = MetersPerPixel(latitude, zoom);
                double widthMeters = widthPx * mpp;
                double heightMeters = heightPx * mpp;

                EnsureDimensionTextBlock();
                dimensionTextBlock.Text = $"{widthMeters:F1} m × {heightMeters:F1} m";
                dimensionTextBlock.Visibility = Visibility.Visible;
                Canvas.SetLeft(dimensionTextBlock, pos.X + 10);
                Canvas.SetTop(dimensionTextBlock, pos.Y + 10);
            }
            else if (rectPhase == RectangleDrawPhase.WidthDefinition && tempWidthLine != null)
            {
                var axis = rectSecondPoint - rectFirstPoint;
                if (axis.Length == 0)
                    return;

                axis.Normalize();
                var perp = new Vector(-axis.Y, axis.X);

                double halfWidth = (pos - rectSecondPoint) * perp;

                Point end = rectSecondPoint + perp * halfWidth;
                tempWidthLine.X2 = end.X;
                tempWidthLine.Y2 = end.Y;

                ShowPreviewRectangle(halfWidth);

                double widthPx = Math.Abs(halfWidth) * 2;
                double heightPx = (rectSecondPoint - rectFirstPoint).Length;

                double mpp = MetersPerPixel(latitude, zoom);
                double widthMeters = widthPx * mpp;
                double heightMeters = heightPx * mpp;

                EnsureDimensionTextBlock();
                dimensionTextBlock.Text = $"{widthMeters:F1} m × {heightMeters:F1} m";
                dimensionTextBlock.Visibility = Visibility.Visible;
                Canvas.SetLeft(dimensionTextBlock, pos.X + 10);
                Canvas.SetTop(dimensionTextBlock, pos.Y + 10);
            }
            else if (currentDrawingMode == DrawingMode.Polyline && _isDrawingPolyline)
            {
                if (e.MiddleButton == MouseButtonState.Pressed || isMiddleMousePanning)
                {
                    CollapsePolylinePreview();
                    if (dimensionTextBlock != null)
                    {
                        dimensionTextBlock.Visibility = Visibility.Collapsed;
                    }
                    return;
                }

                if (currentPolyline != null && polylinePoints.Count > 0)
                {
                    var lastPoint = polylinePoints[^1];

                    if (currentPolyline.Points.Count == polylinePoints.Count + 1)
                    {
                        currentPolyline.Points[^1] = pos;
                    }
                    else
                    {
                        currentPolyline.Points.Add(pos);
                    }

                    double segmentLengthPx = (pos - lastPoint).Length;
                    double mpp = MetersPerPixel(latitude, zoom);
                    double segmentMeters = segmentLengthPx * mpp;

                    EnsureDimensionTextBlock();
                    dimensionTextBlock.Text = $"Segment: {segmentMeters:F1} m";
                    dimensionTextBlock.Visibility = Visibility.Visible;
                    Canvas.SetLeft(dimensionTextBlock, pos.X + 10);
                    Canvas.SetTop(dimensionTextBlock, pos.Y + 10);
                }
                else
                {
                    EnsureDimensionTextBlock();
                    dimensionTextBlock.Text = "Click to place first point";
                    dimensionTextBlock.Visibility = Visibility.Visible;
                    Canvas.SetLeft(dimensionTextBlock, pos.X + 10);
                    Canvas.SetTop(dimensionTextBlock, pos.Y + 10);
                }
            }
        }

        /// <summary>
        /// Handles the mouse left button up event on the tile canvas.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse button event data.</param>
        private void TileCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {

        }

        /// <summary>
        /// Ensures the dimension text block is created and added to the canvas.
        /// </summary>
        private void EnsureDimensionTextBlock()
        {
            if (dimensionTextBlock == null)
            {
                dimensionTextBlock = new TextBlock
                {
                    Foreground = Brushes.Black,
                    Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                    Padding = new Thickness(4),
                    FontWeight = FontWeights.Bold,
                    IsHitTestVisible = false
                };
                TileCanvas.Children.Add(dimensionTextBlock);
                Panel.SetZIndex(dimensionTextBlock, 1000);
            }
        }

        /// <summary>
        /// Shows a preview rectangle on the canvas.
        /// </summary>
        /// <param name="halfWidth">The half width of the rectangle.</param>
        private void ShowPreviewRectangle(double halfWidth)
        {
            var axis = rectSecondPoint - rectFirstPoint;
            if (axis.Length == 0)
                return;

            axis.Normalize();

            var perp = new Vector(-axis.Y, axis.X);

            Point center = new Point(
                (rectFirstPoint.X + rectSecondPoint.X) / 2,
                (rectFirstPoint.Y + rectSecondPoint.Y) / 2);

            Point p1 = new Point(center.X - axis.X * (axis.Length / 2) - perp.X * halfWidth,
                                 center.Y - axis.Y * (axis.Length / 2) - perp.Y * halfWidth);

            Point p2 = new Point(center.X + axis.X * (axis.Length / 2) - perp.X * halfWidth,
                                 center.Y + axis.Y * (axis.Length / 2) - perp.Y * halfWidth);

            Point p3 = new Point(center.X + axis.X * (axis.Length / 2) + perp.X * halfWidth,
                                 center.Y + axis.Y * (axis.Length / 2) + perp.Y * halfWidth);

            Point p4 = new Point(center.X - axis.X * (axis.Length / 2) + perp.X * halfWidth,
                                 center.Y - axis.Y * (axis.Length / 2) + perp.Y * halfWidth);

            if (previewRect != null)
                TileCanvas.Children.Remove(previewRect);

            previewRect = new Polygon
            {
                Stroke = Brushes.Blue,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Points = new PointCollection { p1, p2, p3, p4 }
            };
            TileCanvas.Children.Add(previewRect);
        }

        /// <summary>
        /// Clears temporary elements from the canvas.
        /// </summary>
        private void ClearTempElements()
        {
            if (tempHeightLine != null)
            {
                TileCanvas.Children.Remove(tempHeightLine);
                tempHeightLine = null;
            }
            if (tempWidthLine != null)
            {
                TileCanvas.Children.Remove(tempWidthLine);
                tempWidthLine = null;
            }
            if (startPointEllipse != null)
            {
                TileCanvas.Children.Remove(startPointEllipse);
                startPointEllipse = null;
            }
            if (secondPointEllipse != null)
            {
                TileCanvas.Children.Remove(secondPointEllipse);
                secondPointEllipse = null;
            }
            if (previewRect != null)
            {
                TileCanvas.Children.Remove(previewRect);
                previewRect = null;
            }
            if (dimensionTextBlock != null)
            {
                TileCanvas.Children.Remove(dimensionTextBlock);
                dimensionTextBlock = null;
            }
        }

        /// <summary>
        /// Calculates the azimuth angle between two points.
        /// </summary>
        /// <param name="start">The starting point.</param>
        /// <param name="end">The ending point.</param>
        /// <returns>The azimuth angle in degrees.</returns>
        private int CalculateAzimuth(Point start, Point end)
        {
            double dx = end.X - start.X;
            double dy = start.Y - end.Y;

            double angleRad = Math.Atan2(dx, dy);
            double angleDeg = angleRad * (180.0 / Math.PI);

            if (angleDeg < 0)
                angleDeg += 360.0;

            return (int)Math.Round(angleDeg);
        }

        /// <summary>
        /// Handles the right mouse button down event on the tile canvas.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse button event arguments.</param>
        private void TileCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var position = e.GetPosition(TileCanvas);
            var hit = VisualTreeHelper.HitTest(TileCanvas, position);
            if (hit == null) return;

            var element = hit.VisualHit as UIElement;
            if (element == null) return;

            if (element.IsHitTestVisible == false)
            {
                if (element is not Ellipse || (element as Ellipse).Tag as string != "PolylineVertex")
                    return;
            }

            if ((radiusEllipse != null && ReferenceEquals(element, radiusEllipse)))
                return;

            if (element is Ellipse ellipse && ellipse.Tag is string tag && tag == "PolylineVertex")
            {
                if (_polylineVertexMap.TryGetValue(ellipse, out var info))
                {
                    if (_hoveredVertex == ellipse)
                    {
                        var currentLeft = Canvas.GetLeft(ellipse);
                        var currentTop = Canvas.GetTop(ellipse);

                        ellipse.Width = 8;
                        ellipse.Height = 8;
                        ellipse.Fill = Brushes.Black;
                        ellipse.StrokeThickness = 1;
                        Canvas.SetLeft(ellipse, currentLeft + 2);
                        Canvas.SetTop(ellipse, currentTop + 2);
                        _hoveredVertex = null;
                    }

                    selectedElement = ellipse;
                    mouseOffset = position;
                    TileCanvas.Cursor = Cursors.SizeAll;
                    e.Handled = true;
                    return;
                }
            }

            if (element is Polyline polyline)
            {
                selectedElement = polyline;
                mouseOffset = position;
                TileCanvas.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }

            if (element is Rectangle || element is Ellipse || element is TextBlock || element is Line)
            {
                if (element is Shape sh && Equals(sh.Tag, "Srv"))
                    return;
                if (element is TextBlock tb && Equals(tb.Tag, "Srv"))
                    return;

                selectedElement = element;
                mouseOffset = position;
                TileCanvas.Cursor = Cursors.SizeAll;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Handles the mouse move event for dragging elements on the tile canvas.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse event arguments.</param>
        private void TileCanvas_MouseMoveForDrag(object sender, MouseEventArgs e)
        {
            if (selectedElement == null || e.RightButton != MouseButtonState.Pressed) return;

            if (_isDrawingPolyline && selectedElement is Ellipse selectedDot &&
                selectedDot.Tag is string tag && tag == "PolylineVertex")
                return;

            var currentPos = e.GetPosition(TileCanvas);
            var dx = currentPos.X - mouseOffset.X;
            var dy = currentPos.Y - mouseOffset.Y;

            if (selectedElement is Ellipse vertexDot &&
                vertexDot.Tag is string vTag && vTag == "PolylineVertex" &&
                _polylineVertexMap.TryGetValue(vertexDot, out var vertexInfo))
            {
                var newPos = new Point(currentPos.X, currentPos.Y);
                Canvas.SetLeft(vertexDot, newPos.X - 4);
                Canvas.SetTop(vertexDot, newPos.Y - 4);
                UpdatePolylineSegmentsAroundVertex(vertexInfo.polyline, vertexInfo.pointIndex, newPos);
                mouseOffset = currentPos;
                isDirty = true;
                return;
            }

            if (selectedElement is FrameworkElement element)
            {
                double left = Canvas.GetLeft(element);
                double top = Canvas.GetTop(element);
                Canvas.SetLeft(element, left + dx);
                Canvas.SetTop(element, top + dy);

                if (selectedElement is TextBlock tb)
                {
                    var matchingPoint = points.FirstOrDefault(p => p.Text == tb);
                    if (matchingPoint != null)
                    {
                        var newPos = new Point(left + dx + 5, top + dy + 10);
                        matchingPoint.Position = newPos;
                        RecalculateConnectionLine();
                        if (isRecording)
                        {
                            matchingPoint.MovementFrames.Add(new MovementFrame
                            {
                                Timestamp = DateTime.Now - recordingStartTime,
                                Position = newPos
                            });
                        }
                    }
                }

                mouseOffset = currentPos;
            }

            if (selectedElement is Rectangle rect && activationZones.TryGetValue(rect, out var zone))
            {
                double left = Canvas.GetLeft(rect);
                double top = Canvas.GetTop(rect);
                var baseCenter = new Point(left + rect.Width / 2.0, top + rect.Height);

                isUpdatingActivationZone = true;
                try
                {
                    zone.StartPoint = baseCenter;
                    var lonlat = CanvasPixelsToLatLon(baseCenter, latitude, longitude, zoom);
                    zone.Longitude = lonlat.X;
                    zone.Latitude = lonlat.Y;
                    try { EnsureZoneArrow(zone); } catch { }
                }
                finally
                {
                    isUpdatingActivationZone = false;
                }

                UpdateActivationZoneBounds(zone);
            }
        }

        /// <summary>
        /// Handles the right mouse button up event on the tile canvas.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The mouse button event arguments.</param>
        private void TileCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (selectedElement is Rectangle rect)
            {

            }
            selectedElement = null;
            TileCanvas.Cursor = Cursors.Arrow;
        }

        /// <summary>
        /// Handles the preview key down event for the main window.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The key event arguments.</param>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            //esc stop drawing
            if (e.Key == Key.Escape)
            {
                if (CancelAllDrawing())
                {
                    e.Handled = true;
                    return;
                }

                // Clear table highlight when idle
                ClearZoneTableSelection();

                e.Handled = true;
                return;
            }

            if (e.Key == Key.E || e.Key == Key.Q)
            {
                int delta = (e.Key == Key.E) ? 5 : -5;
                RotateSelectedActivationZone(delta);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && currentDrawingMode == DrawingMode.Polyline && currentPolyline != null)
            {
                FinalizePolyline();

                currentDrawingMode = DrawingMode.Polyline;
                isDrawing = false;

                e.Handled = true;
                return;
            }

            if (e.Key == Key.M && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                WindowState = WindowState.Minimized;
                e.Handled = true;
                return;
            }

            //undo
            if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Undo();
                e.Handled = true;
                return;
            }

            //redo
            if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Redo();
                e.Handled = true;
                return;
            }

            //deletion
            if (e.Key == Key.Delete && selectedElement != null)
            {
                var elementToDelete = selectedElement;

                if (selectedElement is Ellipse selectedDot &&
            selectedDot.Tag is string tag && tag == "PolylineVertex" &&
            _polylineVertexMap.TryGetValue(selectedDot, out var vertexInfo))
                {
                    DeletePolylineVertex(vertexInfo.polyline, vertexInfo.pointIndex);
                    selectedElement = null;
                    isDirty = true;
                    e.Handled = true;
                    return;
                }

                // **NOVÉ: Delete celou polyline (RMB hold na čáře + Del)**
                if (selectedElement is Polyline polyToDelete)
                {
                    DeleteEntirePolyline(polyToDelete);
                    selectedElement = null;
                    isDirty = true;
                    e.Handled = true;
                    return;
                }

                if (selectedElement is Ellipse ellipse)
                {
                    var pointToRemove = points.FirstOrDefault(p => p.Ellipse == ellipse);
                    if (pointToRemove != null)
                    {
                        var textToRemove = pointToRemove.Text;

                        AddUndoRedo(
                            undo: () =>
                            {
                                TileCanvas.Children.Add(ellipse);
                                if (textToRemove != null)
                                    TileCanvas.Children.Add(textToRemove);
                                points.Add(pointToRemove);
                                RecalculateConnectionLine();
                                isDirty = true;
                            },
                            redo: () =>
                            {
                                TileCanvas.Children.Remove(ellipse);
                                if (textToRemove != null)
                                    TileCanvas.Children.Remove(textToRemove);
                                points.Remove(pointToRemove);
                                RecalculateConnectionLine();
                                isDirty = true;
                            }
                        );

                        TileCanvas.Children.Remove(ellipse);
                        TileCanvas.Children.Remove(textToRemove);
                        points.Remove(pointToRemove);
                        RecalculateConnectionLine();
                    }
                }
                else if (selectedElement is TextBlock tb)
                {
                    var pointToRemove = points.FirstOrDefault(p => p.Text == tb);
                    if (pointToRemove != null)
                    {
                        var ellipseToRemove = pointToRemove.Ellipse;

                        AddUndoRedo(
                            undo: () =>
                            {
                                if (ellipseToRemove != null)
                                    TileCanvas.Children.Add(ellipseToRemove);
                                TileCanvas.Children.Add(tb);
                                points.Add(pointToRemove);
                                RecalculateConnectionLine();
                                isDirty = true;
                            },
                            redo: () =>
                            {
                                if (ellipseToRemove != null)
                                    TileCanvas.Children.Remove(ellipseToRemove);
                                TileCanvas.Children.Remove(tb);
                                points.Remove(pointToRemove);
                                RecalculateConnectionLine();
                                isDirty = true;
                            }
                        );

                        TileCanvas.Children.Remove(ellipseToRemove);
                        TileCanvas.Children.Remove(tb);
                        points.Remove(pointToRemove);
                        RecalculateConnectionLine();
                    }
                }
                else if (selectedElement is Rectangle rect)
                {
                    var mapRect = mapRectangles.FirstOrDefault(r => r.Shape == rect);
                    ActivationZone zoneToRemove = null;

                    if (activationZones.TryGetValue(rect, out zoneToRemove))
                    {
                        // Capture all state before removal
                        var zoneSnapshot = zoneToRemove;
                        var rectSnapshot = rect;
                        var mapRectSnapshot = mapRect;

                        AddUndoRedo(
                            undo: () =>
                            {
                                TileCanvas.Children.Add(rectSnapshot);
                                activationZones[rectSnapshot] = zoneSnapshot;
                                if (!ActivationZonesCollection.Contains(zoneSnapshot))
                                    ActivationZonesCollection.Add(zoneSnapshot);
                                if (mapRectSnapshot != null && !mapRectangles.Contains(mapRectSnapshot))
                                    mapRectangles.Add(mapRectSnapshot);
                                isDirty = true;
                            },
                            redo: () =>
                            {
                                TileCanvas.Children.Remove(rectSnapshot);
                                activationZones.Remove(rectSnapshot);
                                ActivationZonesCollection.Remove(zoneSnapshot);
                                if (mapRectSnapshot != null)
                                    mapRectangles.Remove(mapRectSnapshot);
                                isDirty = true;
                            }
                        );

                        // Perform deletion
                        ActivationZonesCollection.Remove(zoneToRemove);
                        activationZones.Remove(rect);
                    }

                    if (mapRect != null)
                        mapRectangles.Remove(mapRect);

                    TileCanvas.Children.Remove(rect);
                }
                else if (selectedElement is Polyline polyline)
                {
                    var mapRect = mapRectangles.FirstOrDefault(r => r.Shape == polyline);

                    var dots = TileCanvas.Children.OfType<Ellipse>()
                        .Where(e =>
                        {
                            double left = Canvas.GetLeft(e);
                            double top = Canvas.GetTop(e);
                            return polyline.Points.Any(p =>
                                Math.Abs(p.X - (left + 4)) < 1 &&
                                Math.Abs(p.Y - (top + 4)) < 1);
                        })
                        .ToList();

                    var polylineSnapshot = polyline;
                    var mapRectSnapshot = mapRect;
                    var dotsSnapshot = dots.ToList();

                    AddUndoRedo(
                        undo: () =>
                        {
                            TileCanvas.Children.Add(polylineSnapshot);
                            foreach (var dot in dotsSnapshot)
                                TileCanvas.Children.Add(dot);
                            if (mapRectSnapshot != null && !mapRectangles.Contains(mapRectSnapshot))
                                mapRectangles.Add(mapRectSnapshot);
                            isDirty = true;
                        },
                        redo: () =>
                        {
                            TileCanvas.Children.Remove(polylineSnapshot);
                            foreach (var dot in dotsSnapshot)
                                TileCanvas.Children.Remove(dot);
                            if (mapRectSnapshot != null)
                                mapRectangles.Remove(mapRectSnapshot);
                            isDirty = true;
                        }
                    );

                    TileCanvas.Children.Remove(polyline);
                    foreach (var dot in dots)
                        TileCanvas.Children.Remove(dot);

                    if (mapRect != null)
                        mapRectangles.Remove(mapRect);

                    Console.WriteLine($"[POLYLINE] Deleted polyline with {polyline.Points.Count} points");
                }

                selectedElement = null;
                isDirty = true;
                e.Handled = true;
            }

            //switching between trams
            if (e.Key == Key.X)
            {
                currentDrawnTramIndex = 0;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.C)
            {
                currentDrawnTramIndex = 1;
                e.Handled = true;
                return;
            }
        }

        /// <summary>
        /// Deletes a vertex from the specified polyline.
        /// </summary>
        /// <param name="polyline">The polyline from which to delete the vertex.</param>
        /// <param name="vertexIndex">The index of the vertex to delete.</param>
        private void DeletePolylineVertex(Polyline polyline, int vertexIndex)
        {
            if (polyline == null || vertexIndex < 0 || vertexIndex >= polyline.Points.Count)
                return;

            Console.WriteLine($"[POLYLINE] Deleting vertex {vertexIndex}/{polyline.Points.Count}");

            // CRITICAL: Suspend property change handling during deletion
            var wasUpdating = isUpdatingActivationZone;
            isUpdatingActivationZone = true;

            try
            {
                // Find and remove vertex dot
                Ellipse dotToRemove = null;
                foreach (var kv in _polylineVertexMap.Where(kv => kv.Value.polyline == polyline && kv.Value.pointIndex == vertexIndex).ToList())
                {
                    dotToRemove = kv.Key;
                    break;
                }

                if (dotToRemove != null)
                {
                    TileCanvas.Children.Remove(dotToRemove);
                    _polylineVertexMap.Remove(dotToRemove);
                    _polylineVertexToCircle.Remove(dotToRemove);

                    if (_hoveredVertex == dotToRemove)
                        _hoveredVertex = null;
                }

                // Remove point from polyline
                polyline.Points.RemoveAt(vertexIndex);

                // Remove geo point
                if (_polylineGeoPoints.TryGetValue(polyline, out var geoList) && vertexIndex < geoList.Count)
                {
                    geoList.RemoveAt(vertexIndex);
                }

                // Reindex remaining vertex mappings
                var toReindex = _polylineVertexMap.Where(kv => kv.Value.polyline == polyline && kv.Value.pointIndex > vertexIndex).ToList();
                foreach (var kv in toReindex)
                {
                    _polylineVertexMap[kv.Key] = (polyline, kv.Value.pointIndex - 1);
                }

                // Remove corresponding segment from table and reindex
                if (_polylineToSegmentZones.TryGetValue(polyline, out var segments))
                {
                    int segmentIndexToRemove = vertexIndex == 0 ? 0 : vertexIndex - 1;

                    if (segmentIndexToRemove < segments.Count)
                    {
                        var segmentToRemove = segments.FirstOrDefault(s => s.SegmentIndex == segmentIndexToRemove);
                        if (segmentToRemove != null)
                        {
                            Console.WriteLine($"[POLYLINE] Removing segment {segmentIndexToRemove} from table");

                            // Unsubscribe from property changed to avoid cascading events
                            segmentToRemove.PropertyChanged -= ActivationZone_PropertyChanged;

                            ActivationZonesCollection.Remove(segmentToRemove);
                            _polylineRows.Remove(segmentToRemove);
                            segments.Remove(segmentToRemove);
                        }
                    }

                    // Reindex all segments after the deleted one
                    var segmentsToReindex = segments.Where(s => s.SegmentIndex > segmentIndexToRemove).OrderBy(s => s.SegmentIndex).ToList();
                    foreach (var seg in segmentsToReindex)
                    {
                        int oldIndex = seg.SegmentIndex;
                        seg.SegmentIndex = oldIndex - 1;

                        // Update segment properties WITHOUT triggering PropertyChanged
                        if (seg.SegmentIndex >= 0 && seg.SegmentIndex < polyline.Points.Count - 1)
                        {
                            var p1Canvas = polyline.Points[seg.SegmentIndex];
                            var p2Canvas = polyline.Points[seg.SegmentIndex + 1];

                            var (lat1, lon1) = ConvertCanvasXYToLatLon(p1Canvas.X, p1Canvas.Y, zoom);
                            var (lat2, lon2) = ConvertCanvasXYToLatLon(p2Canvas.X, p2Canvas.Y, zoom);

                            // Directly update backing fields to avoid PropertyChanged
                            seg.Latitude = (lat1 + lat2) / 2;
                            seg.Longitude = (lon1 + lon2) / 2;
                            seg.Height = HaversineMeters(lat1, lon1, lat2, lon2);
                            seg.Azimuth = CalculateAzimuth(p1Canvas, p2Canvas);

                            // Update name
                            seg.UpdateName();
                        }

                        Console.WriteLine($"[POLYLINE] Reindexed segment {oldIndex} -> {seg.SegmentIndex}");
                    }
                }

                // Check if polyline still valid
                if (polyline.Points.Count <= 1)
                {
                    Console.WriteLine("[POLYLINE] <2 points - deleting entire polyline");
                    DeleteEntirePolyline(polyline);
                    return;
                }

                // Rebuild finalized polyline from current table segment data
                var rebuiltPoints = polyline.Points.ToList();

                if (_polylineToSegmentZones.TryGetValue(polyline, out var remainingSegments) &&
                    remainingSegments.Count > 0)
                {
                    RebuildPolylineZoneWithVariableWidths(polyline, rebuiltPoints);
                }
                else
                {
                    double mpp = MetersPerPixel(latitude, zoom);
                    double halfWidthPx = (_polylineZoneWidthMeters / 2.0) / mpp;

                    RebuildPolylineZone(polyline, rebuiltPoints, halfWidthPx);
                }

                UpdatePolylineDirectionArrows(polyline, rebuiltPoints);

                isDirty = true;
                Console.WriteLine($"[POLYLINE] Vertex deleted. Remaining: {polyline.Points.Count}");
            }
            finally
            {
                // Restore original state
                isUpdatingActivationZone = wasUpdating;
            }
        }

        /// <summary>
        /// Deletes the entire specified polyline and its associated elements.
        /// </summary>
        /// <param name="polyline">The polyline to delete.</param>
        private void DeleteEntirePolyline(Polyline polyline)
        {
            if (polyline == null) return;

            Console.WriteLine("[POLYLINE] Deleting entire polyline");

            TileCanvas.Children.Remove(polyline);

            foreach (var kv in _polylineVertexMap.Where(kv => kv.Value.polyline == polyline).ToList())
            {
                var dot = kv.Key;
                TileCanvas.Children.Remove(dot);

                if (_polylineVertexToCircle.TryGetValue(dot, out var circle))
                {
                    TileCanvas.Children.Remove(circle);
                    _polylineVertexToCircle.Remove(dot);
                }

                _polylineVertexMap.Remove(dot);
            }

            if (_polylineToSegments.TryGetValue(polyline, out var segments))
            {
                foreach (var seg in segments)
                    TileCanvas.Children.Remove(seg);
                _polylineToSegments.Remove(polyline);
            }

            _polylineGeoPoints.Remove(polyline);

            var mr = mapRectangles.FirstOrDefault(m => m.Shape == polyline);
            if (mr != null)
                mapRectangles.Remove(mr);

            Console.WriteLine("[POLYLINE] Polyline deleted completely");
        }

        /// <summary>
        /// Finalizes the current polyline and performs necessary cleanup.
        /// </summary>
        private void FinalizePolyline()
        {
            if (currentPolyline == null || polylinePoints.Count < 2)
            {
                Console.WriteLine("[POLYLINE] Not enough points to finalize (need at least 2)");
                CancelAllDrawing();
                _isDrawingPolyline = false;
                return;
            }

            if (currentPolyline.Points.Count > polylinePoints.Count)
            {
                currentPolyline.Points.RemoveAt(currentPolyline.Points.Count - 1);
            }

            bool wasContinuation = (_polylineCommittedPointsCount > 0);

            Console.WriteLine($"[POLYLINE] ========== FINALIZING POLYLINE ==========");
            Console.WriteLine($"[POLYLINE] Is continuation: {wasContinuation}");
            Console.WriteLine($"[POLYLINE] Committed points: {_polylineCommittedPointsCount}");
            Console.WriteLine($"[POLYLINE] Total vertices: {polylinePoints.Count}");

            var polylineId = Guid.NewGuid();
            var createdAt = DateTime.Now;

            var polylineData = new PolylineData
            {
                PolylineId = polylineId,
                CreatedAt = createdAt,
                ColorHex = ((SolidColorBrush)_strokeBrush).Color.ToString(),
                Vertices = new List<PolylinePointData>()
            };

            for (int i = 0; i < polylinePoints.Count; i++)
            {
                var canvasPoint = polylinePoints[i];
                var (lat, lon) = ConvertCanvasXYToLatLon(canvasPoint.X, canvasPoint.Y, zoom);

                var vertexData = new PolylinePointData
                {
                    VertexIndex = i,
                    CanvasPosition = canvasPoint,
                    Latitude = lat,
                    Longitude = lon,
                    Timestamp = createdAt
                };

                polylineData.Vertices.Add(vertexData);
                Console.WriteLine($"[POLYLINE] Vertex {i + 1}: Canvas({canvasPoint.X:F2}, {canvasPoint.Y:F2})  Geo({lat:F7}, {lon:F7})");
            }

            mapRectangles.Add(new MapRectangle(currentPolyline));
            isDirty = true;

            var addedSegments = new List<ActivationZone>();
            double totalPolylineLength = 0;

            // Segmenty UŽ EXISTUJÍ v _polylineToSegmentZones (vytvořeny v AddPolylineSegmentToTable)
            if (_polylineToSegmentZones.TryGetValue(currentPolyline, out var existingSegments))
            {
                // Jen updatovat PolylineId na finální hodnotu
                foreach (var seg in existingSegments)
                {
                    seg.PolylineId = polylineId;
                    addedSegments.Add(seg);
                    Console.WriteLine($"[FINALIZE] Using existing segment {seg.SegmentIndex}: Main={seg.MainZone}, Sub={seg.SubZone}, Color={seg.Color}");
                }
            }
            else
            {
                Console.WriteLine($"[FINALIZE] WARNING: No segments found in _polylineToSegmentZones!");
            }

            int startSegmentIndex = wasContinuation ? Math.Max(0, _polylineCommittedPointsCount - 1) : 0;

            // *** KLÍČOVÁ OPRAVA: Najít poslední MainZone/SubZone z EXISTUJÍCÍCH segmentů ***
            int currentMainZone = 0;
            int currentSubZone = -1;

            if (wasContinuation && _polylineToSegmentZones.TryGetValue(currentPolyline, out var existingSegs) && existingSegs.Count > 0)
            {
                // Continuation - pokračuj od posledního segmentu této polyline
                var lastExisting = existingSegs.OrderBy(s => s.SegmentIndex).Last();
                currentMainZone = lastExisting.MainZone;
                currentSubZone = lastExisting.SubZone;
                Console.WriteLine($"[FINALIZE] Continuation from existing: Main={currentMainZone}, Sub={currentSubZone}");
            }
            else if (!wasContinuation)
            {
                // Nová polyline - hledej v ActivationZonesCollection (tam jsou finalizované segmenty)
                var lastGlobal = ActivationZonesCollection
                    .Where(z => z.SegmentIndex >= 0) // pouze polyline segmenty
                    .OrderByDescending(z => z.MainZone)
                    .ThenByDescending(z => z.SubZone)
                    .FirstOrDefault();

                if (lastGlobal != null)
                {
                    currentMainZone = lastGlobal.MainZone;
                    currentSubZone = lastGlobal.SubZone;
                    Console.WriteLine($"[FINALIZE] New polyline continuing from ActivationZonesCollection: Main={currentMainZone}, Sub={currentSubZone}");
                }
                else
                {
                    Console.WriteLine($"[FINALIZE] New polyline - starting from 0/0 (no existing segments)");
                }
            }

            for (int i = startSegmentIndex; i < polylinePoints.Count - 1; i++)
            {
                Point p1 = polylinePoints[i];
                Point p2 = polylinePoints[i + 1];

                var (lat1, lon1) = ConvertCanvasXYToLatLon(p1.X, p1.Y, zoom);
                var (lat2, lon2) = ConvertCanvasXYToLatLon(p2.X, p2.Y, zoom);

                double centerLat = (lat1 + lat2) / 2;
                double centerLon = (lon1 + lon2) / 2;

                double lengthMeters = HaversineMeters(lat1, lon1, lat2, lon2);
                totalPolylineLength += lengthMeters;
                int azimuth = CalculateAzimuth(p1, p2);
                bool isrtvmode = IsSwitchMode();

                // *** AUTOMATICKÉ PŘIŘAZENÍ MainZone/SubZone pro NOVÝ segment ***
                // (Ne pro staré segmenty při pokračování!)
                if (!wasContinuation || i >= _polylineCommittedPointsCount - 1)
                {
                    // Increment pro nový segment
                    currentSubZone++;

                    // SubZone přesáhl 4  další MainZone
                    if (currentSubZone > 4 && !isrtvmode)
                    {
                        currentMainZone++;
                        currentSubZone = 0;

                        // Limit: MainZone 0-3
                        if (currentMainZone > 3)
                        {
                            currentMainZone = 3;
                            currentSubZone = 4;
                            Console.WriteLine($"[FINALIZE] WARNING: Max zones (3/4) at segment {i}");
                        }
                    }
                    else
                    {
                        if (isrtvmode && currentSubZone > 6)
                        {
                            currentMainZone++;
                            currentSubZone = 0;
                            // Limit: MainZone 0-4
                            if (currentMainZone > 4)
                            {
                                currentMainZone = 4;
                                currentSubZone = 6;
                                Console.WriteLine($"[FINALIZE] WARNING: Max zones (4/6) in switch mode at segment {i}");
                            }
                        }
                    }
                }

                string color = GetColorForMainZone(currentMainZone, IsSwitchMode());

                string segmentType = "";
                if (polylinePoints.Count >= 3 && isrtvmode)
                {
                    int totalSegments = polylinePoints.Count - 1;
                    if (i == 0)
                        segmentType = "P";
                    else if (i == totalSegments - 1)
                        segmentType = "V";
                    else
                        segmentType = "B";
                }

                var segment = new ActivationZone
                {
                    PolylineId = polylineId,
                    SegmentIndex = i,
                    SegmentType = segmentType,
                    Latitude = centerLat,
                    Longitude = centerLon,
                    Width = _polylineZoneWidthMeters,
                    Height = lengthMeters,
                    Azimuth = azimuth,
                    Color = color, // *** Barva podle MainZone ***
                    MainZone = currentMainZone, // *** Správné přiřazení ***
                    SubZone = currentSubZone,   // *** Správné přiřazení ***
                    IsSwitchZone = false
                };

                segment.UpdateName();

                _polylineRows.Add(segment);
                ActivationZonesCollection.Add(segment);
                addedSegments.Add(segment);

                Console.WriteLine($"[FINALIZE] Segment {i}: Main={currentMainZone}, Sub={currentSubZone}, Color={color}, Type={segmentType}, Len={lengthMeters:F2}m");
            }

            if (!_polylineToSegmentZones.ContainsKey(currentPolyline))
            {
                _polylineToSegmentZones[currentPolyline] = new List<ActivationZone>();
            }

            _polylineToSegmentZones[currentPolyline].AddRange(addedSegments);

            var currentPoints = currentPolyline.Points.ToList();
            Console.WriteLine($"[FINALIZE] Calling live rebuild with {currentPoints.Count} points and {addedSegments.Count} segments");
            RebuildPolylineZoneWithVariableWidths(currentPolyline, currentPoints);
            UpdatePolylineDirectionArrows(currentPolyline, currentPoints);
            polylineData.TotalLengthMeters = totalPolylineLength;
            _drawnPolylines.Add(polylineData);

            var geoPoints = new List<(double lat, double lon)>();
            if (_polylineGeoPoints.TryGetValue(currentPolyline, out var existingGeo))
            {
                geoPoints = new List<(double lat, double lon)>(existingGeo);
            }
            else
            {
                foreach (var pt in polylinePoints)
                {
                    var (lat, lon) = ConvertCanvasXYToLatLon(pt.X, pt.Y, zoom);
                    geoPoints.Add((lat, lon));
                }
                _polylineGeoPoints[currentPolyline] = geoPoints;
            }

            var polylineToAdd = currentPolyline;
            var dotsToAdd = new List<Ellipse>(polylineVertexDots);
            var circlesToAdd = new List<Ellipse>(_currentPolylineCircles);
            var geoPointsToAdd = new List<(double, double)>(geoPoints);
            var tableSegmentsToAdd = new List<ActivationZone>(addedSegments);
            var polylineDataToAdd = polylineData;

            // Snapshot for continuation: save old state to restore on Undo
            List<(double lat, double lon)> oldGeoPoints = null;
            List<ActivationZone> oldTableSegments = null;
            List<Ellipse> oldDots = null;
            List<Ellipse> oldCircles = null;

            if (wasContinuation)
            {
                // Save state BEFORE continuation
                oldGeoPoints = geoPointsToAdd.Take(_polylineCommittedPointsCount).ToList();
                oldDots = dotsToAdd.Take(_polylineCommittedPointsCount).ToList();
                oldCircles = circlesToAdd.Take(_polylineCommittedPointsCount).ToList();

                if (_polylineToSegmentZones.TryGetValue(polylineToAdd, out var allSegs))
                {
                    oldTableSegments = allSegs.Take(Math.Max(0, _polylineCommittedPointsCount - 1)).ToList();
                }
            }

            var allDotsToAdd = new List<Ellipse>(polylineVertexDots);
            var allCirclesToAdd = new List<Ellipse>(_currentPolylineCircles);
            var allGeoPointsToAdd = new List<(double, double)>(geoPoints);
            var newDotsToAdd = wasContinuation ? allDotsToAdd.Skip(_polylineCommittedPointsCount).ToList() : allDotsToAdd;
            var newCirclesToAdd = wasContinuation ? allCirclesToAdd.Skip(_polylineCommittedPointsCount).ToList() : allCirclesToAdd;

            var vertexMapSnapshot = new Dictionary<Ellipse, (Polyline, int)>();
            foreach (var kv in _polylineVertexMap)
            {
                if (kv.Value.polyline == currentPolyline)
                    vertexMapSnapshot[kv.Key] = kv.Value;
            }

            AddUndoRedo(
                undo: () =>
                {
                    if (wasContinuation)
                    {
                        // RESTORE to pre-continuation state
                        Console.WriteLine($"[UNDO] Reverting continuation to {oldGeoPoints.Count} points");

                        // Remove NEW dots and circles only
                        foreach (var dot in newDotsToAdd)
                        {
                            if (TileCanvas.Children.Contains(dot))
                                TileCanvas.Children.Remove(dot);
                            _polylineVertexMap.Remove(dot);

                            if (_polylineVertexToCircle.TryGetValue(dot, out var circle))
                            {
                                _polylineVertexToCircle.Remove(dot);
                            }
                        }

                        foreach (var circle in newCirclesToAdd)
                        {
                            if (TileCanvas.Children.Contains(circle))
                                TileCanvas.Children.Remove(circle);
                        }

                        // Remove NEW table segments
                        foreach (var tableSeg in tableSegmentsToAdd)
                        {
                            ActivationZonesCollection.Remove(tableSeg);
                            _polylineRows.Remove(tableSeg);
                        }

                        // Restore OLD table segments
                        if (oldTableSegments != null)
                        {
                            if (!_polylineToSegmentZones.ContainsKey(polylineToAdd))
                                _polylineToSegmentZones[polylineToAdd] = new List<ActivationZone>();

                            _polylineToSegmentZones[polylineToAdd].Clear();
                            _polylineToSegmentZones[polylineToAdd].AddRange(oldTableSegments);

                            // Re-add old segments to table
                            foreach (var oldSeg in oldTableSegments)
                            {
                                if (!ActivationZonesCollection.Contains(oldSeg))
                                {
                                    _polylineRows.Add(oldSeg);
                                    ActivationZonesCollection.Add(oldSeg);
                                }
                            }
                        }

                        // Restore OLD geo points
                        _polylineGeoPoints[polylineToAdd] = new List<(double lat, double lon)>(oldGeoPoints);

                        // Rebuild polyline visual from OLD geo
                        polylineToAdd.Points.Clear();
                        foreach (var (lat, lon) in oldGeoPoints)
                        {
                            var canvasPos = ConvertLatLonToCanvasXY(lat, lon);
                            polylineToAdd.Points.Add(new Point(canvasPos.x, canvasPos.y));
                        }

                        // Rebuild OLD vertex dot positions
                        for (int i = 0; i < oldDots.Count && i < oldGeoPoints.Count; i++)
                        {
                            var (lat, lon) = oldGeoPoints[i];
                            var canvasPos = ConvertLatLonToCanvasXY(lat, lon);
                            Canvas.SetLeft(oldDots[i], canvasPos.x - 4);
                            Canvas.SetTop(oldDots[i], canvasPos.y - 4);

                            // Restore to vertex map
                            _polylineVertexMap[oldDots[i]] = (polylineToAdd, i);
                        }

                        // Rebuild OLD circle positions
                        double mpp = MetersPerPixel(latitude, zoom);
                        double halfWidthPx = (_polylineZoneWidthMeters / 2.0) / mpp;

                        for (int i = 0; i < oldCircles.Count && i < oldGeoPoints.Count; i++)
                        {
                            var (lat, lon) = oldGeoPoints[i];
                            var canvasPos = ConvertLatLonToCanvasXY(lat, lon);
                            Canvas.SetLeft(oldCircles[i], canvasPos.x - halfWidthPx);
                            Canvas.SetTop(oldCircles[i], canvasPos.y - halfWidthPx);

                            // Restore mapping
                            if (i < oldDots.Count)
                                _polylineVertexToCircle[oldDots[i]] = oldCircles[i];
                        }

                        // Rebuild visual zones from OLD segments
                        if (oldTableSegments != null && oldTableSegments.Count > 0)
                        {
                            var oldPoints = polylineToAdd.Points.ToList();
                            RebuildPolylineZoneWithVariableWidths(polylineToAdd, oldPoints);
                            UpdatePolylineDirectionArrows(polylineToAdd, oldPoints);
                        }

                        // Keep in finalized state
                        if (!mapRectangles.Any(m => m.Shape == polylineToAdd))
                            mapRectangles.Add(new MapRectangle(polylineToAdd));
                    }
                    else
                    {
                        // DELETE entire polyline
                        Console.WriteLine("[UNDO] Deleting entire new polyline");

                        if (TileCanvas.Children.Contains(polylineToAdd))
                            TileCanvas.Children.Remove(polylineToAdd);

                        foreach (var dot in allDotsToAdd)
                        {
                            if (TileCanvas.Children.Contains(dot))
                                TileCanvas.Children.Remove(dot);
                            _polylineVertexMap.Remove(dot);
                            _polylineVertexToCircle.Remove(dot);
                        }

                        foreach (var circle in allCirclesToAdd)
                        {
                            if (TileCanvas.Children.Contains(circle))
                                TileCanvas.Children.Remove(circle);
                        }

                        if (_polylineToSegments.TryGetValue(polylineToAdd, out var visualSegs))
                        {
                            foreach (var seg in visualSegs)
                            {
                                if (TileCanvas.Children.Contains(seg))
                                    TileCanvas.Children.Remove(seg);
                            }
                            _polylineToSegments.Remove(polylineToAdd);
                        }

                        foreach (var tableSeg in tableSegmentsToAdd)
                        {
                            ActivationZonesCollection.Remove(tableSeg);
                            _polylineRows.Remove(tableSeg);
                        }

                        _polylineToSegmentZones.Remove(polylineToAdd);
                        _polylineGeoPoints.Remove(polylineToAdd);
                        _drawnPolylines.Remove(polylineDataToAdd);

                        var mr = mapRectangles.FirstOrDefault(m => m.Shape == polylineToAdd);
                        if (mr != null) mapRectangles.Remove(mr);
                    }

                    isDirty = true;
                },
                redo: () =>
                {
                    Console.WriteLine($"[REDO] Restoring polyline (continuation: {wasContinuation})");

                    if (!TileCanvas.Children.Contains(polylineToAdd))
                        TileCanvas.Children.Add(polylineToAdd);

                    // Add ALL dots back
                    foreach (var dot in allDotsToAdd)
                    {
                        if (!TileCanvas.Children.Contains(dot))
                            TileCanvas.Children.Add(dot);
                    }

                    // Restore ALL vertex mappings
                    foreach (var kv in vertexMapSnapshot)
                        _polylineVertexMap[kv.Key] = kv.Value;

                    // Restore vertex-to-circle mappings
                    for (int i = 0; i < allDotsToAdd.Count && i < allCirclesToAdd.Count; i++)
                    {
                        _polylineVertexToCircle[allDotsToAdd[i]] = allCirclesToAdd[i];
                    }

                    // Add ALL circles back
                    foreach (var circle in allCirclesToAdd)
                    {
                        if (!TileCanvas.Children.Contains(circle))
                            TileCanvas.Children.Add(circle);
                    }

                    // Add table segments
                    if (wasContinuation && oldTableSegments != null)
                    {
                        // Re-add OLD segments first
                        foreach (var oldSeg in oldTableSegments)
                        {
                            if (!ActivationZonesCollection.Contains(oldSeg))
                            {
                                _polylineRows.Add(oldSeg);
                                ActivationZonesCollection.Add(oldSeg);
                            }
                        }
                    }

                    // Then add NEW segments
                    foreach (var tableSeg in tableSegmentsToAdd)
                    {
                        if (!ActivationZonesCollection.Contains(tableSeg))
                        {
                            _polylineRows.Add(tableSeg);
                            ActivationZonesCollection.Add(tableSeg);
                        }
                    }

                    // Restore segment zones mapping
                    if (!_polylineToSegmentZones.ContainsKey(polylineToAdd))
                    {
                        _polylineToSegmentZones[polylineToAdd] = new List<ActivationZone>();
                    }

                    _polylineToSegmentZones[polylineToAdd].Clear();
                    if (wasContinuation && oldTableSegments != null)
                        _polylineToSegmentZones[polylineToAdd].AddRange(oldTableSegments);

                    // Restore geo points
                    _polylineGeoPoints[polylineToAdd] = new List<(double lat, double lon)>(allGeoPointsToAdd);

                    // Rebuild polyline from geo
                    polylineToAdd.Points.Clear();
                    foreach (var (lat, lon) in allGeoPointsToAdd)
                    {
                        var canvasPos = ConvertLatLonToCanvasXY(lat, lon);
                        polylineToAdd.Points.Add(new Point(canvasPos.x, canvasPos.y));
                    }

                    // Rebuild ALL vertex positions from geo
                    for (int i = 0; i < allDotsToAdd.Count && i < allGeoPointsToAdd.Count; i++)
                    {
                        var (lat, lon) = allGeoPointsToAdd[i];
                        var canvasPos = ConvertLatLonToCanvasXY(lat, lon);
                        Canvas.SetLeft(allDotsToAdd[i], canvasPos.x - 4);
                        Canvas.SetTop(allDotsToAdd[i], canvasPos.y - 4);
                    }

                    // Rebuild ALL circle positions from geo
                    double mpp = MetersPerPixel(latitude, zoom);
                    double halfWidthPx = (_polylineZoneWidthMeters / 2.0) / mpp;

                    for (int i = 0; i < allCirclesToAdd.Count && i < allGeoPointsToAdd.Count; i++)
                    {
                        var (lat, lon) = allGeoPointsToAdd[i];
                        var canvasPos = ConvertLatLonToCanvasXY(lat, lon);
                        Canvas.SetLeft(allCirclesToAdd[i], canvasPos.x - halfWidthPx);
                        Canvas.SetTop(allCirclesToAdd[i], canvasPos.y - halfWidthPx);
                    }

                    // Rebuild visual zones from ALL table segments
                    var currentPoints = polylineToAdd.Points.ToList();
                    RebuildPolylineZoneWithVariableWidths(polylineToAdd, currentPoints);
                    UpdatePolylineDirectionArrows(polylineToAdd, currentPoints);
                    // Na konci redo akce v FinalizePolyline (kolem řádku 193000):

                    if (!_drawnPolylines.Contains(polylineDataToAdd))
                        _drawnPolylines.Add(polylineDataToAdd);

                    if (!mapRectangles.Any(m => m.Shape == polylineToAdd))
                        mapRectangles.Add(new MapRectangle(polylineToAdd));

                    // OPRAVA Z-indexů
                    Panel.SetZIndex(polylineToAdd, 1000); // polyline čára
                    foreach (var d in allDotsToAdd) Panel.SetZIndex(d, 1000000); // dots NAD čarou
                    foreach (var c in allCirclesToAdd) Panel.SetZIndex(c, 500); // circles POD čarou ale NAD tilesy

                    isDirty = true;
                }
            );

            currentPolyline = null;
            polylinePoints.Clear();
            polylineVertexDots.Clear();
            _currentPolylineCircles.Clear();
            _currentPolylineSegments.Clear();
            isDrawing = false;
            _isDrawingPolyline = false;
            _currentPolylineCircleGeoPoints.Clear();
            _polylineCommittedPointsCount = 0;

            if (dimensionTextBlock != null)
            {
                TileCanvas.Children.Remove(dimensionTextBlock);
                dimensionTextBlock = null;
            }

            Console.WriteLine("[POLYLINE] ==========================================");
            Console.WriteLine("[POLYLINE] Finalized and ready for next polyline");
        }

        /// <summary>
        /// Updates the segments of a polyline around a specific vertex.
        /// </summary>
        /// <param name="polyline">The polyline containing the vertex.</param>
        /// <param name="vertexIndex">The index of the vertex to update.</param>
        /// <param name="newPosition">The new positiFon of the vertex.</param>
        private void UpdatePolylineSegmentsAroundVertex(Polyline polyline, int vertexIndex, Point newPosition)
        {
            if (polyline == null || vertexIndex < 0 || vertexIndex >= polyline.Points.Count)
                return;

            // Temporarily disable PropertyChanged to avoid cascade
            var wasUpdating = isUpdatingActivationZone;
            isUpdatingActivationZone = true;

            try
            {
                polyline.Points[vertexIndex] = newPosition;

                // Update geo points
                var (geoLat, geoLon) = ConvertCanvasXYToLatLon(newPosition.X, newPosition.Y, zoom);

                if (_polylineGeoPoints.TryGetValue(polyline, out var geoList) && vertexIndex < geoList.Count)
                {
                    geoList[vertexIndex] = (geoLat, geoLon);
                }

                // Update segments in table that are affected by this vertex movement
                if (_polylineToSegmentZones.TryGetValue(polyline, out var segments))
                {
                    // Update segment BEFORE this vertex (if exists)
                    if (vertexIndex > 0)
                    {
                        int prevSegmentIndex = vertexIndex - 1;
                        var prevSegment = segments.FirstOrDefault(s => s.SegmentIndex == prevSegmentIndex);

                        if (prevSegment != null && geoList != null && prevSegmentIndex < geoList.Count - 1)
                        {
                            var (lat1, lon1) = geoList[prevSegmentIndex];
                            var (lat2, lon2) = geoList[prevSegmentIndex + 1];

                            // Update segment properties (keep Width unchanged - it's per-segment now)
                            prevSegment.Latitude = (lat1 + lat2) / 2;
                            prevSegment.Longitude = (lon1 + lon2) / 2;
                            prevSegment.Height = HaversineMeters(lat1, lon1, lat2, lon2);
                            prevSegment.Azimuth = CalculateAzimuth(
                                polyline.Points[prevSegmentIndex],
                                polyline.Points[prevSegmentIndex + 1]
                            );

                            Console.WriteLine($"[DRAG] Segment {prevSegmentIndex}: Len={prevSegment.Height:F2}m, Az={prevSegment.Azimuth}, Width={prevSegment.Width:F2}m");
                        }
                    }

                    // Update segment AFTER this vertex (if exists)
                    if (vertexIndex < polyline.Points.Count - 1)
                    {
                        int nextSegmentIndex = vertexIndex;
                        var nextSegment = segments.FirstOrDefault(s => s.SegmentIndex == nextSegmentIndex);

                        if (nextSegment != null && geoList != null && nextSegmentIndex < geoList.Count - 1)
                        {
                            var (lat1, lon1) = geoList[nextSegmentIndex];
                            var (lat2, lon2) = geoList[nextSegmentIndex + 1];

                            // Update segment properties (keep Width unchanged)
                            nextSegment.Latitude = (lat1 + lat2) / 2;
                            nextSegment.Longitude = (lon1 + lon2) / 2;
                            nextSegment.Height = HaversineMeters(lat1, lon1, lat2, lon2);
                            nextSegment.Azimuth = CalculateAzimuth(
                                polyline.Points[nextSegmentIndex],
                                polyline.Points[nextSegmentIndex + 1]
                            );

                            Console.WriteLine($"[DRAG] Segment {nextSegmentIndex}: Len={nextSegment.Height:F2}m, Az={nextSegment.Azimuth}, Width={nextSegment.Width:F2}m");
                        }
                    }

                    // Also update PolylineData in _drawnPolylines list
                    var polylineData = _drawnPolylines.FirstOrDefault(pd =>
                        segments.Any(s => s.PolylineId == pd.PolylineId));

                    if (polylineData != null)
                    {
                        // Update vertex data
                        if (vertexIndex < polylineData.Vertices.Count)
                        {
                            polylineData.Vertices[vertexIndex].CanvasPosition = newPosition;
                            polylineData.Vertices[vertexIndex].Latitude = geoLat;
                            polylineData.Vertices[vertexIndex].Longitude = geoLon;
                        }

                        // Update affected segment data
                        if (vertexIndex > 0 && vertexIndex - 1 < polylineData.Segments.Count)
                        {
                            var prevSegData = polylineData.Segments[vertexIndex - 1];
                            var prevSeg = segments.FirstOrDefault(s => s.SegmentIndex == vertexIndex - 1);
                            if (prevSeg != null)
                            {
                                prevSegData.LengthMeters = prevSeg.Height;
                                prevSegData.AzimuthDegrees = prevSeg.Azimuth;
                                // Width stays as is - per segment
                            }
                        }

                        if (vertexIndex < polylineData.Segments.Count)
                        {
                            var nextSegData = polylineData.Segments[vertexIndex];
                            var nextSeg = segments.FirstOrDefault(s => s.SegmentIndex == vertexIndex);
                            if (nextSeg != null)
                            {
                                nextSegData.LengthMeters = nextSeg.Height;
                                nextSegData.AzimuthDegrees = nextSeg.Azimuth;
                                // Width stays as is - per segment
                            }
                        }

                        // Recalculate total length
                        polylineData.TotalLengthMeters = polylineData.Segments.Sum(s => s.LengthMeters);
                    }
                }

                // Rebuild with per-segment widths
                RebuildPolylineZoneWithVariableWidths(polyline, polyline.Points.ToList());

                // Update vertex positions (dots and circles)
                UpdatePolylineVertexPositions(polyline, polyline.Points.ToList());
                UpdatePolylineDirectionArrows(polyline, polyline.Points.ToList());
            }
            finally
            {
                isUpdatingActivationZone = wasUpdating;
            }
        }

        /// <summary>
        /// Rebuilds the polyline zone with variable widths.
        /// </summary>
        /// <param name="polyline">The polyline to rebuild.</param>
        /// <param name="points">The list of points defining the polyline.</param>
        private void RebuildPolylineZoneWithVariableWidths(Polyline polyline, List<Point> points)
        {
            // Remove old HIT segments
            if (_polylineToSegments.TryGetValue(polyline, out var oldHitSegments))
            {
                foreach (var shape in oldHitSegments.ToList())
                    TileCanvas.Children.Remove(shape);

                oldHitSegments.Clear();
            }
            else
            {
                _polylineToSegments[polyline] = new List<System.Windows.Shapes.Path>();
            }

            // Remove old VISUAL groups
            if (_polylineVisualGroups.TryGetValue(polyline, out var oldVisualGroups))
            {
                foreach (var shape in oldVisualGroups.ToList())
                    TileCanvas.Children.Remove(shape);

                oldVisualGroups.Clear();
            }
            else
            {
                _polylineVisualGroups[polyline] = new List<System.Windows.Shapes.Path>();
            }

            // Remove old mappings for this polyline
            if (_polylineToSegmentZones.TryGetValue(polyline, out var oldZones))
            {
                foreach (var zone in oldZones)
                {
                    _segmentToVisualPath.Remove(zone);
                    _segmentToCircles.Remove(zone);
                }
            }

            if (points.Count < 2)
                return;

            double mpp = MetersPerPixel(latitude, zoom);
            SolidColorBrush defaultBrush = _strokeBrush as SolidColorBrush ?? new SolidColorBrush(Colors.Red);

            // Get segments
            List<ActivationZone> segmentZones = null;
            if (_polylineToSegmentZones.TryGetValue(polyline, out segmentZones))
            {
                segmentZones = segmentZones.OrderBy(s => s.SegmentIndex).ToList();
            }

            // Helper: Převod barvy na Color objekt
            Color ParseColor(string colorStr)
            {
                var brush = TryBrushFromColor(colorStr);
                return brush?.Color ?? defaultBrush.Color;
            }

            // Vytvoříme virtuální segmenty s Color objekty místo stringů
            var virtualSegments = new List<(int index, double widthMeters, Color color, string colorStr)>();

            for (int i = 0; i < points.Count - 1; i++)
            {
                ActivationZone tableSegment = segmentZones?.FirstOrDefault(s => s.SegmentIndex == i);

                if (tableSegment != null)
                {
                    Color color = ParseColor(tableSegment.Color);
                    virtualSegments.Add((i, tableSegment.Width, color, tableSegment.Color));
                }
                else
                {
                    // Nový segment - použijeme default
                    virtualSegments.Add((i, _polylineZoneWidthMeters, defaultBrush.Color, defaultBrush.Color.ToString()));
                }
            }

            Console.WriteLine($"[REBUILD VAR] points={points.Count}, virtualSegs={virtualSegments.Count}");
            foreach (var seg in virtualSegments)
            {
                Console.WriteLine($"[REBUILD VAR]   Seg {seg.index}: colorStr={seg.colorStr}, RGB=({seg.color.R},{seg.color.G},{seg.color.B}), width={seg.widthMeters:F2}m");
            }

            // Seskupíme podle RGB COLOR A šířky - POUZE SOUSEDNÍ!
            var groups = new List<(int startIndex, int endIndex, Color color, string colorStr, double widthMeters)>();

            if (virtualSegments.Count > 0)
            {
                Color currentColor = virtualSegments[0].color;
                string currentColorStr = virtualSegments[0].colorStr;
                double currentWidth = virtualSegments[0].widthMeters;
                int startIdx = 0;

                for (int i = 1; i < virtualSegments.Count; i++)
                {
                    // *** KLÍČOVÁ OPRAVA: Porovnání RGB hodnot místo stringů ***
                    bool colorChanged = (virtualSegments[i].color.R != currentColor.R ||
                                       virtualSegments[i].color.G != currentColor.G ||
                                       virtualSegments[i].color.B != currentColor.B);
                    bool widthChanged = Math.Abs(virtualSegments[i].widthMeters - currentWidth) > 0.01;

                    if (colorChanged || widthChanged)
                    {
                        // Konec aktuální skupiny
                        groups.Add((startIdx, i - 1, currentColor, currentColorStr, currentWidth));
                        Console.WriteLine($"[REBUILD VAR]  Merged group: seg {startIdx}-{i - 1} ({i - startIdx} segments), RGB=({currentColor.R},{currentColor.G},{currentColor.B}), width={currentWidth:F2}m");

                        // Začátek nové skupiny
                        currentColor = virtualSegments[i].color;
                        currentColorStr = virtualSegments[i].colorStr;
                        currentWidth = virtualSegments[i].widthMeters;
                        startIdx = i;
                    }
                    else
                    {
                        Console.WriteLine($"[REBUILD VAR]    Seg {i} merged (same color+width)");
                    }
                }

                // Poslední skupina
                groups.Add((startIdx, virtualSegments.Count - 1, currentColor, currentColorStr, currentWidth));
                Console.WriteLine($"[REBUILD VAR]  Merged group: seg {startIdx}-{virtualSegments.Count - 1} ({virtualSegments.Count - startIdx} segments), RGB=({currentColor.R},{currentColor.G},{currentColor.B}), width={currentWidth:F2}m");
            }

            Console.WriteLine($"[REBUILD VAR] Total: {virtualSegments.Count} segments  {groups.Count} merged groups");

            // Vykreslíme zóny
            foreach (var group in groups)
            {
                double halfWidthPx = (group.widthMeters / 2.0) / mpp;
                var pathGeometry = new PathGeometry { FillRule = FillRule.Nonzero };

                for (int i = group.startIndex; i <= group.endIndex && i < points.Count - 1; i++)
                {
                    var p1 = points[i];
                    var p2 = points[i + 1];

                    var dir = p2 - p1;
                    double len = dir.Length;
                    if (len < 0.01) continue;

                    dir.Normalize();
                    var perp = new Vector(-dir.Y, dir.X);

                    var topLeft = p1 + perp * halfWidthPx;
                    var topRight = p2 + perp * halfWidthPx;
                    var bottomRight = p2 - perp * halfWidthPx;
                    var bottomLeft = p1 - perp * halfWidthPx;

                    var figure = new PathFigure { StartPoint = topLeft, IsClosed = true };
                    figure.Segments.Add(new LineSegment(topRight, true));
                    figure.Segments.Add(new LineSegment(bottomRight, true));
                    figure.Segments.Add(new LineSegment(bottomLeft, true));
                    pathGeometry.Figures.Add(figure);
                }

                // Endcaps
                for (int i = group.startIndex; i <= group.endIndex + 1 && i < points.Count; i++)
                {
                    bool needCap = false;

                    if (i == 0 || i == points.Count - 1)
                    {
                        needCap = true;
                    }
                    else if (i == group.startIndex && i > 0)
                    {
                        needCap = true;
                    }
                    else if (i == group.endIndex + 1 && i < points.Count - 1)
                    {
                        needCap = true;
                    }
                    else if (i > 0 && i < points.Count - 1)
                    {
                        var v1 = points[i] - points[i - 1];
                        var v2 = points[i + 1] - points[i];

                        if (v1.Length > 0.01 && v2.Length > 0.01)
                        {
                            v1.Normalize();
                            v2.Normalize();
                            double dotProduct = v1 * v2;
                            if (dotProduct < Math.Cos(0.1 * Math.PI / 180.0))
                                needCap = true;
                        }
                    }

                    if (needCap)
                    {
                        var capGeometry = new EllipseGeometry(points[i], halfWidthPx, halfWidthPx);
                        pathGeometry = Geometry.Combine(pathGeometry, capGeometry, GeometryCombineMode.Union, null);
                    }
                }

                var groupBrush = new SolidColorBrush(group.color);

                var groupPath = new System.Windows.Shapes.Path
                {
                    Data = pathGeometry,
                    Fill = MakeAlphaBrush(groupBrush, 60),
                    Stroke = null,
                    IsHitTestVisible = false,
                    Tag = $"PolylineZone_{group.startIndex}_{group.endIndex}"
                };

                TileCanvas.Children.Add(groupPath);
                Panel.SetZIndex(groupPath, 500);

                _polylineVisualGroups[polyline].Add(groupPath);
            }

            for (int i = 0; i < points.Count - 1; i++)
            {
                var p1 = points[i];
                var p2 = points[i + 1];

                var dir = p2 - p1;
                double len = dir.Length;
                if (len < 0.01)
                    continue;

                ActivationZone? zone = segmentZones?.FirstOrDefault(s => s.SegmentIndex == i);

                if (zone != null)
                {
                    var startDot = _polylineVertexMap
                        .FirstOrDefault(kvp => kvp.Value.polyline == polyline &&
                                               kvp.Value.pointIndex == i)
                        .Key;

                    if (startDot != null &&
                        _polylineVertexToCircle.TryGetValue(startDot, out var startCircle))
                    {
                        AddCircleForSegment(zone, startCircle);
                    }

                    var endDot = _polylineVertexMap
                        .FirstOrDefault(kvp => kvp.Value.polyline == polyline &&
                                               kvp.Value.pointIndex == i + 1)
                        .Key;

                    if (endDot != null &&
                        _polylineVertexToCircle.TryGetValue(endDot, out var endCircle))
                    {
                        AddCircleForSegment(zone, endCircle);
                    }
                }

                double widthMeters = zone?.Width ?? _polylineZoneWidthMeters;
                Color color = zone != null
                    ? ParseColor(zone.Color)
                    : defaultBrush.Color;

                double halfWidthPx = (widthMeters / 2.0) / mpp;

                dir.Normalize();
                var perp = new Vector(-dir.Y, dir.X);

                var topLeft = p1 + perp * halfWidthPx;
                var topRight = p2 + perp * halfWidthPx;
                var bottomRight = p2 - perp * halfWidthPx;
                var bottomLeft = p1 - perp * halfWidthPx;

                var geometry = new PathGeometry { FillRule = FillRule.Nonzero };

                var figure = new PathFigure
                {
                    StartPoint = topLeft,
                    IsClosed = true
                };

                figure.Segments.Add(new LineSegment(topRight, true));
                figure.Segments.Add(new LineSegment(bottomRight, true));
                figure.Segments.Add(new LineSegment(bottomLeft, true));
                geometry.Figures.Add(figure);

                // přidá kruhy na začátek a konec segmentu
                var startCap = new EllipseGeometry(p1, halfWidthPx, halfWidthPx);
                var endCap = new EllipseGeometry(p2, halfWidthPx, halfWidthPx);

                Geometry capsule = Geometry.Combine(geometry, startCap, GeometryCombineMode.Union, null);
                capsule = Geometry.Combine(capsule, endCap, GeometryCombineMode.Union, null);

                var brush = new SolidColorBrush(color);

                var segmentPath = new System.Windows.Shapes.Path
                {
                    Data = capsule,
                    Fill = Brushes.Transparent,
                    Stroke = null,
                    IsHitTestVisible = false,
                    Tag = $"PolylineActiveSegment_{i}"
                };

                TileCanvas.Children.Add(segmentPath);
                Panel.SetZIndex(segmentPath, 501);

                _polylineToSegments[polyline].Add(segmentPath);

                if (zone != null)
                {
                    _segmentToVisualPath[zone] = segmentPath;
                }
            }

            Console.WriteLine($"[REBUILD VAR]  Created {_polylineToSegments[polyline].Count} Path elements");
        }

        private void AddCircleForSegment(ActivationZone zone, Ellipse circle)
        {
            if (!_segmentToCircles.TryGetValue(zone, out var list))
            {
                list = new List<Ellipse>();
                _segmentToCircles[zone] = list;
            }

            if (!list.Contains(circle))
                list.Add(circle);
        }

        /// <summary>
        /// Updates the geometry of a segment between two points.
        /// </summary>
        /// <param name="segment">The segment to update.</param>
        /// <param name="p1">The starting point of the segment.</param>
        /// <param name="p2">The ending point of the segment.</param>
        /// <param name="halfWidthPx">Half of the width of the segment in pixels.</param>
        private void UpdateSegmentGeometry(System.Windows.Shapes.Path segment, Point p1, Point p2, double halfWidthPx)
        {
            // This method is deprecated - zones are now rebuilt entirely using RebuildPolylineZone
            // Keeping for backward compatibility but it won't be called in normal flow
            var dir = p2 - p1;
            double len = dir.Length;
            if (len < 0.01) return;

            dir.Normalize();
            var perp = new Vector(-dir.Y, dir.X);

            var topLeft = p1 + perp * halfWidthPx;
            var topRight = p2 + perp * halfWidthPx;
            var bottomRight = p2 - perp * halfWidthPx;
            var bottomLeft = p1 - perp * halfWidthPx;

            var geometry = new PathGeometry();
            var segFigure = new PathFigure { StartPoint = topLeft, IsClosed = true };
            segFigure.Segments.Add(new LineSegment(topRight, true));
            segFigure.Segments.Add(new LineSegment(bottomRight, true));
            segFigure.Segments.Add(new LineSegment(bottomLeft, true));
            geometry.Figures.Add(segFigure);

            segment.Data = geometry;
        }

        /// <summary>
        /// Updates or creates a segment between two points.
        /// </summary>
        /// <param name="p1">The starting point of the segment.</param>
        /// <param name="p2">The ending point of the segment.</param>
        /// <param name="halfWidthPx">Half of the width of the segment in pixels.</param>
        /// <param name="existingSegments">The list of existing segments to update or add to.</param>
        private void UpdateOrCreateSegment(Point p1, Point p2, double halfWidthPx, List<System.Windows.Shapes.Path> existingSegments)
        {
            var dir = p2 - p1;
            double len = dir.Length;
            if (len < 0.01) return;

            dir.Normalize();
            var perp = new Vector(-dir.Y, dir.X);

            var topLeft = p1 + perp * halfWidthPx;
            var topRight = p2 + perp * halfWidthPx;
            var bottomRight = p2 - perp * halfWidthPx;
            var bottomLeft = p1 - perp * halfWidthPx;

            var existingSegment = existingSegments.FirstOrDefault(seg =>
            {
                if (seg.Data is not PathGeometry geom || geom.Figures.Count == 0)
                    return false;
                var fig = geom.Figures[0];
                return Math.Abs(fig.StartPoint.X - topLeft.X) < 5 &&
                       Math.Abs(fig.StartPoint.Y - topLeft.Y) < 5;
            });

            PathGeometry geometry;
            System.Windows.Shapes.Path segmentPath;

            if (existingSegment != null)
            {
                segmentPath = existingSegment;
                geometry = new PathGeometry();
            }
            else
            {
                geometry = new PathGeometry();
                segmentPath = new System.Windows.Shapes.Path
                {
                    Fill = new SolidColorBrush(Color.FromArgb(50,
                        ((SolidColorBrush)_strokeBrush).Color.R,
                        ((SolidColorBrush)_strokeBrush).Color.G,
                        ((SolidColorBrush)_strokeBrush).Color.B)),
                    Stroke = null,
                    StrokeThickness = 0,
                    IsHitTestVisible = false,
                    Tag = "PolylineZoneSegment"
                };
                TileCanvas.Children.Add(segmentPath);
                Panel.SetZIndex(segmentPath, 500);
            }

            var segFigure = new PathFigure { StartPoint = topLeft, IsClosed = true };
            segFigure.Segments.Add(new LineSegment(topRight, true));
            segFigure.Segments.Add(new LineSegment(bottomRight, true));
            segFigure.Segments.Add(new LineSegment(bottomLeft, true));
            geometry.Figures.Add(segFigure);
            segmentPath.Data = geometry;
        }

        /// <summary>
        /// Cancels all drawing.
        /// </summary>
        private bool CancelAllDrawing()
        {
            bool didSomething = false;

            if (rectPhase != RectangleDrawPhase.None ||
                tempHeightLine != null || tempWidthLine != null ||
                previewRect != null || startPointEllipse != null || secondPointEllipse != null)
            {
                ClearTempElements();
                rectPhase = RectangleDrawPhase.None;
                isDrawing = false;
                didSomething = true;
            }

            if (currentDrawingMode == DrawingMode.Polyline && currentPolyline != null)
            {
                Console.WriteLine($"[POLYLINE] Cancelled with {polylinePoints.Count} points");

                bool wasContinuation = _polylineCommittedPointsCount > 0;

                if (wasContinuation)
                {
                    Console.WriteLine(
                        $"[POLYLINE] Restoring previously finalized polyline with " +
                        $"{_polylineCommittedPointsCount} committed points");

                    if (_polylineToSegmentZones.TryGetValue(currentPolyline, out var tableSegments))
                    {
                        int committedSegmentCount =
                            Math.Max(0, _polylineCommittedPointsCount - 1);

                        var newSegments = tableSegments
                            .Where(s => s.SegmentIndex >= committedSegmentCount)
                            .ToList();

                        foreach (var segment in newSegments)
                        {
                            Console.WriteLine(
                                $"[POLYLINE] Cancel continuation: removing new segment " +
                                $"{segment.SegmentIndex}");

                            segment.PropertyChanged -= ActivationZone_PropertyChanged;

                            tableSegments.Remove(segment);
                            _polylineRows.Remove(segment);

                            if (ActivationZonesCollection.Contains(segment))
                                ActivationZonesCollection.Remove(segment);

                            if (PolylineZonesCollection.Contains(segment))
                                PolylineZonesCollection.Remove(segment);

                            _segmentToVisualPath.Remove(segment);
                            _segmentToCircles.Remove(segment);
                        }
                    }

                    for (int i = _polylineCommittedPointsCount;
                         i < polylineVertexDots.Count;
                         i++)
                    {
                        var dot = polylineVertexDots[i];

                        if (TileCanvas.Children.Contains(dot))
                            TileCanvas.Children.Remove(dot);

                        if (_polylineVertexToCircle.TryGetValue(dot, out var circle))
                        {
                            if (TileCanvas.Children.Contains(circle))
                                TileCanvas.Children.Remove(circle);

                            _polylineVertexToCircle.Remove(dot);
                        }

                        _polylineVertexMap.Remove(dot);
                    }

                    for (int i = _polylineCommittedPointsCount;
                         i < _currentPolylineCircles.Count;
                         i++)
                    {
                        var circle = _currentPolylineCircles[i];

                        if (TileCanvas.Children.Contains(circle))
                            TileCanvas.Children.Remove(circle);
                    }

                    if (_polylineToSegments.TryGetValue(currentPolyline, out var visualSegments))
                    {
                        foreach (var segmentPath in visualSegments.ToList())
                        {
                            if (TileCanvas.Children.Contains(segmentPath))
                                TileCanvas.Children.Remove(segmentPath);
                        }

                        visualSegments.Clear();
                    }

                    _currentPolylineSegments.Clear();

                    if (_polylineVisualGroups.TryGetValue(currentPolyline, out var visualGroups))
                    {
                        foreach (var group in visualGroups.ToList())
                        {
                            if (TileCanvas.Children.Contains(group))
                                TileCanvas.Children.Remove(group);
                        }

                        visualGroups.Clear();
                    }

                    var committedPoints = polylinePoints
                        .Take(_polylineCommittedPointsCount)
                        .ToList();

                    currentPolyline.Points.Clear();

                    foreach (var point in committedPoints)
                        currentPolyline.Points.Add(point);

                    if (_polylineGeoPoints.TryGetValue(currentPolyline, out var geoPoints))
                    {
                        var committedGeoPoints = geoPoints
                            .Take(_polylineCommittedPointsCount)
                            .ToList();

                        _polylineGeoPoints[currentPolyline] = committedGeoPoints;
                    }

                    var rebuiltPoints = currentPolyline.Points.ToList();

                    if (_polylineToSegmentZones.TryGetValue(
                            currentPolyline,
                            out var remainingSegments) &&
                        remainingSegments.Count > 0)
                    {
                        RebuildPolylineZoneWithVariableWidths(
                            currentPolyline,
                            rebuiltPoints);
                    }
                    else
                    {
                        double mpp = MetersPerPixel(latitude, zoom);
                        double halfWidthPx =
                            (_polylineZoneWidthMeters / 2.0) / mpp;

                        RebuildPolylineZone(
                            currentPolyline,
                            rebuiltPoints,
                            halfWidthPx);
                    }

                    UpdatePolylineDirectionArrows(
                        currentPolyline,
                        rebuiltPoints);

                    if (!mapRectangles.Any(mr => mr.Shape == currentPolyline))
                        mapRectangles.Add(new MapRectangle(currentPolyline));

                    Console.WriteLine(
                        $"[POLYLINE] Restored polyline with " +
                        $"{currentPolyline.Points.Count} points");
                }
                else
                {
                    Console.WriteLine("[POLYLINE] Deleting new polyline completely");

                    // Směrové šipky
                    if (_polylineDirectionArrows.TryGetValue(
                            currentPolyline,
                            out var arrows))
                    {
                        foreach (var arrow in arrows.ToList())
                        {
                            if (TileCanvas.Children.Contains(arrow))
                                TileCanvas.Children.Remove(arrow);
                        }

                        _polylineDirectionArrows.Remove(currentPolyline);
                    }

                    // Visual groups
                    if (_polylineVisualGroups.TryGetValue(
                            currentPolyline,
                            out var groups))
                    {
                        foreach (var group in groups.ToList())
                        {
                            if (TileCanvas.Children.Contains(group))
                                TileCanvas.Children.Remove(group);
                        }

                        _polylineVisualGroups.Remove(currentPolyline);
                    }

                    // Table segments
                    if (_polylineToSegmentZones.TryGetValue(
                            currentPolyline,
                            out var tableSegments))
                    {
                        foreach (var segment in tableSegments.ToList())
                        {
                            segment.PropertyChanged -= ActivationZone_PropertyChanged;

                            _polylineRows.Remove(segment);

                            if (ActivationZonesCollection.Contains(segment))
                                ActivationZonesCollection.Remove(segment);

                            if (PolylineZonesCollection.Contains(segment))
                                PolylineZonesCollection.Remove(segment);

                            _segmentToVisualPath.Remove(segment);
                            _segmentToCircles.Remove(segment);
                        }

                        _polylineToSegmentZones.Remove(currentPolyline);
                    }

                    // Main polyline
                    if (TileCanvas.Children.Contains(currentPolyline))
                        TileCanvas.Children.Remove(currentPolyline);

                    // Vertex dots + circles
                    foreach (var dot in polylineVertexDots.ToList())
                    {
                        if (TileCanvas.Children.Contains(dot))
                            TileCanvas.Children.Remove(dot);

                        if (_polylineVertexToCircle.TryGetValue(dot, out var circle))
                        {
                            if (TileCanvas.Children.Contains(circle))
                                TileCanvas.Children.Remove(circle);

                            _polylineVertexToCircle.Remove(dot);
                        }

                        _polylineVertexMap.Remove(dot);
                    }

                    foreach (var circle in _currentPolylineCircles.ToList())
                    {
                        if (TileCanvas.Children.Contains(circle))
                            TileCanvas.Children.Remove(circle);
                    }

                    // Segment paths
                    if (_polylineToSegments.TryGetValue(
                            currentPolyline,
                            out var segments))
                    {
                        foreach (var segmentPath in segments.ToList())
                        {
                            if (TileCanvas.Children.Contains(segmentPath))
                                TileCanvas.Children.Remove(segmentPath);
                        }

                        _polylineToSegments.Remove(currentPolyline);
                    }

                    _polylineGeoPoints.Remove(currentPolyline);

                    var mapRect = mapRectangles
                        .FirstOrDefault(mr => mr.Shape == currentPolyline);

                    if (mapRect != null)
                        mapRectangles.Remove(mapRect);
                }

                currentPolyline = null;

                polylinePoints.Clear();
                polylineVertexDots.Clear();
                _currentPolylineCircles.Clear();
                _currentPolylineSegments.Clear();
                _currentPolylineCircleGeoPoints.Clear();

                _isDrawingPolyline = false;
                isDrawing = false;
                _polylineCommittedPointsCount = 0;

                didSomething = true;
            }

            if (currentDrawingMode == DrawingMode.Point && isDrawing)
            {
                isDrawing = false;
                didSomething = true;
            }

            if (isMiddleMousePanning)
            {
                isMiddleMousePanning = false;
                isPanning = false;
                TileCanvas.ReleaseMouseCapture();
                didSomething = true;
            }

            if (dimensionTextBlock != null &&
                dimensionTextBlock.Visibility == Visibility.Visible)
            {
                dimensionTextBlock.Visibility = Visibility.Collapsed;
                didSomething = true;
            }

            if (selectedElement != null)
            {
                DeselectElement();
                didSomething = true;
            }

            TileCanvas.Cursor = Cursors.Arrow;

            if (PolylineWidthPanel != null &&
                PolylineWidthPanel.Visibility == Visibility.Visible)
            {
                PolylineWidthPanel.Visibility = Visibility.Collapsed;
                didSomething = true;
            }

            SetSelectionMode();

            return didSomething;
        }

        /// <summary>
        /// Checks if drawn points are close to each other (to prevent duplicates when dragging vertices).
        /// </summary>
        /// <param name="p1">The first point to compare.</param>
        /// <param name="p2">The second point to compare.</param>
        /// <param name="threshold">The distance threshold to consider points as close.</param>
        /// <returns>True if the points are close; otherwise, false.</returns>
        private bool ArePointsClose(Point p1, Point p2, double threshold = 2.0)
        {
            return Math.Abs(p1.X - p2.X) < threshold && Math.Abs(p1.Y - p2.Y) < threshold;
        }

        /// <summary>
        /// Recalculates the connection line between points after dragging or removing point(s).
        /// </summary>
        private void RecalculateConnectionLine()
        {
            connectionLine.Points.Clear();

            foreach (var pt in points)
            {
                connectionLine.Points.Add(pt.Position);
            }
        }

        /// <summary>
        /// Logic for entering rectangles in selection mode - highlights them on mouse enter and selects on click.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        private void Rectangle_MouseEnter(object? sender, MouseEventArgs e)
        {
            if (sender is not Rectangle rect)
                return;

            if (_highlightedRect == rect)
                return;

            bool isTableSelected =
                ActivationZonesDataGrid?.SelectedItem is ActivationZone az &&
                ReferenceEquals(az.Rectangle, rect);

            if (isTableSelected)
                return;

            _highlightedRect = rect;
            _highlightedRectOldBrush = rect.Stroke;
            _highlightedRectOldThickness = rect.StrokeThickness;

            if (activationZones != null &&
                activationZones.TryGetValue(rect, out var zone))
            {
                var brush = TryBrushFromColor(zone.Color) as SolidColorBrush
                            ?? Brushes.Gray;

                rect.Stroke = MakeAlphaBrush(brush, 255);
            }

            if (_zoneArrows.TryGetValue(rect, out var arrow))
            {
                arrow.Fill = rect.Stroke;
                arrow.Opacity = 1.0;
            }

            rect.StrokeThickness =
                Math.Max(1.0, _highlightedRectOldThickness) + 1.5;
        }

        /// <summary>
        /// Logic for leaving rectangles in selection mode - resets their appearance.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        private void Rectangle_MouseLeave(object? sender, MouseEventArgs e)
        {
            if (sender is not Rectangle rect)
                return;

            if (_highlightedRect != rect)
                return;

            rect.Stroke = _highlightedRectOldBrush;
            rect.StrokeThickness = _highlightedRectOldThickness;

            if (_zoneArrows.TryGetValue(rect, out var arrow))
            {
                if (activationZones != null &&
                    activationZones.TryGetValue(rect, out var zone))
                {
                    var brush = TryBrushFromColor(zone.Color) as SolidColorBrush ?? Brushes.Gray;
                    arrow.Fill = brush;
                }

                arrow.Stroke = null;
                arrow.StrokeThickness = 0;
                arrow.Opacity = 0.20;
            }

            _highlightedRect = null;
            _highlightedRectOldBrush = null;
        }

        /// <summary>
        /// Logic for handling mouse left button down event on rectangles in selection mode - selects the rectangle.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        private void Rectangle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var rectangle = sender as Rectangle;

            if (isSelectionMode)
            {
                if (rectangle != null)
                {
                    SelectElement(rectangle);
                }
                e.Handled = true;
                return;
            }

            if (currentDrawingMode == DrawingMode.None)
            {
                ActivationZone? zone = null;
                activationZones.TryGetValue(rectangle, out zone);
                zone ??= switchZones.TryGetValue(rectangle, out var sz) ? sz : null;

                if (zone != null)
                {
                    SelectZoneInTable(zone);
                    e.Handled = true;
                }
            }
        }

        /// <summary>
        /// Updates the hit test visibility for selectable elements based on the current mode.
        /// </summary>
        private void UpdateHitTestForSelectableElements()
        {
            bool allowSelection = isSelectionMode || currentDrawingMode == DrawingMode.Rectangle;

            foreach (var rect in TileCanvas.Children.OfType<Rectangle>())
            {
                rect.IsHitTestVisible = allowSelection;
            }

        }

        /// <summary>
        /// Sets the drawing mode and updates the hit test visibility for selectable elements.
        /// </summary>
        /// <param name="mode">The drawing mode to set.</param>
        private void SetDrawingMode(DrawingMode mode)
        {
            currentDrawingMode = mode;
            isSelectionMode = false;
            UpdateHitTestForSelectableElements();
        }

        /// <summary>
        /// Sets the selection mode and updates the hit test visibility for selectable elements.
        /// </summary>
        private void SetSelectionMode()
        {
            isSelectionMode = true;
            currentDrawingMode = DrawingMode.None;
            UpdateHitTestForSelectableElements();
        }

        /// <summary>
        /// Handles the mouse left button down event on the activation zone.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        private void ActivationZone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        /// <summary>
        /// Draws the radius circle around the RSU if the checkbox is checked.
        /// </summary>
        private void DrawRadiusCircle()
        {
            // Do nothing (and ensure removal) when the checkbox is unchecked
            if (CircleCheckBox?.IsChecked != true)
            {
                if (radiusEllipse != null)
                {
                    TileCanvas.Children.Remove(radiusEllipse);
                    radiusEllipse = null;
                }
                return;
            }

            if (RadiusComboBox.SelectedItem is ComboBoxItem selectedItem &&
                double.TryParse(selectedItem.Content.ToString(), out double radiusMeters) &&
                srvLatitude.HasValue && srvLongitude.HasValue)
            {
                double mpp = MetersPerPixel(srvLatitude.Value, zoom);
                double radiusPixels = radiusMeters / mpp;

                var (x, y) = ConvertLatLonToCanvasXY(srvLatitude.Value, srvLongitude.Value);
                Point rsuCenter = new Point(x, y);

                if (radiusEllipse != null)
                    TileCanvas.Children.Remove(radiusEllipse);

                radiusEllipse = new Ellipse
                {
                    Width = radiusPixels * 2,
                    Height = radiusPixels * 2,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1,
                    Fill = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)),
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(radiusEllipse, rsuCenter.X - radiusPixels);
                Canvas.SetTop(radiusEllipse, rsuCenter.Y - radiusPixels);

                TileCanvas.Children.Add(radiusEllipse);
                Panel.SetZIndex(radiusEllipse, 1);
                Console.WriteLine("[RSU CIRCLE] Drawing radius circle of radius: " + radiusPixels);
            }
            else
            {
                // If we can't draw (e.g., missing coords), ensure it is removed
                if (radiusEllipse != null)
                {
                    TileCanvas.Children.Remove(radiusEllipse);
                    radiusEllipse = null;
                }
                Console.WriteLine("[RSU CIRCLE] Circle cannot be drawn - missing coordinates");
            }
        }

        /// <summary>
        /// Cleans up old vehicles that haven't been updated in the last 60 seconds.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        private void CleanupOldVehicles(object? sender, EventArgs e)
        {
            var now = DateTime.Now;

            var toRemoveFromTable = TramTable
                .Where(t => (now - t.LastMessageTimestamp)?.TotalSeconds > TableRowTimeout.TotalSeconds)
                .ToList();

            foreach (var item in toRemoveFromTable)
            {
                if (drawnTramIds?.Any(id => id.EndsWith(item?.VehicleId ?? string.Empty)) == true)
                    continue;

                TramTable.Remove(item);
            }

            for (int i = 0; i < drawnTrams?.Length; i++)
            {
                var tram = drawnTrams[i];
                if (tram == null) continue;

                if (tram.IsRecorded)
                {
                    if (isPlaying) continue;
                    if ((now - tram.LastUpdate).TotalSeconds > 30)
                        StartDrawnTramCleanup(i, tram);
                }
                else
                {
                    if ((now - tram.LastUpdate).TotalSeconds > 10)
                        StartDrawnTramCleanup(i, tram);
                }
            }

            var toRemove = activeVehicles
                .Where(kvp => (now - kvp.Value.LastUpdate).TotalSeconds > 10)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var vehicleId in toRemove)
            {
                if (!activeVehicles.TryGetValue(vehicleId, out var vehicle))
                    continue;

                if (vehicle.Ellipse?.Tag?.ToString() == "Srv")
                    continue;

                if (vehicleTrailCleanupTokens.ContainsKey(vehicleId))
                    continue;

                Console.WriteLine($"[VEHICLE] Starting gradual trail removal for {vehicleId}");

                var cts = new CancellationTokenSource();
                vehicleTrailCleanupTokens[vehicleId] = cts;

                _ = RemoveTrailGradually(vehicleId, vehicle, cts.Token);
            }
        }

        private void StartDrawnTramCleanup(int idx, MapPoint tram)
        {
            string key = $"drawn_{idx}_trail";

            if (vehicleTrailCleanupTokens.ContainsKey(key))
                return;

            var cts = new CancellationTokenSource();
            vehicleTrailCleanupTokens[key] = cts;

            _ = RemoveDrawnTramTrailGradually(idx, tram, cts.Token);
        }

        // mozna memory leak
        private async Task RemoveDrawnTramTrailGradually(int idx, MapPoint tram, CancellationToken token)
        {
            string key = $"drawn_{idx}_trail";

            try
            {
                bool continueRemoving = true;

                while (continueRemoving)
                {
                    continueRemoving = false;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        var trail = drawnTramTrails[idx];

                        if (trail != null && trail.Points.Count > 1)
                        {
                            trail.Points.RemoveAt(0);
                            continueRemoving = true;

                            if (tram.TrailDots != null && tram.TrailDots.Count > 0)
                            {
                                var firstDot = tram.TrailDots[0];
                                TileCanvas.Children.Remove(firstDot);
                                tram.TrailDots.RemoveAt(0);
                            }

                            if (drawnTramTrailPoints[idx].Count > 0)
                                drawnTramTrailPoints[idx].RemoveAt(0);

                            if (drawnTramTrailGeoPoints[idx].Count > 0)
                                drawnTramTrailGeoPoints[idx].RemoveAt(0);
                        }
                    });

                    if (continueRemoving)
                        await Task.Delay(1000, token);
                }

                await Task.Delay(1000, token);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    RemoveDrawnTramCompletely(idx, tram);
                });
            }
            catch (TaskCanceledException)
            {
            }
            finally
            {
                if (vehicleTrailCleanupTokens.TryGetValue(key, out var cts))
                {
                    cts.Dispose();
                    vehicleTrailCleanupTokens.Remove(key);
                }
            }
        }

        /// <summary>
        /// Removes a vehicle completely from the map and all associated data structures.
        /// </summary>
        /// <param name="vehicleId">The ID of the vehicle to remove.</param>
        /// <param name="vehicle">The vehicle object to remove.</param>
        private void RemoveVehicleCompletely(string vehicleId, MapPoint vehicle)
        {
            if (vehicle == null) return;

            // Remove ellipse
            if (vehicle.Ellipse != null && TileCanvas.Children.Contains(vehicle.Ellipse))
                TileCanvas.Children.Remove(vehicle.Ellipse);

            // Remove text
            if (vehicle.Text != null && TileCanvas.Children.Contains(vehicle.Text))
                TileCanvas.Children.Remove(vehicle.Text);

            // Remove speed text
            if (vehicle.Speed != null && TileCanvas.Children.Contains(vehicle.Speed))
                TileCanvas.Children.Remove(vehicle.Speed);

            // Remove accuracy text
            if (_liveAccuracyTextById.TryGetValue(vehicleId, out var accText))
            {
                if (TileCanvas.Children.Contains(accText))
                    TileCanvas.Children.Remove(accText);
                _liveAccuracyTextById.Remove(vehicleId);
            }

            // Remove trail polyline
            var trailLines = TileCanvas.Children.OfType<Polyline>()
                .Where(pl => pl.Tag is string tag && tag == $"trail_{vehicleId}")
                .ToList();
            foreach (var line in trailLines)
                TileCanvas.Children.Remove(line);

            // Remove trail dots
            if (vehicle.TrailDots != null)
            {
                foreach (var dot in vehicle.TrailDots.ToList())
                {
                    if (TileCanvas.Children.Contains(dot))
                        TileCanvas.Children.Remove(dot);
                }
                vehicle.TrailDots.Clear();
            }

            // Remove vehicle box
            if (_vehicleBoxes.TryGetValue(vehicleId, out var box))
            {
                if (TileCanvas.Children.Contains(box))
                    TileCanvas.Children.Remove(box);
                _vehicleBoxes.Remove(vehicleId);
            }

            // Remove accuracy circle
            var accCircles = TileCanvas.Children.OfType<Ellipse>()
                .Where(e => e.Tag is string s && s == $"live_acc_{vehicleId}")
                .ToList();
            foreach (var circle in accCircles)
                TileCanvas.Children.Remove(circle);

            // Remove from dictionaries
            activeVehicles.Remove(vehicleId);
            _lastLatLon.Remove(vehicleId);
            _lastHeadingLive.Remove(vehicleId);
            _lastLiveAccuracyById.Remove(vehicleId);

            // Remove any cleanup tokens
            if (vehicleTrailCleanupTokens.TryGetValue(vehicleId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                vehicleTrailCleanupTokens.Remove(vehicleId);
            }
        }

        /// <summary>
        /// Hides vehicle, cleans up trail
        /// </summary>
        private void HideVehicleKeepTrail(string vehicleId, MapPoint vehicle)
        {
            if (vehicle == null) return;

            if (vehicle.Ellipse != null) vehicle.Ellipse.Visibility = Visibility.Collapsed;
            if (vehicle.Text != null) vehicle.Text.Visibility = Visibility.Collapsed;
            if (vehicle.Speed != null) vehicle.Speed.Visibility = Visibility.Collapsed;

            if (_vehicleBoxes.TryGetValue(vehicleId, out var box))
                box.Visibility = Visibility.Collapsed;

            if (_liveAccuracyTextById.TryGetValue(vehicleId, out var accText))
                accText.Visibility = Visibility.Collapsed;

            var accCircles = TileCanvas.Children.OfType<Ellipse>()
                .Where(e => e.Tag is string s && s == $"live_acc_{vehicleId}")
                .ToList();
            foreach (var circle in accCircles)
                circle.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Removes a drawn tram completely from the map and all associated data structures.
        /// </summary>
        /// <param name="idx">The index of the drawn tram to remove.</param>
        /// <param name="tram">The tram object to remove.</param>
        private void RemoveDrawnTramCompletely(int idx, MapPoint tram)
        {
            Dispatcher.Invoke(() =>
            {
                string tramId = drawnTramIds[idx];

                if (tram.Ellipse != null)
                    TileCanvas.Children.Remove(tram.Ellipse);

                if (tram.Text != null)
                    TileCanvas.Children.Remove(tram.Text);

                if (tram.Speed != null)
                    TileCanvas.Children.Remove(tram.Speed);

                if (drawnTramTrails[idx] != null)
                {
                    TileCanvas.Children.Remove(drawnTramTrails[idx]);
                    drawnTramTrails[idx] = null;
                }

                if (tram.TrailDots != null)
                {
                    foreach (var dot in tram.TrailDots.ToList())
                        TileCanvas.Children.Remove(dot);

                    tram.TrailDots.Clear();
                }

                if (_vehicleBoxes.TryGetValue(tramId, out var box))
                {
                    TileCanvas.Children.Remove(box);
                    _vehicleBoxes.Remove(tramId);
                }

                if (_liveAccuracyTextById.TryGetValue(tramId, out var accText))
                {
                    TileCanvas.Children.Remove(accText);
                    _liveAccuracyTextById.Remove(tramId);
                }

                var accCircles = TileCanvas.Children
                    .OfType<Ellipse>()
                    .Where(e => e.Tag is string tag && tag == $"live_acc_{tramId}")
                    .ToList();

                foreach (var circle in accCircles)
                    TileCanvas.Children.Remove(circle);

                drawnTramTrailPoints[idx].Clear();
                drawnTramTrailGeoPoints[idx].Clear();

                drawnTramLat[idx] = null;
                drawnTramLon[idx] = null;
                drawnTrams[idx] = null;

                _lastLatLon.Remove(tramId);
                _lastHeadingLive.Remove(tramId);
                _lastLiveAccuracyById.Remove(tramId);
                activeVehicles.Remove(tramId);
                vehicleColorMap.Remove(tramId);
                lastCamUpdates.Remove(tramId);
                lastCamTimes.Remove(tramId);
                prevCamTimes.Remove(tramId);

                string shortId = tramId.Length >= 4 ? tramId[^4..] : tramId;

                var tramInfo = TramTable.FirstOrDefault(t => t.VehicleId == shortId);
                if (tramInfo != null)
                    TramTable.Remove(tramInfo);

                string tokenKey = $"drawn_{idx}_trail";
                if (vehicleTrailCleanupTokens.TryGetValue(tokenKey, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                    vehicleTrailCleanupTokens.Remove(tokenKey);
                }
            });
        }

        private void AddCamToBuffer(string message)
        {
            bool shouldDump = false;

            lock (_camBufferLock)
            {
                recordedCamMessages.Add(message);
                shouldDump = recordedCamMessages.Count >= MaxRecordedCamMessages;
            }

            if (shouldDump)
                _ = DumpCamBufferAsync("limit");
        }

        private void AddSrvToBuffer(string message)
        {
            bool shouldDump = false;

            lock (_srvBufferLock)
            {
                recordedSrvMessages.Add(message);
                shouldDump = recordedSrvMessages.Count >= MaxRecordedSrvMessages;
            }

            if (shouldDump)
                _ = DumpSrvBufferAsync("limit");
        }

        private async Task DumpCamBatchAsync(List<string> batch)
        {
            try
            {
                string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cam_dumps");
                Directory.CreateDirectory(dir);

                string file = System.IO.Path.Combine(
                    dir,
                    $"cam_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log"
                );

                await File.WriteAllLinesAsync(file, batch);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CAM DUMP ERR] {ex.Message}");
            }
        }

        // mozna memory leak
        /// <summary>
        /// Gradually removes a vehicle's trail from the map.
        /// </summary>
        /// <param name="vehicleId">The ID of the vehicle whose trail is to be removed.</param>
        /// <param name="vehicle">The vehicle object whose trail is to be removed.</param>
        /// <param name="token">A cancellation token to cancel the operation.</param>
        private async Task RemoveTrailGradually(string vehicleId, MapPoint vehicle, CancellationToken token)
        {
            try
            {
                bool continueRemoving = true;
                while (continueRemoving)
                {
                    continueRemoving = false;
                    Dispatcher.Invoke(() =>
                    {
                        var trail = TileCanvas.Children.OfType<Polyline>()
                            .FirstOrDefault(pl => pl.Tag != null && pl.Tag.ToString() == $"trail_{vehicleId}");

                        if (trail != null && trail.Points.Count > 1)
                        {
                            trail.Points.RemoveAt(0);
                            continueRemoving = true;

                            // Pak smazat odpovídající tečku od začátku
                            if (vehicle.TrailDots != null && vehicle.TrailDots.Count > 0)
                            {
                                var firstDot = vehicle.TrailDots[0];
                                TileCanvas.Children.Remove(firstDot);
                                vehicle.TrailDots.RemoveAt(0);
                            }
                        }
                        else if (trail != null)
                        {
                            TileCanvas.Children.Remove(trail);
                        }
                    });

                    if (continueRemoving)
                        await Task.Delay(300, token);
                }

                Dispatcher.Invoke(() =>
                {
                    if (vehicle.TrailDots != null)
                    {
                        foreach (var dot in vehicle.TrailDots.ToList())
                            TileCanvas.Children.Remove(dot);
                        vehicle.TrailDots.Clear();
                    }
                });

                await Task.Delay(500, token);

                Dispatcher.Invoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    if (!vehicleTrailCleanupTokens.TryGetValue(vehicleId, out var activeCts)
                        || activeCts.Token != token)
                        return;

                    vehicleTrailCleanupTokens.Remove(vehicleId);
                    RemoveVehicleCompletely(vehicleId, vehicle);
                });
            }
            catch (TaskCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    if (vehicle == null) return;
                    if (vehicle.Ellipse != null) vehicle.Ellipse.Visibility = Visibility.Visible;
                    if (vehicle.Text != null) vehicle.Text.Visibility = Visibility.Visible;
                    if (vehicle.Speed != null) vehicle.Speed.Visibility = Visibility.Visible;
                    if (_vehicleBoxes.TryGetValue(vehicleId, out var box))
                        box.Visibility = Visibility.Visible;
                    if (_liveAccuracyTextById.TryGetValue(vehicleId, out var accTb))
                        accTb.Visibility = Visibility.Visible;
                });
            }
        }

        /// <summary>
        /// Adds an undo and redo action to the respective stacks.
        /// </summary>
        /// <param name="undo">The action to undo.</param>
        /// <param name="redo">The action to redo.</param>
        private void AddUndoRedo(Action undo, Action redo)
        {
            undoStack.Push(new UndoRedoAction { UndoAction = undo, RedoAction = redo });
            redoStack.Clear();
        }

        /// <summary>
        /// Undos the last action by invoking the undo action from the top of the undo stack and pushing it onto the redo stack.
        /// </summary>
        private void Undo()
        {
            if (undoStack.Count > 0)
            {
                var action = undoStack.Pop();
                action.UndoAction?.Invoke();
                redoStack.Push(action);
            }
        }

        /// <summary>
        /// Redoes the last undone action by invoking the redo action from the top of the redo stack and pushing it onto the undo stack.
        /// </summary>
        private void Redo()
        {
            if (redoStack.Count > 0)
            {
                var action = redoStack.Pop();
                action.RedoAction?.Invoke();
                undoStack.Push(action);
            }
        }

        /// <summary>
        /// Selects the specified UI element.
        /// </summary>
        /// <param name="element">The UI element to select.</param>
        private void SelectElement(UIElement element)
        {
            if (element is Polyline)
            {
                return;
            }

            if (selectedElement != null && selectedElement != element)
                DeselectElement();

            selectedElement = element;

            if (element is Rectangle rect)
            {
                return;
            }
            else
            {
                selectedRectangle = null;
            }
        }

        /// <summary>
        /// Deselects the currently selected UI element.
        /// </summary>
        private void DeselectElement()
        {
            if (selectedElement is Rectangle rect)
            {
                // Restore the color from the ActivationZone if available
                if (activationZones.TryGetValue(rect, out var zone))
                {
                    try
                    {
                        var brush = (Brush)new BrushConverter().ConvertFromString(zone.Color);
                        rect.Stroke = brush;
                    }
                    catch
                    {
                        rect.Stroke = Brushes.Red; // fallback color
                    }
                }
                else
                {
                    rect.Stroke = Brushes.Red; // fallback color if not in activationZones
                }
            }
            else if (selectedElement is Shape shape)
            {
                shape.Stroke = Brushes.Red; // fallback for other shapes
            }

            selectedElement = null;
            selectedRectangle = null;
        }

    }
}
