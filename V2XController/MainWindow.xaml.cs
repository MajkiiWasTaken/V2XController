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

/**********************************************************************************************************
 * V2X Controller - MainWindow.xaml.cs
 * Author: Michal Švrček
 * Version: 3.1.6
 * Description: Main window logic of the V2X Controller application. Handles map display, user interactions, 
 *              CAM/SRV message processing, and activation zone management. Provides a visual interface 
 *              for monitoring and controlling V2X communications in real-time. 
 *              Activation zone drawing, tram tracking, tram simulation, map panning/zooming, 
 *              CAM message playback, exporting data onto devices and a terminal for raw message display, 
 *              Protobuf Translator and more. Designed for use in traffic management and V2X testing scenarios. 
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/

//TODO: Polyline

/* Budoucí rozšíření: stav návěstidla / SPATEM + MAPEM // Navestidlo +- 49.844802, 18.280692
 * 
 * V současné implementaci jsou zprávy SPATEM/MAPEM zpracovávány na straně RSU, takže aplikace zatím nepracuje přímo se stavem návěstidel ani s geometrií křižovatky.
 * 
 * Do budoucna by bylo možné přidat podporu pro:
 * - příjem a dekódování SPATEM zpráv,
 * - zobrazení aktuálního stavu návěstidla,
 * - čas do změny fáze,
 * - mapování signálních skupin na konkrétní pruhy,
 * - příjem MAPEM geometrie křižovatky,
 * - vykreslení pruhů, stop čar a povolených směrů do mapy.
 * 
 * Smysl by to mělo hlavně pro vizualizaci křižovatek, diagnostiku RSU a zpětné přehrávání dopravních situací.
 *
 * FEATURE: Vizualizace stavů návěstidel z V2X zpráv
 *
 * Priorita: nízká / budoucí rozšíření
 *
 * Popis:
 * Aplikace by mohla v budoucnu zobrazovat stav návěstidla na základě SPATEM zpráv a napojit jej na geometrii křižovatky z MAPEM zpráv.
 *
 * Aktuální stav:
 * SPATEM/MAPEM zprávy řeší RSU, aplikace je zatím nepřijímá ani nedekóduje.
 *
 * Přínos:
 * Lepší diagnostika RSU, vizualizace signálních skupin, možnost replaye dopravních scén.
 */

namespace V2XController
{

    public partial class MainWindow : Window
    {
        // ===== all the variables and objects used in the application =====

        // Connection
        private SerialPort? serialPort;
        private readonly object _serialIoLock = new();

        private TcpClient? _tcpClient;
        private NetworkStream? _tcpStream;
        private StreamReader? _tcpReader;
        private StreamWriter? _tcpWriter;

        private CancellationTokenSource? _connectionCts;

        private enum ConnectionType
        {
            Serial,
            Ethernet
        }

        private ConnectionType _connectionType = ConnectionType.Serial;

        private readonly object _connectionWriteLock = new();

        // Heartbeat
        private DispatcherTimer? _heartbeatTimer;

        // Map
        private int zoom = 18;
        private double latitude = 49.842432;
        private double longitude = 18.276736;

        //Grid
        private const int TileSize = 256; //in px (map tile is 256x256 px)
        const int TileCount = 3;
        const int CanvasSize = TileSize * TileCount;
        const int TilePixelSize = TileSize;

        private int _currentTopLeftTileX;
        private int _currentTopLeftTileY;

        private readonly List<string> _recentLocalWrites = new();
        private readonly object _recentLocalWritesLock = new();
        private const int RecentLocalWritesMax = 16;

        private const int MaxCachedTiles = 2000;
        private readonly List<string> recordedCamMessages = new();
        private readonly List<string> recordedSrvMessages = new();
        private const int MaxRecordedCamMessages = 300;
        private const int MaxRecordedSrvMessages = 60;
        private readonly object _camBufferLock = new();
        private readonly object _srvBufferLock = new();

        private DispatcherTimer? _dumpTimer;

        //tram table
        public ObservableCollection<TramInfo> TramTable { get; set; }
        private Dictionary<string, DateTime> lastCamTimes = new();
        private Dictionary<string, DateTime> prevCamTimes = new();

        // Time zone
        TimeZoneInfo czechTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

        //points
        private TranslateTransform translate = new TranslateTransform();
        private ScaleTransform scale = new ScaleTransform(1, 1);
        private TransformGroup transformGroup = new TransformGroup();

        //drawing
        private List<MapRectangle> mapRectangles = new List<MapRectangle>();
        private MapRectangle? _currentMapRectangle;

        private enum RectangleDrawPhase
        {
            None,
            HeightDefinition,
            WidthDefinition
        }

        private RectangleDrawPhase rectPhase = RectangleDrawPhase.None;

        private Point rectFirstPoint;
        private Point rectSecondPoint;
        private Point rectWidthPoint;

        private Line? tempHeightLine;
        private Line? tempWidthLine;
        private Polygon? previewRect;
        private Ellipse? startPointEllipse;
        private Ellipse? secondPointEllipse;
        private Polyline? currentPolyline;
        private List<Point> polylinePoints = new List<Point>();
        private List<Ellipse> polylineVertexDots = new List<Ellipse>();
        private double _polylineZoneWidthMeters = 50.0;

        private List<Ellipse> _currentPolylineCircles = new List<Ellipse>();
        private List<System.Windows.Shapes.Path> _currentPolylineSegments = new List<System.Windows.Shapes.Path>();
        private Dictionary<Ellipse, (Polyline polyline, int pointIndex)> _polylineVertexMap = new Dictionary<Ellipse, (Polyline, int)>();
        private Dictionary<Polyline, List<System.Windows.Shapes.Path>> _polylineToSegments = new Dictionary<Polyline, List<System.Windows.Shapes.Path>>();
        private Dictionary<Ellipse, Ellipse> _polylineVertexToCircle = new Dictionary<Ellipse, Ellipse>();
        private Dictionary<Polyline, List<(double lat, double lon)>> _polylineGeoPoints = new Dictionary<Polyline, List<(double, double)>>();
        private bool _isDrawingPolyline = false;
        private Ellipse? _hoveredVertex = null;
        public ObservableCollection<ActivationZone> PolylineZonesCollection { get; set; }
        private readonly HashSet<ActivationZone> _polylineRows = new HashSet<ActivationZone>();
        private bool _suspendPolylineZoneLiveSort;
        private readonly Dictionary<Polyline, List<System.Windows.Shapes.Path>> _polylineVisualGroups = new();
        private readonly Dictionary<ActivationZone, System.Windows.Shapes.Path> _segmentToVisualPath = new();
        private readonly Dictionary<ActivationZone, List<Ellipse>> _segmentToCircles = new();

        private enum DrawingMode
        {
            None,
            Rectangle,
            Polyline,
            Point           //drawing selection
        }

        private DrawingMode currentDrawingMode = DrawingMode.None;
        private bool isDrawing = false;
        private Rectangle? currentRect = null;
        private Point startPoint;
        //private List<Railway> railwayLines;

        private List<Point> trailPoints = new List<Point>();

        private Brush _strokeBrush = Brushes.Red; //default brush color

        private List<MapPoint> points = new();
        private Polyline connectionLine = new Polyline
        {
            Stroke = Brushes.Red,
            StrokeThickness = 0.5
        };

        private Point? _rectangleStartPoint = null;
        private Rectangle? _currentRectangle = null;
        private TextBlock? _currentSizeLabel = null;
        private bool isSelectionMode = false;
        private Rectangle? selectedRectangle = null;

        private MapPoint?[]? drawnTrams = new MapPoint[2]; // 0 = X, 1 = C
        private int currentDrawnTramIndex = 0; // 0 = X, 1 = C
        private string[]? drawnTramIds = new[] { "0000009999", "0000001111" };
        private string[]? drawnTramNames = new[] { "tram-test1", "tram-test2" };
        private Brush[]? drawnTramColors = new[] { Brushes.Red, Brushes.Blue };
        private Polyline?[]? drawnTramTrails = new Polyline[2];
        private List<Point>?[]? drawnTramTrailPoints = new List<Point>[2] { new List<Point>(), new List<Point>() };
        private readonly Dictionary<string, CancellationTokenSource> vehicleTrailCleanupTokens = new();

        //moving objects (RMB)
        private UIElement? selectedElement = null;
        private Point mouseOffset;

        //rectangle dimensions textblock
        private TextBlock? dimensionTextBlock;

        private Stack<UndoRedoAction> undoStack = new();
        private Stack<UndoRedoAction> redoStack = new();

        //recording movement
        private bool isPlaying = false;
        private DateTime playbackStartTime;
        private DispatcherTimer playbackTimer;
        private TimeSpan playbackElapsedTime;
        private bool isRecording = false;
        private DateTime recordingStartTime;

        private Dictionary<string, MapPoint> activeVehicles = new();

        //panning
        private bool isPanning = false;
        private bool isMiddleMousePanning = false;

        private string[] controls = {
            "Zoom: MWheel",
            "Pan: Press MWhell (MB3)",
            "Draw: LMB",
            "Move objects: hold RMB",
            "Delete objects: hold RMB + Del",
            "Stop drawing: ESC",
            "Undo: Ctrl + Z",
            "Redo: Ctrl + Y",
            "Rotate selected rectangle clockwise: hold RMB + E",
            "Rotate selected rectangle counterclockwise: hold RMB + Q"
        };
        private string ControlsMessage => string.Join(" \n", controls);

        // CAM status
        int camOkCount = 0;
        int camErrorCount = 0;
        private V2XMessage? lastV2XMessage = null;

        // SRV status
        int srvOkCount = 0;
        int srvErrorCount = 0;
        private double? srvLatitude = null;
        private double? srvLongitude = null;

        private bool _savedRecording = false;

        //Activation zones
        private Dictionary<Rectangle, ActivationZone> activationZones = new();
        private Ellipse? radiusEllipse;
        public ObservableCollection<ActivationZone> ActivationZonesCollection { get; set; } = new();

        //dict for last cam updates 
        Dictionary<string, DateTime> lastCamUpdates = new Dictionary<string, DateTime>();
        private DispatcherTimer? cleanupTimer;

        //colors for tram points
        private readonly List<Brush> vehicleColors = new List<Brush>
        {
            Brushes.DarkViolet,
            Brushes.Blue,
            Brushes.DarkGreen,
            Brushes.Purple,
            Brushes.Magenta,
            Brushes.Brown,
            Brushes.Navy,
            Brushes.Maroon
        };
        private readonly Dictionary<string, Brush> vehicleColorMap = new Dictionary<string, Brush>();

        //timer for cam updates (seconds)
        private DispatcherTimer? camTimer;

        // xml files
        private string loadedFileName;

        private bool isDirty = false; // to track if the map has been modified

        private bool isUpdatingActivationZone = false;

        private const double playbackSpeedFactor = 1;
        private TimeSpan playbackMaxTime = TimeSpan.Zero;
        private bool isSliderDragging = false;
        private bool wasPlayingBeforeSliderDrag = false;

        private bool _suppressTramTextChanged;

        private double _manualCamSpeedKmh = 0.0;

        private DispatcherTimer _srvTimer;

        private bool _isConnected = false;
        private bool _playbackLoaded = false;

        private double?[] drawnTramLat = new double?[2];
        private double?[] drawnTramLon = new double?[2];
        private List<(double lat, double lon)>[] drawnTramTrailGeoPoints = new List<(double lat, double lon)>[2]
        {
             new List<(double lat, double lon)>(),
             new List<(double lat, double lon)>()
        };

        private List<TimeSpan> _keyframes = new();

        private int _playbackIndex = 0;

        private readonly Dictionary<ActivationZone, DispatcherTimer> _zoneDeactivateTimers = new();

        // debounce zoom preview
        private DispatcherTimer _zoomDebounceTimer;
        private int _pendingZoom;
        private Point _lastWheelPos;

        // playback helpers
        private Dictionary<string, double> _playbackSpeedByIdAndTs = new(); // key = $"{vehId}|{ts.Ticks}"

        private ActivationZone? _pendingNewZone;

        private string _lastReplayFile;

        private bool _isPlaybackSessionActive = false;
        private readonly List<(TimeSpan ts, int intersectionId, TramSignalDirection direction)> _replaySignalFrames = new();

        private bool _timeshiftEnabled = false;       // recording continuously after connect
        private bool _timeshiftPaused = false;        // pause suppresses live rendering but keeps buffering
        private bool _suppressLiveRender = false;     // gate for HandleV2XMessage during timeshift pause
        private DateTime _timeshiftStartUtc;          // session start (after Connect)
        private DispatcherTimer _timeshiftUiTimer;    // updates slider while live
        private DateTime? _markInUtc = null;          // export range start
        private DateTime? _markOutUtc = null;

        private List<string>? recordedManualCamMessages = new();     // manual: only simulated (drawn) CAMs while recording

        private bool _isTimeshiftPlaybackActive;           // catch-up playback running
        private CancellationTokenSource _timeshiftPlaybackCts;
        private bool _timeshiftFollowLive;                 // auto-follow live edge when true

        private bool isReplaySliderDragging = false;
        private bool wasPlayingBeforeReplayDrag = false;

        private readonly Dictionary<Rectangle, ScaleTransform> _rectHighlightScales = new();
        private Rectangle? _highlightedRect;
        private Brush? _highlightedRectOldBrush;
        private double _highlightedRectOldThickness;

        private DateTime? _replayStartUtc;
        private DateTime? _replayEndUtc;

        private readonly Dictionary<string, MapPoint> _replayVehicles = new();
        private readonly Dictionary<string, List<MovementFrame>> _replayFrames = new();

        private readonly Dictionary<string, Rectangle> _vehicleBoxes = new();   // live CAM boxes
        private readonly Dictionary<string, Rectangle> _replayBoxes = new();    // replay boxes
        private readonly Dictionary<string, double> _playbackHeadingByIdAndTs = new(); // key: $"{id}|{ts.Ticks}"

        private readonly Dictionary<string, (double lat, double lon)> _lastLatLon = new();
        private readonly Dictionary<string, double> _lastHeadingLive = new();

        private readonly Dictionary<string, List<(TimeSpan ts, double lat, double lon)>> _replayGeoFrames = new();

        // add near other small constants/fields inside MainWindow
        private static readonly TimeSpan ReplayVisibilityTimeout = TimeSpan.FromSeconds(23);
        private int _maxTrailLength = 6; // max number of segments (points = segments + 1)

        // SRV replay containers (near other replay fields)
        private readonly Dictionary<string, List<(TimeSpan ts, double lat, double lon)>> _replaySrvFramesById = new();
        private readonly Dictionary<string, MapPoint> _replaySrvPoints = new();

        private static readonly TimeSpan TableRowTimeout = TimeSpan.FromSeconds(60);

        private (double lat, double lon)? _localAltitudeFor;
        private double? _localAltitudeMeters;

        // Replay: store per-frame altitude (key = $"{id}|{ts.Ticks}")
        private readonly Dictionary<string, double> _playbackAltitudeByIdAndTs = new();

        public IReadOnlyList<int> MainZoneOptions { get; } = new[] { 0, 1, 2, 3 };
        public IReadOnlyList<int> SubZoneOptions { get; } = new[] { 0, 1, 2, 3, 4 };

        private bool _suspendZoneLiveSort;

        private readonly Dictionary<Rectangle, ActivationZone> switchZones = new();
        public ObservableCollection<ActivationZone> SwitchZonesCollection { get; } = new();

        // Switch-specific options: Main 0..4, Sub 0..6
        public IReadOnlyList<int> MainZoneOptionsSwitch { get; } = new[] { 0, 1, 2, 3, 4 };
        public IReadOnlyList<int> SubZoneOptionsSwitch { get; } = new[] { 0, 1, 2, 3, 4, 5, 6 };

        private bool _suspendSwitchZoneLiveSort;
        private ActivationZone? _pendingNewSwitchZone;

        private bool _drawToSwitchZones;

        private readonly HashSet<ActivationZone> _switchRows = new();

        // helper: current drawing/adding mode comes from radio buttons
        private bool IsSwitchMode() => SwitchRadio?.IsChecked == true;
        private static bool IsSwitchZone(ActivationZone z) =>
            z?.IsSwitchZone ?? false;

        public List<Stop> stops = new List<Stop>();

        private readonly List<(string text, Brush color)> _terminalBuffer = new();
        private readonly Dictionary<string, Stop?> _vehCurrentStop = new(); // current stop per vehicle (null = none)
        private const double StopRadiusMeters = 25.0;
        private const int MaxBlocks = 2000;

        private static readonly HttpClient s_httpClient = CreateSharedHttpClient();
        private static HttpClient CreateSharedHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("V2XController/1.0 (Michal.Svrcek@hrosistavby.cz)");
            return client;
        }

        private readonly ConcurrentDictionary<(int z, int x, int y), BitmapSource> _tileCache = new();

        private static int WrapTileX(int x, int zoom)
        {
            int n = 1 << zoom;
            int r = x % n;
            return r < 0 ? r + n : r;
        }

        private static int ClampTileY(int y, int zoom)
        {
            int n = 1 << zoom;
            return Math.Clamp(y, 0, n - 1);
        }

        private CancellationTokenSource? _tileCts;
        private readonly SemaphoreSlim _tileSemaphore = new SemaphoreSlim(4); //tile semaphore limit

        private double visualOffsetX = 0;
        private double visualOffsetY = 0;

        private int lastTileX, lastTileY;
        private bool isTileLoadPending = false;

        private double tileOffsetX = 0;
        private double tileOffsetY = 0;

        private double mapOffsetX = 0;
        private double mapOffsetY = 0;

        private int cameraX = 0;  // Camera position in world pixels
        private int cameraY = 0;
        private Point lastMousePos;
        private bool isDragging = false;

        private readonly Dictionary<string, double> _playbackAccuracyByIdAndTs = new();

        private bool _suppressFilterTramSelectionChanged = false;
        private readonly HashSet<string> _knownLiveShortIds = new();

        private readonly Dictionary<string, double?> _lastLiveAccuracyById = new();
        private readonly Dictionary<string, TextBlock> _liveAccuracyTextById = new();

        private readonly Dictionary<Polyline, List<ActivationZone>> _polylineToSegmentZones = new();
        private readonly List<PolylineData> _drawnPolylines = new List<PolylineData>();
        private List<(double lat, double lon)> _currentPolylineCircleGeoPoints = new List<(double, double)>();
        private int _polylineCommittedPointsCount = 0;
        private readonly Dictionary<Polyline, List<System.Windows.Shapes.Line>> _polylineDirectionArrows = new Dictionary<Polyline, List<System.Windows.Shapes.Line>>();

        private DispatcherTimer? _protobufTestTimer;
        private bool _protobufTestRunning = false;
        private double _protobufTestLatOffset = 0.0;
        private bool _protobufTestDirection = true; // true = increasing, false = decreasing
        private string _protobufTestDecoded = ""; // Uložená dekódovaná zpráva

        private readonly Dictionary<Rectangle, Polygon> _zoneArrows = new Dictionary<Rectangle, Polygon>();
        private readonly Dictionary<string, HashSet<ActivationZone>> _vehicleActiveZones = new();

        private bool _suppressModeSwitch;

        private Transform? _highlightedRectOldTransform;

        private readonly Dictionary<(string vehicleId, ActivationZone zone), bool> _vehicleZoneValidEntry = new();

        private Border? _loadingOverlay;
        private ProgressBar? _loadingProgressBar;

        private ProtobufWindow? _protobufWindow;

        private class UndoRedoAction
        {
            public Action? UndoAction { get; set; }
            public Action? RedoAction { get; set; }
        }

        private enum TramSignalSide
        {
            Left,
            Right
        }

        private class TramSignalInstance
        {
            public int IntersectionId { get; set; }
            public string Name { get; set; } = "";
            public string Title { get; set; } = "";
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public double RotationDeg { get; set; }
            public TramSignalSide Side { get; set; }
            public UserControl? Control { get; set; }
            public TextBlock? SignalLabel { get; set; }
        }

        private readonly List<TramSignalInstance> _tramSignals = new()
        {
            new TramSignalInstance
            {
                IntersectionId = 640,
                Name = "640-left",
                Title = "V640",
                Latitude = 49.844671,
                Longitude = 18.280884,
                RotationDeg = 346.0,
                Side = TramSignalSide.Left
            },
            new TramSignalInstance
            {
                IntersectionId = 677,
                Name = "667-left",
                Title = "V667",
                Latitude = 49.844863,
                Longitude = 18.280103,
                RotationDeg = 75.0,
                Side = TramSignalSide.Left
            },

            new TramSignalInstance
            {
                IntersectionId = 663,
                Name = "663-right",
                Title = "V633",
                Latitude = 49.845336,
                Longitude = 18.280349,
                RotationDeg = 175.0,
                Side = TramSignalSide.Right
            }
        };

        // ===== Main window constructor =====

        /// <summary>
        /// Main window constructor. Initializes UI, sets up event handlers
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            var assembly = Assembly.GetExecutingAssembly();
            var fileVersion = FileVersionInfo
                .GetVersionInfo(assembly.Location)
                .FileVersion;

            BuildVersionLabel.Content = $"Build version: {fileVersion}";

            tempHeightLine = new Line();
            tempWidthLine = new Line();
            previewRect = new Polygon();
            startPointEllipse = new Ellipse();
            secondPointEllipse = new Ellipse();
            currentPolyline = null;
            playbackTimer = null;
            radiusEllipse = new Ellipse();
            loadedFileName = string.Empty;
            _srvTimer = null;
            _zoomDebounceTimer = null;
            _pendingNewZone = null;
            _lastReplayFile = string.Empty;
            _timeshiftUiTimer = new DispatcherTimer();
            _timeshiftPlaybackCts = new CancellationTokenSource();
            _highlightedRect = new Rectangle();
            _highlightedRectOldBrush = Brushes.Transparent;
            _pendingNewSwitchZone = null;

#if DEBUG
            AllocConsole();
            Console.WriteLine("[DEBUG] Allocating console...");
#endif
            Console.WriteLine("[INIT] MainWindow initialized");
            this.WindowStyle = WindowStyle.SingleBorderWindow;
            this.ResizeMode = ResizeMode.CanMinimize;

            LoadAvailableComPorts();
            Console.WriteLine("[APP] Application started.");
            Console.WriteLine($"[PARAMS] Zoom: {zoom}, Lat: {latitude:F6}, Lon: {longitude:F6}");
            // main data context
            this.DataContext = this;

            _heartbeatTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };

            _heartbeatTimer.Tick += (s, e) =>
            {
                var connStatus = _isConnected
                    ? $"Connected ({GetConnectionDisplayName()})"
                    : "Disconnected";
                var playStatus = isPlaying ? "Playing" : (isRecording ? "Recording" : "Idle");

                Console.WriteLine($"[HEARTBEAT] " +
                                 $"Zoom: {zoom} | " +
                                 $"Conn: {connStatus} | " +
                                 $"Status: {playStatus} | " +
                                 $"Zones: {ActivationZonesCollection.Count}");
                Console.WriteLine(
                    $"MEMCHECK | " +
                    $"Canvas={TileCanvas.Children.Count}, " +
                    $"activeVehicles={activeVehicles.Count}, " +
                    $"vehicleColorMap={vehicleColorMap.Count}, " +
                    $"lastLatLon={_lastLatLon.Count}, " +
                    $"lastHeading={_lastHeadingLive.Count}, " +
                    $"boxes={_vehicleBoxes.Count}, " +
                    $"accTexts={_liveAccuracyTextById.Count}, " +
                    $"cleanupTokens={vehicleTrailCleanupTokens.Count}, " +
                    $"camBuf={recordedCamMessages.Count}, " +
                    $"srvBuf={recordedSrvMessages.Count}"
                );
            };

            _heartbeatTimer.Start();
            Console.WriteLine("[HEARTBEAT] Started (interval: 3 seconds)");

            if (PolylineWidthTB != null)
            {
                PolylineWidthTB.TextChanged += PolylineWidthTB_TextChanged;
            }

            // INIT TramTable BEFORE any timers or bindings use it
            TramTable = new ObservableCollection<TramInfo>();

            InitSwitchZonesUi();

            // timer for cleanup of old vehicles
            StartCleanupTimer();

            _dumpTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(60)
            };

            _dumpTimer.Tick += async (s, e) =>
            {
                await DumpCamBufferAsync("timer");
                await DumpSrvBufferAsync("timer");
            };

            _dumpTimer.Start();

            // cam timer 
            camTimer = new DispatcherTimer();
            camTimer.Interval = TimeSpan.FromSeconds(1);
            camTimer.Tick += (s, e) =>
            {
                // In classic replay, seconds are driven by the replay timeline
                if (_playbackLoaded) return;
                if (TramTable == null) return;

                var now = DateTime.Now;
                var toRemove = new List<TramInfo>();

                foreach (var tram in TramTable)
                {
                    if (!tram.LastMessageTimestamp.HasValue) continue;

                    var secs = (int)Math.Floor((now - tram.LastMessageTimestamp.Value).TotalSeconds);
                    if (secs > TableRowTimeout.TotalSeconds)
                    {
                        toRemove.Add(tram); // delete row if over 60 s
                        continue;
                    }

                    tram.SecondsSinceLastCam = Math.Max(0, secs);
                }

                foreach (var row in toRemove)
                    TramTable.Remove(row);
            };
            camTimer.Start();

            Loaded += async (s, e) =>
            {
                _ = EnsureLocalAreaAltitudeAsync(force: true);

                Keyboard.Focus(this);
                EnsureDefaultRadiusSelection();
                DrawRadiusCircle();
                UpdateUiEnabledState();

            };

            ActivationZonesDataGrid.ItemsSource = ActivationZonesCollection;

            foreach (var column in ActivationZonesDataGrid.Columns)
            {
                if (column is DataGridComboBoxColumn comboColumn &&
                    comboColumn.Header != null &&
                    comboColumn.Header.ToString() == "Color")
                {
                    comboColumn.ItemsSource = new List<string>
                    {
                        "Red",
                        "Green",
                        "Blue",
                        "Yellow",
                        "Orange",
                        "Cyan",
                        "Magenta",
                        "Black"
                    };
                }
            }

            if (TramBox != null)
                TramBox.SelectionChanged += TramBox_SelectionChanged;
            if (FilterTram != null)
            {
                FilterTram.SelectionChanged += FilterTram_SelectionChanged;
                PopulateLiveTramBoxFromActiveVehicles();
            }

            //trams
            Tram1TB.Text = "9999";
            Tram2TB.Text = "1111";
            drawnTramIds[0] = "0000009999";
            drawnTramIds[1] = "0000001111";

            Tram1TB.MaxLength = 4;
            Tram2TB.MaxLength = 4;

            LatitudeBox.Text = Mapsettings.Latitude.ToString("F6", CultureInfo.InvariantCulture);
            LongitudeBox.Text = Mapsettings.Longitude.ToString("F6", CultureInfo.InvariantCulture);

            var zonesView = CollectionViewSource.GetDefaultView(ActivationZonesCollection);
            zonesView.SortDescriptions.Clear();
            zonesView.SortDescriptions.Add(new SortDescription(nameof(ActivationZone.MainZone), ListSortDirection.Ascending));
            zonesView.SortDescriptions.Add(new SortDescription(nameof(ActivationZone.SubZone), ListSortDirection.Ascending));
            if (zonesView is ICollectionViewLiveShaping live)
            {
                live.IsLiveSorting = true;
                live.LiveSortingProperties.Add(nameof(ActivationZone.MainZone));
                live.LiveSortingProperties.Add(nameof(ActivationZone.SubZone));
            }

            //moving the map
            transformGroup.Children.Add(scale);
            transformGroup.Children.Add(translate);
            TileCanvas.RenderTransform = transformGroup;

            //loading map tiles on the grid
            var (centerX, centerY) = LatLonToTileXY(latitude, longitude, zoom);
            cameraX = (centerX - TileCount / 2) * TileSize;
            cameraY = (centerY - TileCount / 2) * TileSize;

            // Load map synchronously on startup (blocking to ensure it's visible immediately)
            Loaded += async (s, e) =>
            {
                // Initialize camera and load tiles FIRST
                await LoadTilesInitialAsync();
                Console.WriteLine("[TILES] Tiles loaded.");
                _ = EnsureLocalAreaAltitudeAsync(force: true);
                EnsureTramSignals();

                Keyboard.Focus(this);
                EnsureDefaultRadiusSelection();
                DrawRadiusCircle();
                UpdateUiEnabledState();

                // Load OSM tram stops and draw them
                try
                {
                    stops = await LoadStopsFromOSM();
                    Console.WriteLine($"[STOPS] Loaded {stops.Count} tram stops from OSM.");
                    //DrawStopsOnCanvasSafe();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERR] Failed to load stops: {ex.Message}");
                }
            };

            //Mouse wheel events for zooming
            TileCanvas.MouseWheel += TileCanvas_MouseWheel;
            this.MouseWheel += Window_MouseWheel;

            //records table (not in the app anymore)

            ActivationZonesCollection.CollectionChanged += ActivationZonesCollection_CollectionChanged;

            // CSV loading can be triggered explicitly when needed.

            //RMB
            TileCanvas.MouseRightButtonDown += TileCanvas_MouseRightButtonDown;
            TileCanvas.MouseMove += TileCanvas_MouseMoveForDrag;
            TileCanvas.MouseRightButtonUp += TileCanvas_MouseRightButtonUp;

            //MB3
            TileCanvas.PreviewMouseDown += TileCanvas_PreviewMouseDown;
            TileCanvas.MouseMove += TileCanvas_MouseMove_MiddlePan;
            TileCanvas.PreviewMouseUp += TileCanvas_PreviewMouseUp;

            ActivationZonesDataGrid.BeginningEdit += ActivationZonesDataGrid_BeginningEdit;
            ActivationZonesDataGrid.RowEditEnding += ActivationZonesDataGrid_RowEditEnding;

            PolylineZonesCollection = new ObservableCollection<ActivationZone>();
            PolylineZonesCollection.CollectionChanged += PolylineZonesCollection_CollectionChanged;

            //point connection line
            TileCanvas.Children.Add(connectionLine);

            this.PreviewKeyDown += Window_PreviewKeyDown;

            //rectangle dimensions under the mouse
            dimensionTextBlock = new TextBlock
            {
                Foreground = Brushes.Black,
                Background = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Visibility = Visibility.Collapsed
            };

            //Z index of all elements to prevent them from overlapping with map grid
            Panel.SetZIndex(dimensionTextBlock, int.MaxValue);
            TileCanvas.Children.Add(dimensionTextBlock);

            ActivationZonesDataGrid.SelectionChanged += ActivationZonesDataGrid_SelectionChanged;
            ActivationZonesDataGrid.CellEditEnding += ActivationZonesDataGrid_CellEditEnding;
            ActivationZonesDataGrid.CurrentCellChanged += ActivationZonesDataGrid_CurrentCellChanged;

            this.Closing += MainWindow_Closing;
        }

        private string GetConnectionDisplayName()
        {
            if (_connectionType == ConnectionType.Serial)
            {
                return serialPort?.PortName ?? "COM";
            }

            string host = EthernetAddressTB?.Text?.Trim() ?? "?";
            string port = EthernetPortTB?.Text?.Trim() ?? "?";

            return $"{host}:{port}";
        }

        /// <summary>
        /// Closes main window and performs necessary cleanup.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            Console.WriteLine("[APP] Closing application...");

            if (_heartbeatTimer != null)
            {
                _heartbeatTimer.Stop();
                Console.WriteLine("[HEARTBEAT] Stopped");
            }

            if (isDirty)
            {
                var result = MessageBox.Show(
                    "You have unsaved changes. Do you want to save before closing?",
                    "Unsaved changes",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                else if (result == MessageBoxResult.Yes)
                {
                    SaveToXML_Click(null, null);

                    if (isDirty)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }

            StopTimer(_heartbeatTimer);
            StopTimer(camTimer);
            StopTimer(cleanupTimer, CleanupOldVehicles);
            StopTimer(_srvTimer);
            StopTimer(_timeshiftUiTimer);

            _tileCts?.Cancel();
            _tileCts?.Dispose();
            _tileCts = null;

            _timeshiftPlaybackCts?.Cancel();
            _timeshiftPlaybackCts?.Dispose();

            _ = DumpCamBufferAsync("closing");
            _ = DumpSrvBufferAsync("closing");

            foreach (var cts in vehicleTrailCleanupTokens.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }

            vehicleTrailCleanupTokens.Clear();
        }

        private void StopTimer(DispatcherTimer? timer, EventHandler? handler = null)
        {
            if (timer == null)
                return;

            timer.Stop();

            if (handler != null)
                timer.Tick -= handler;
        }

        /// <summary>
        /// Loads available COM ports into the dropdown. Called on startup and when refreshing the list.
        /// </summary>
        private void LoadAvailableComPorts()
        {
            ComPortsComboBox.Items.Clear();

            using (var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    string displayName = obj["Name"]?.ToString();

                    if (string.IsNullOrEmpty(displayName))
                        continue;

                    var match = System.Text.RegularExpressions.Regex.Match(
                        displayName,
                        @"\(COM\d+\)");

                    if (!match.Success)
                        continue;

                    string portName = match.Value.Trim('(', ')');

                    ComPortsComboBox.Items.Add(new ComPortInfo
                    {
                        PortName = portName,
                        DisplayName = displayName
                    });
                }
            }

            if (ComPortsComboBox.Items.Count > 0)
                ComPortsComboBox.SelectedIndex = 0;
        }

        /// <summary>
        /// Ensures the default radius selection is set in the RadiusComboBox.
        /// </summary>
        private void EnsureDefaultRadiusSelection()
        {
            if (RadiusComboBox == null) return;

            // Find "250" item or create it
            var item = RadiusComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content?.ToString(), "250", StringComparison.OrdinalIgnoreCase));

            if (item == null)
            {
                item = new ComboBoxItem { Content = "250" };
                RadiusComboBox.Items.Insert(0, item);
            }

            RadiusComboBox.SelectedItem = item;
        }

        private async Task DumpCamBufferAsync(string reason)
        {
            List<string> batch;

            lock (_camBufferLock)
            {
                if (recordedCamMessages.Count == 0)
                    return;

                batch = recordedCamMessages.ToList();
                recordedCamMessages.Clear();
            }

            await DumpMessagesAsync("CAM", batch, reason);
        }

        private async Task DumpSrvBufferAsync(string reason)
        {
            List<string> batch;

            lock (_srvBufferLock)
            {
                if (recordedSrvMessages.Count == 0)
                    return;

                batch = recordedSrvMessages.ToList();
                recordedSrvMessages.Clear();
            }

            await DumpMessagesAsync("SRV", batch, reason);
        }

        private async Task DumpMessagesAsync(string type, List<string> batch, string reason)
        {
            try
            {
                string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "message_dumps");
                Directory.CreateDirectory(dir);

                string file = System.IO.Path.Combine(
                    dir,
                    $"{type}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{reason}_{batch.Count}.log"
                );

                await System.IO.File.WriteAllLinesAsync(file, batch);

                Console.WriteLine($"[{type} DUMP] {batch.Count} messages dumped because of {reason}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{type} DUMP ERR] {ex.Message}");
            }
        }

    }
}
