using Google.Protobuf.WellKnownTypes;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
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
using Windows.UI.Composition;


/**********************************************************************************************************
 * V2X Controller - MainWindow.xaml.cs
 * Author: Michal Švrček
 * Version: 3.1.2
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
        //||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        // all the variables and objects used in the application

        //!!!!!!PORT and BAUDRATE!!!!!!!!!
        private SerialPort serialPort = new SerialPort("COM10", 57600);
        private readonly object _serialIoLock = new();

        //HEARTBEAT
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

        //tram table
        public ObservableCollection<TramInfo> TramTable { get; set; }
        private Dictionary<string, DateTime> lastCamTimes = new();
        private Dictionary<string, DateTime> prevCamTimes = new();

        //TIME ZONE
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

        private List<string> recordedCamMessages = new();

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

        //CAM STATUSES
        int camOkCount = 0;
        int camErrorCount = 0;
        private V2XMessage? lastV2XMessage = null;


        //SRV STATUSES
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

        // u ostatních fieldů
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

        // near other fields controlling playback state
        private string _lastReplayFile;

        // near other fields controlling playback state
        private bool _isPlaybackSessionActive = false;
        private readonly List<(TimeSpan ts, int intersectionId, TramSignalDirection direction)> _replaySignalFrames = new();

        // near other fields (playback/recording)
        private bool _timeshiftEnabled = false;       // recording continuously after connect
        private bool _timeshiftPaused = false;        // pause suppresses live rendering but keeps buffering
        private bool _suppressLiveRender = false;     // gate for HandleV2XMessage during timeshift pause
        private DateTime _timeshiftStartUtc;          // session start (after Connect)
        private DispatcherTimer _timeshiftUiTimer;    // updates slider while live
        private DateTime? _markInUtc = null;          // export range start
        private DateTime? _markOutUtc = null;

        // global buffers
        private List<string>? recordedManualCamMessages = new();     // manual: only simulated (drawn) CAMs while recording

        // near other timeshift fields
        private bool _isTimeshiftPlaybackActive;           // catch-up playback running
        private CancellationTokenSource _timeshiftPlaybackCts;
        private bool _timeshiftFollowLive;                 // auto-follow live edge when true


        private bool isReplaySliderDragging = false;
        private bool wasPlayingBeforeReplayDrag = false;

        private readonly Dictionary<Rectangle, ScaleTransform> _rectHighlightScales = new();
        private Rectangle? _highlightedRect;
        private Brush? _highlightedRectOldBrush;
        private double _highlightedRectOldThickness;

        // near other playback state fields
        private DateTime? _replayStartUtc;
        private DateTime? _replayEndUtc;

        // near other fields (add replay containers)
        private readonly Dictionary<string, MapPoint> _replayVehicles = new();
        private readonly Dictionary<string, List<MovementFrame>> _replayFrames = new();

        // near other fields (add below _replayFrames)
        private readonly Dictionary<string, Rectangle> _vehicleBoxes = new();   // live CAM boxes
        private readonly Dictionary<string, Rectangle> _replayBoxes = new();    // replay boxes
        private readonly Dictionary<string, double> _playbackHeadingByIdAndTs = new(); // key: $"{id}|{ts.Ticks}"


        // Store last-known geo for live CAMs
        private readonly Dictionary<string, (double lat, double lon)> _lastLatLon = new();
        private readonly Dictionary<string, double> _lastHeadingLive = new();

        private readonly Dictionary<string, List<(TimeSpan ts, double lat, double lon)>> _replayGeoFrames = new();


        // add near other small constants/fields inside MainWindow
        private static readonly TimeSpan ReplayVisibilityTimeout = TimeSpan.FromSeconds(23);
        private int _maxTrailLength = 6; // max number of segments (points = segments + 1)

        // near other buffers
        private List<string> recordedSrvMessages = new();

        // SRV replay containers (near other replay fields)
        private readonly Dictionary<string, List<(TimeSpan ts, double lat, double lon)>> _replaySrvFramesById = new();
        private readonly Dictionary<string, MapPoint> _replaySrvPoints = new();

        // near other small constants/fields
        private static readonly TimeSpan TableRowTimeout = TimeSpan.FromSeconds(60);

        private (double lat, double lon)? _localAltitudeFor;
        private double? _localAltitudeMeters;

        // Replay: store per-frame altitude (key = $"{id}|{ts.Ticks}")
        private readonly Dictionary<string, double> _playbackAltitudeByIdAndTs = new();

        public IReadOnlyList<int> MainZoneOptions { get; } = new[] { 0, 1, 2, 3 };
        public IReadOnlyList<int> SubZoneOptions { get; } = new[] { 0, 1, 2, 3, 4 };

        // near other fields
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





        //||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //Main window constructor


        /// <summary>
        /// Main window constructor. Initializes UI, sets up event handlers
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
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
            _pendingNewZone = new ActivationZone();
            _lastReplayFile = string.Empty;
            _timeshiftUiTimer = new DispatcherTimer();
            _timeshiftPlaybackCts = new CancellationTokenSource();
            _highlightedRect = new Rectangle();
            _highlightedRectOldBrush = Brushes.Transparent;
            _pendingNewSwitchZone = new ActivationZone();

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
                var connStatus = _isConnected ? $"Connected ({serialPort?.PortName})" : "Disconnected";
                var playStatus = isPlaying ? "Playing" : (isRecording ? "Recording" : "Idle");

                Console.WriteLine($"[HEARTBEAT] " +
                                 $"Zoom: {zoom} | " +
                                 $"Conn: {connStatus} | " +
                                 $"Status: {playStatus} | " +
                                 $"Zones: {ActivationZonesCollection.Count} | " +
                                 $"Vehicles: {activeVehicles.Count}");
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

                // Load OSM tram stops and draw them
                try
                {
                    stops = await LoadStopsFromOSM();
                    Console.WriteLine($"[STOPS] Loaded {stops.Count} tram stops from OSM.");
                    DrawStopsOnCanvasSafe();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[STOPS] Failed to load stops: {ex.Message}");
                }
            };

            ActiveTramsDataGrid.ItemsSource = TramTable;
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
                    DrawStopsOnCanvasSafe();
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

            //csv loading
            var filePath = "export.csv" ?? throw new InvalidOperationException("CSV file path is not specified.");
            //LoadFromCSV(filePath);

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


            //check if the points are still active (65 seconds)
            var cleanupTimer = new DispatcherTimer();
            cleanupTimer.Interval = TimeSpan.FromSeconds(5);
            cleanupTimer.Tick += CleanupOldVehicles;
            cleanupTimer.Start();

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
        }


        /// <summary>
        /// Loads available COM ports into the dropdown. Called on startup and when refreshing the list.
        /// </summary>
        private void LoadAvailableComPorts()
        {
            ComPortsComboBox.Items.Clear();
            var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
            foreach (var port in ports)
                ComPortsComboBox.Items.Add(port);

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


        //||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        // METHODS FOR THE MAP

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
                        DrawStopsOnCanvasSafe();
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

            Console.WriteLine($"[ZOOM] Wheel event: {zoom} → {_pendingZoom}, scale: {previewScale:F2}");

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
            Console.WriteLine($"[ZOOM] Timer fired: processing zoom {oldZoom} → {_pendingZoom}");

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

                Console.WriteLine($"[ZOOM] Complete: {oldZoom} → {zoom}");
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

                    if (isActiveDrawing && hasContinuation && hasTableSegments)
                    {
                        // Pokračování v existující polyline - použít variable widths
                        Console.WriteLine($"[UPDATE POS] Rebuilding with VARIABLE widths (continuation, committed={_polylineCommittedPointsCount})");
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
                DrawStopsOnCanvasSafe();
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

            // ✅ Move zone arrows - changed from simple move to recalculation
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

        //||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        // METHODS FOR DRAWING AND MOUSE EVENTS

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
                    // Detekce zda pokračujeme v existující polyline
                    if (_polylineCommittedPointsCount > 0 && _polylineToSegmentZones.ContainsKey(currentPolyline))
                    {
                        // Pokračování - použít variable widths
                        Console.WriteLine($"[POLYLINE] Rebuilding with VARIABLE widths (committed: {_polylineCommittedPointsCount})");
                        RebuildPolylineZoneWithVariableWidths(currentPolyline, polylinePoints);
                    }
                    else
                    {
                        // Nová polyline - uniform width
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
                _polylineToSegments[polyline].Add(groupPath);
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

                // Rebuild zone with merged geometry
                double mpp = MetersPerPixel(latitude, zoom);
                double halfWidthPx = (_polylineZoneWidthMeters / 2.0) / mpp;
                RebuildPolylineZone(polyline, polyline.Points.ToList(), halfWidthPx);

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
                Console.WriteLine($"[POLYLINE] Vertex {i + 1}: Canvas({canvasPoint.X:F2}, {canvasPoint.Y:F2}) → Geo({lat:F7}, {lon:F7})");
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

                    // SubZone přesáhl 4 → další MainZone
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

                            Console.WriteLine($"[DRAG] Segment {prevSegmentIndex}: Len={prevSegment.Height:F2}m, Az={prevSegment.Azimuth}°, Width={prevSegment.Width:F2}m");
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

                            Console.WriteLine($"[DRAG] Segment {nextSegmentIndex}: Len={nextSegment.Height:F2}m, Az={nextSegment.Azimuth}°, Width={nextSegment.Width:F2}m");
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
            // Remove old shapes
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

            if (points.Count < 2) return;

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
                        Console.WriteLine($"[REBUILD VAR] ✓ Merged group: seg {startIdx}-{i - 1} ({i - startIdx} segments), RGB=({currentColor.R},{currentColor.G},{currentColor.B}), width={currentWidth:F2}m");

                        // Začátek nové skupiny
                        currentColor = virtualSegments[i].color;
                        currentColorStr = virtualSegments[i].colorStr;
                        currentWidth = virtualSegments[i].widthMeters;
                        startIdx = i;
                    }
                    else
                    {
                        Console.WriteLine($"[REBUILD VAR]   → Seg {i} merged (same color+width)");
                    }
                }

                // Poslední skupina
                groups.Add((startIdx, virtualSegments.Count - 1, currentColor, currentColorStr, currentWidth));
                Console.WriteLine($"[REBUILD VAR] ✓ Merged group: seg {startIdx}-{virtualSegments.Count - 1} ({virtualSegments.Count - startIdx} segments), RGB=({currentColor.R},{currentColor.G},{currentColor.B}), width={currentWidth:F2}m");
            }

            Console.WriteLine($"[REBUILD VAR] Total: {virtualSegments.Count} segments → {groups.Count} merged groups");

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
                _polylineToSegments[polyline].Add(groupPath);
            }

            // Center lines
            foreach (var group in groups)
            {
                var groupBrush = new SolidColorBrush(group.color);

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
                Panel.SetZIndex(centerLinePath, 100000);
                _polylineToSegments[polyline].Add(centerLinePath);
            }

            Console.WriteLine($"[REBUILD VAR] ✓ Created {_polylineToSegments[polyline].Count} Path elements");
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
                    Console.WriteLine($"[POLYLINE] Restoring previously finalized polyline with {_polylineCommittedPointsCount} committed points");

                    // Remove only the NEW dots and circles added after continuation
                    for (int i = _polylineCommittedPointsCount; i < polylineVertexDots.Count; i++)
                    {
                        var dot = polylineVertexDots[i];
                        if (TileCanvas.Children.Contains(dot))
                            TileCanvas.Children.Remove(dot);

                        _polylineVertexMap.Remove(dot);

                        if (_polylineVertexToCircle.TryGetValue(dot, out var circle))
                        {
                            if (TileCanvas.Children.Contains(circle))
                                TileCanvas.Children.Remove(circle);
                            _polylineVertexToCircle.Remove(dot);
                        }
                    }

                    // Remove new circles that were added AFTER continuation
                    for (int i = _polylineCommittedPointsCount; i < _currentPolylineCircles.Count; i++)
                    {
                        var circle = _currentPolylineCircles[i];
                        if (TileCanvas.Children.Contains(circle))
                            TileCanvas.Children.Remove(circle);
                    }

                    // Remove ALL current segment paths and rebuild properly
                    foreach (var seg in _currentPolylineSegments)
                    {
                        if (TileCanvas.Children.Contains(seg))
                            TileCanvas.Children.Remove(seg);
                    }
                    _currentPolylineSegments.Clear();

                    // Rebuild polyline points
                    currentPolyline.Points.Clear();
                    var committedPoints = new List<Point>();
                    var committedGeoPoints = new List<(double lat, double lon)>();

                    for (int i = 0; i < _polylineCommittedPointsCount; i++)
                    {
                        currentPolyline.Points.Add(polylinePoints[i]);
                        committedPoints.Add(polylinePoints[i]);

                        // Also restore geo points
                        if (_polylineGeoPoints.TryGetValue(currentPolyline, out var geoList) && i < geoList.Count)
                        {
                            committedGeoPoints.Add(geoList[i]);
                        }
                    }

                    // Update geo points list to only committed points
                    if (_polylineGeoPoints.ContainsKey(currentPolyline))
                    {
                        _polylineGeoPoints[currentPolyline] = committedGeoPoints;
                    }

                    // Rebuild the zone with the committed points only
                    double mpp = MetersPerPixel(latitude, zoom);
                    double halfWidthPx = (_polylineZoneWidthMeters / 2.0) / mpp;

                    if (committedPoints.Count >= 2)
                    {
                        RebuildPolylineZone(currentPolyline, committedPoints, halfWidthPx);
                    }

                    // Restore it as finalized
                    if (!mapRectangles.Any(mr => mr.Shape == currentPolyline))
                        mapRectangles.Add(new MapRectangle(currentPolyline));

                    Console.WriteLine($"[POLYLINE] Restored polyline with {currentPolyline.Points.Count} points and zone shapes");
                }
                else
                {
                    // This was a NEW polyline (not a continuation) - delete everything
                    Console.WriteLine("[POLYLINE] Deleting new polyline completely");

                    if (TileCanvas.Children.Contains(currentPolyline))
                        TileCanvas.Children.Remove(currentPolyline);

                    foreach (var dot in polylineVertexDots)
                    {
                        if (TileCanvas.Children.Contains(dot))
                            TileCanvas.Children.Remove(dot);
                        _polylineVertexMap.Remove(dot);
                        _polylineVertexToCircle.Remove(dot);
                    }

                    foreach (var c in _currentPolylineCircles)
                    {
                        if (TileCanvas.Children.Contains(c))
                            TileCanvas.Children.Remove(c);
                    }

                    foreach (var seg in _currentPolylineSegments)
                    {
                        if (TileCanvas.Children.Contains(seg))
                            TileCanvas.Children.Remove(seg);
                    }

                    if (_polylineToSegments.TryGetValue(currentPolyline, out var segments))
                    {
                        foreach (var seg in segments)
                        {
                            if (TileCanvas.Children.Contains(seg))
                                TileCanvas.Children.Remove(seg);
                        }
                    }

                    _polylineToSegments.Remove(currentPolyline);
                    _polylineGeoPoints.Remove(currentPolyline);
                }

                // OPRAVA 2: Smazat také směrové šipky při zrušení polyline
                if (currentPolyline != null && _polylineDirectionArrows.ContainsKey(currentPolyline))
                {
                    var arrows = _polylineDirectionArrows[currentPolyline];
                    foreach (var arrow in arrows)
                    {
                        if (TileCanvas.Children.Contains(arrow))
                            TileCanvas.Children.Remove(arrow);
                    }
                    _polylineDirectionArrows.Remove(currentPolyline);
                    Console.WriteLine("[POLYLINE] Removed direction arrows");
                }

                currentPolyline = null;
                polylinePoints.Clear();
                polylineVertexDots.Clear();
                _currentPolylineCircles.Clear();
                _currentPolylineSegments.Clear();
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

            if (dimensionTextBlock != null && dimensionTextBlock.Visibility == Visibility.Visible)
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

            if (PolylineWidthPanel != null && PolylineWidthPanel.Visibility == Visibility.Visible)
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
            if (sender is not Rectangle rect) return;
            if (_highlightedRect == rect) return;

            bool isTableSelected =
                (ActivationZonesDataGrid?.SelectedItem is ActivationZone az && ReferenceEquals(az.Rectangle, rect));

            if (isTableSelected) return;

            _highlightedRect = rect;
            _highlightedRectOldBrush = rect.Stroke;
            _highlightedRectOldThickness = rect.StrokeThickness;

            var scale = new ScaleTransform(1.04, 1.04, rect.Width / 2.0, rect.Height / 2.0);
            if (rect.RenderTransform is TransformGroup tg)
            {
                if (!_rectHighlightScales.TryGetValue(rect, out _))
                {
                    tg.Children.Insert(0, scale);
                    _rectHighlightScales[rect] = scale;
                }
            }
            else
            {
                var newGroup = new TransformGroup();
                newGroup.Children.Add(scale);
                newGroup.Children.Add(rect.RenderTransform ?? Transform.Identity);
                rect.RenderTransform = newGroup;
                _rectHighlightScales[rect] = scale;
            }

            if (activationZones != null && activationZones.TryGetValue(rect, out var zone))
            {
                var brush = TryBrushFromColor(zone.Color) ?? Brushes.Gray;
                rect.Stroke = MakeAlphaBrush((SolidColorBrush)brush, 210);
            }

            rect.StrokeThickness = Math.Max(1.0, rect.StrokeThickness) + 1.5;
        }

        /// <summary>
        /// Logic for leaving rectangles in selection mode - resets their appearance.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The event data.</param>
        private void Rectangle_MouseLeave(object? sender, MouseEventArgs e)
        {
            if (sender is not Rectangle rect) return;
            if (_highlightedRect != rect) return;

            bool zoneIsActive = false;
            if (activationZones.TryGetValue(rect, out var az))
                zoneIsActive = az.IsActive;

            rect.Stroke = _highlightedRectOldBrush ?? rect.Stroke;

            rect.StrokeThickness = zoneIsActive ? 6 : _highlightedRectOldThickness;

            if (rect.RenderTransform is TransformGroup tg && _rectHighlightScales.TryGetValue(rect, out var scale))
            {
                tg.Children.Remove(scale);

                if (tg.Children.Count == 1)
                    rect.RenderTransform = tg.Children[0];
                else
                    rect.RenderTransform = tg;

                _rectHighlightScales.Remove(rect);
            }

            _highlightedRect = null;
            _highlightedRectOldBrush = null;
            _highlightedRectOldThickness = 0;
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

        private async Task RemoveDrawnTramTrailGradually(int idx, MapPoint tram, CancellationToken token)
        {
            string key = $"drawn_{idx}_trail";

            try
            {
                bool continueRemoving = true;
                while (continueRemoving)
                {
                    continueRemoving = false;
                    Dispatcher.Invoke(() =>
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

                Dispatcher.Invoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    if (!vehicleTrailCleanupTokens.TryGetValue(key, out var activeCts) || activeCts.Token != token)
                        return;

                    if (tram.Ellipse != null) tram.Ellipse.Visibility = Visibility.Collapsed;
                    if (tram.Text != null) tram.Text.Visibility = Visibility.Collapsed;
                    if (tram.Speed != null) tram.Speed.Visibility = Visibility.Collapsed;

                    string manualId = drawnTramIds[idx];
                    if (_vehicleBoxes.TryGetValue(manualId, out var box))
                        box.Visibility = Visibility.Collapsed;

                    vehicleTrailCleanupTokens.Remove(key);
                    RemoveDrawnTramCompletely(idx, tram);
                });
            }
            catch (TaskCanceledException)
            {
                // Cancelled — token already removed by the cancelling caller, nothing to do here
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
                if (tram.Ellipse != null)
                    TileCanvas.Children.Remove(tram.Ellipse);
                if (tram.Text != null)
                    TileCanvas.Children.Remove(tram.Text);
                if (drawnTramTrails[idx] != null)
                    TileCanvas.Children.Remove(drawnTramTrails[idx]);
                if (tram.TrailDots != null)
                {
                    foreach (var dot in tram.TrailDots)
                        TileCanvas.Children.Remove(dot);
                    tram.TrailDots.Clear();
                }

                if (tram.Speed != null)
                    TileCanvas.Children.Remove(tram.Speed);

                drawnTramTrailPoints[idx].Clear();
                drawnTramTrailGeoPoints[idx].Clear();
                drawnTramLat[idx] = null;
                drawnTramLon[idx] = null;
                drawnTrams[idx] = null;

                // Fix: cancel the correct token key for drawn trams
                var tramKey = $"drawn_{idx}_trail";
                if (vehicleTrailCleanupTokens.TryGetValue(tramKey, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                    vehicleTrailCleanupTokens.Remove(tramKey);
                }

                var tramInfo = TramTable.FirstOrDefault(t => t.VehicleId == (tram.Label.Length > 4 ? tram.Label[^4..] : tram.Label));
                if (tramInfo != null)
                    TramTable.Remove(tramInfo);

                var manualId = drawnTramIds[idx];
                if (_vehicleBoxes.TryGetValue(manualId, out var bodyRect))
                {
                    TileCanvas.Children.Remove(bodyRect);
                    _vehicleBoxes.Remove(manualId);
                }
            });
        }

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


        //||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        // METHODS FOR THE CAM AND SRV STATUSES

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



        //||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        // METHODS FOR EXPORTING AND SAVING


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
                        recordedCamMessages.Add(camElem.ToString(SaveOptions.DisableFormatting));
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

            UpdateUiEnabledState();

            foreach (var vehicle in activeVehicles.Values)
            {
                CheckActivationZones(vehicle.Position, vehicle.Label);
            }

            Console.WriteLine($"Loaded file: {filePath}");


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


        //||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        // V2X MESSAGE METHODS


        //V2X Listener !!!!
        /// <summary>
        /// Starts the V2X listener on the specified port and baud rate.
        /// </summary>
        /// <param name="portName">The name of the serial port.</param>
        /// <param name="baudRate">The baud rate for the serial port.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private Task StartV2XListenerAsync(string portName, int baudRate)
        {
            serialPort = new SerialPort(portName, baudRate)
            {
                NewLine = "\r\n",
                Encoding = Encoding.ASCII
            };
            serialPort.Open();

            Dispatcher.Invoke(() =>
            {
                if (SrvCheckBox?.IsChecked == true)
                {
                    SendSrvMessage();
                    StartSrvAutoTimerIfEnabled();
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    while (serialPort.IsOpen)
                    {
                        string rawLine;
                        try { rawLine = await ReadLineAsync(serialPort).ConfigureAwait(false); }
                        catch { break; }

                        if (string.IsNullOrWhiteSpace(rawLine)) continue;

                        // =====================================================================
                        // PROTOBUF MESSAGE DETECTION AND HANDLING
                        // =====================================================================
                        if (IsProtobufMessage(rawLine))
                        {
                            // Ulož raw protobuf řádek z COM portu do bufferu nahrávky
                            if (_timeshiftEnabled)
                                recordedCamMessages.Add(rawLine.Trim());

                            Dispatcher.Invoke(() =>
                            {
                                try
                                {
                                    if (ProtobufParser.TryDecodeProtobufFromHex(rawLine.Trim(), out string decoded))
                                    {
                                        HandleProtobufMessage(decoded);
                                        Console.WriteLine($"[PROTO] Received and decoded Protobuf message ({rawLine.Length} chars)", Brushes.Cyan);
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[PROTO] Failed to decode Protobuf message", Brushes.Orange);
                                        IncrementCamErrorCount();
                                    }
                                }
                                catch (Exception protoEx)
                                {
                                    Console.WriteLine($"[PROTO] Error processing Protobuf: {protoEx.Message}", Brushes.Red);
                                    IncrementCamErrorCount();
                                }
                            });
                            continue;
                        }

                        // =====================================================================
                        // XML MESSAGE HANDLING (CAM/SRV)
                        // =====================================================================

                        // do not mix with classic replay or timeshift catch-up rendering
                        if (_isPlaybackSessionActive || _isTimeshiftPlaybackActive) continue;

                        int xmlStart = rawLine.IndexOf('<');
                        if (xmlStart < 0) continue;
                        string rawXml = rawLine.Substring(xmlStart);

                        bool wasLocalEcho = false;
                        lock (_recentLocalWritesLock)
                        {
                            int idx = _recentLocalWrites.FindIndex(s => s == rawXml);
                            if (idx >= 0)
                            {
                                _recentLocalWrites.RemoveAt(idx);
                                wasLocalEcho = true;
                            }
                        }
                        if (wasLocalEcho)
                        {
                            continue;
                        }

                        try
                        {
                            var msg = V2XMessageParser.ParseV2XMessage(rawXml);

                            if (msg.MessageType == "CAM")
                            {
                                bool valid = IsValidCamMessage(rawXml);
                                if (_timeshiftEnabled && valid && !(msg.VehicleID?.StartsWith("000000") ?? false))
                                    recordedCamMessages.Add(rawXml);

                                Dispatcher.Invoke(() =>
                                {
                                    var shortId = string.IsNullOrEmpty(msg.VehicleID) ? "-" : (msg.VehicleID.Length > 4 ? msg.VehicleID[^4..] : msg.VehicleID);
                                    var crcTxt = valid ? "CRC OK" : "CRC ERR";
                                    if (valid) IncrementCamOkCount();
                                    else
                                    {
                                        IncrementCamErrorCount();
                                    }
                                });
                            }
                            else if (msg.MessageType == "SRV")
                            {
                                if (_timeshiftEnabled)
                                    recordedSrvMessages.Add(rawXml);
                            }

                            if (_timeshiftEnabled && _timeshiftPaused)
                                continue;

                            Dispatcher.Invoke(() => HandleV2XMessage(msg, rawXml));
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                IncrementCamErrorCount();
                            });
                        }
                    }
                }
                catch (Exception loopEx)
                {
                    Dispatcher.Invoke(() => Console.WriteLine($"[SERIAL] Serial listen loop error: {loopEx.Message}"));
                }
            });

            return Task.CompletedTask;
        }


        /// <summary>
        /// Asynchronously reads a line from the specified serial port.
        /// </summary>
        /// <param name="port">The serial port to read from.</param>
        /// <returns>A task representing the asynchronous operation, with the read line as the result.</returns>
        private Task<string> ReadLineAsync(SerialPort port)
        {
            return Task.Run(() => port.ReadLine());
        }


        /// <summary>
        /// Handling logic for V2X messages, including filtering based on UI settings, parsing accuracy, and updating the map display.
        /// </summary>
        /// <param name="msg">The V2X message to handle.</param>
        /// <param name="rawXml">The raw XML representation of the V2X message.</param>
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
                        serialPort.Write(xml);
                        serialPort.Write(serialPort.NewLine);
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

            foreach (var zone in activationZones.Values)
            {
                if (!zone.Bounds.Contains(pos))
                    continue;

                if (!IsPointInRotatedRectangle(pos, zone))
                    continue;

                nowInZones.Add(zone);
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
                if (leftZone.Rectangle != null)
                    leftZone.Rectangle.StrokeThickness = 2;

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

                    Console.WriteLine($"[ZONE] {shortId} entered zone '{zone.Name}' | heading={heading:F0}° | valid={validDirection}");

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
                    if (zone.Rectangle != null)
                        zone.Rectangle.StrokeThickness = 6;
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
                        zone.IsActive = false;
                        if (zone.Rectangle != null)
                            zone.Rectangle.StrokeThickness = 2;
                        ((DispatcherTimer)s).Stop();
                        _zoneDeactivateTimers.Remove(zone);

                        if (_vehicleActiveZones.TryGetValue(vehicleId, out var vz))
                            vz.Remove(zone);

                        _vehicleZoneValidEntry.Remove((vehicleId, zone));
                    };
                    _zoneDeactivateTimers[zone] = timer;
                    timer.Start();
                }
            }

            _vehicleActiveZones[vehicleId] = nowInZones;

            foreach (var poly in _polylineToSegmentZones.Values)
            {
                continue;
            }
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

            Console.WriteLine($"[ZONE DIR] heading={heading:F0}° | zoneAz={zoneAzimuth:F0}° | diff={diff:F0}° | valid={valid}");

            return valid;
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
                            recordedCamMessages.Add(camElem.ToString(SaveOptions.DisableFormatting));
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

            // *** ZMĚNA MainZone → automatická změna barvy ***
            if (propertyName == nameof(ActivationZone.MainZone))
            {
                string newColor = GetColorForMainZone(segment.MainZone, IsSwitchMode());

                // Nastavit barvu (to vyvolá další PropertyChanged pro Color)
                if (segment.Color != newColor)
                {
                    segment.Color = newColor;
                    Console.WriteLine($"[POLYLINE] MainZone→{segment.MainZone} → Color={newColor} for seg {segment.SegmentIndex}");
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

                    UpdatePolylineVertexPositions(polyline, points);
                }

                Console.WriteLine($"[POLYLINE] Azimuth changed to {segment.Azimuth}° for segment {segment.SegmentIndex}");
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

        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //Methods for rotating rectangles around their base center

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
            string query = @"[out:json];
                     node[""railway""=""tram_stop""](49.77,18.19,49.87,18.32);
                     out;";

            using var client = new HttpClient();
            var response = await client.GetStringAsync(
                "https://overpass-api.de/api/interpreter?data=" + Uri.EscapeDataString(query));

            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            var elements = root.GetProperty("elements");

            List<Stop> stops = new List<Stop>();

            foreach (var el in elements.EnumerateArray())
            {
                var lat = el.GetProperty("lat").GetDouble();
                var lon = el.GetProperty("lon").GetDouble();
                string? name = el.TryGetProperty("tags", out var tags) &&
                              tags.TryGetProperty("name", out var stopNameEl)
                    ? stopNameEl.GetString()
                    : "Bez názvu";

                stops.Add(new Stop
                {
                    StopName = name,
                    Latitude = lat,
                    Longitude = lon
                });
            }

            return stops;
        }

        /// <summary>
        /// Draws stops on the canvas safely, ensuring thread safety.
        /// </summary>
        private void DrawStopsOnCanvasSafe()
        {
            if (TileCanvas == null) return;

            Dispatcher.Invoke(() =>
            {
                if (stops == null || stops.Count == 0)
                    return;

                // Remove previous stop visuals
                foreach (var el in TileCanvas.Children.OfType<FrameworkElement>()
                             .Where(el => Equals(el.Tag, "Stop"))
                             .ToList())
                {
                    TileCanvas.Children.Remove(el);
                }

                foreach (var stop in stops)
                {
                    var (x, y) = ConvertLatLonToCanvasXY(stop.Latitude, stop.Longitude);

                    var stopMarker = new Ellipse
                    {
                        Width = 8,
                        Height = 8,
                        Fill = Brushes.Red,
                        Stroke = Brushes.White,
                        StrokeThickness = 1,
                        Tag = "Stop",
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(stopMarker, x - 4);
                    Canvas.SetTop(stopMarker, y - 4);
                    Panel.SetZIndex(stopMarker, 500);
                    TileCanvas.Children.Add(stopMarker);

                    var stopLabel = new TextBlock
                    {
                        Text = stop.StopName,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.Black,
                        FontSize = 15,
                        Tag = "Stop",
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(stopLabel, x + 6);
                    Canvas.SetTop(stopLabel, y - 6);
                    Panel.SetZIndex(stopLabel, 501);
                    TileCanvas.Children.Add(stopLabel);
                }
            });
        }


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
            _isDrawingPolyline = true;

            // OPRAVA: Vytvořit preview polyline hned při aktivaci mode
            if (currentPolyline == null)
            {
                currentPolyline = new Polyline
                {
                    Stroke = _strokeBrush,
                    StrokeThickness = 2,
                    Fill = null,
                    IsHitTestVisible = false // Preview je non-interactive
                };
                TileCanvas.Children.Add(currentPolyline);
                Panel.SetZIndex(currentPolyline, 100);
                Console.WriteLine("[POLYLINE] Created preview polyline");
            }

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
        private void RefreshMap_Click(object sender, RoutedEventArgs e)
        {
            RefreshMap();
        }

        /// <summary>
        /// Handler for export button, exports map to PNG.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ExportMap_Click(object sender, RoutedEventArgs e)
        {
            ExportCanvasToPng(TileCanvas, "map_export.png");
            Console.WriteLine("Map exported into: map_export.png");
        }

        /// <summary>
        /// Handler for connect button, connects to a given COM port with given baudrate.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? portName = ComPortsComboBox.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(portName))
                {
                    MessageBox.Show("Select a COM port from the list.");
                    return;
                }

                if (!int.TryParse(BaudrateTB.Text.Trim(), out int baudRate))
                {
                    MessageBox.Show("Enter valid numeric baudrate");
                    return;
                }

                if (serialPort == null || !serialPort.IsOpen)
                {
                    await StartV2XListenerAsync(portName, baudRate);
                    _isConnected = true;

                    // START TIMESHIFT RIGHT AFTER CONNECT
                    StartTimeshiftSession();
                    UpdateUiEnabledState();
                    Console.WriteLine($"[CONNECT] User connected on port {portName} at {baudRate} baud/s.");
                    MessageBox.Show($"Connected on {portName} at {baudRate} bps.");

                }
                else
                {
                    Console.WriteLine($"[CONNECT] User tried to connect to port {portName} but port is already open.");
                    MessageBox.Show("Port already open.");

                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERR] Failed to connect: {ex.Message}");
                MessageBox.Show($"Failed to connect: {ex.Message}");
            }
        }

        /// <summary>
        /// Handler for disconnect button, disconnects from com port.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Determine whether there is anything to save
                bool hasManualRecording = recordedManualCamMessages != null && recordedManualCamMessages.Count > 0;
                bool hasLiveBuffer = recordedCamMessages != null && recordedCamMessages.Count > 0;
                bool promptNeeded = isRecording || hasManualRecording || hasLiveBuffer;

                if (!promptNeeded)
                {
                    // nothing to save — stop timeshift silently and disconnect
                    if (_timeshiftEnabled)
                        StopTimeshiftSession();

                    // proceed with disconnect
                    try
                    {
                        if (serialPort != null && serialPort.IsOpen)
                        {
                            try { serialPort.DataReceived -= SerialPort_DataReceived; } catch { }
                            serialPort.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DISCONNECT] Error while closing port: " + ex.Message);
                        MessageBox.Show("Error while closing port: " + ex.Message, "Disconnect", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    ClearLiveVehiclesAndRsu();
                    StopSrvAutoTimer();
                    _isConnected = false;
                    UpdateUiEnabledState();
                    Console.WriteLine($"[DISCONNECT] User disconnected from selected serial port.");
                    MessageBox.Show("Disconnected from serial port.");

                    return;
                }

                if (!_savedRecording)
                {
                    // There are unsaved items or active recording — ask the user
                    var messageBuilder = new StringBuilder("Recording or buffered data present. Do you want to stop and save before disconnecting?\n\n");
                    if (isRecording) messageBuilder.AppendLine("- Manual recording is active");
                    if (hasManualRecording) messageBuilder.AppendLine($"- {recordedManualCamMessages?.Count ?? 0} manual CAM message(s) to save");
                    if (hasLiveBuffer) messageBuilder.AppendLine($"- {recordedCamMessages?.Count ?? 0} live CAM message(s) in RS485 buffer");
                    var result = MessageBox.Show(messageBuilder.ToString(), "Recording active", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Cancel)
                    {
                        // User aborted disconnect
                        return;
                    }

                    if (result == MessageBoxResult.Yes)
                    {
                        // Stop & save manual recording (StopRecording shows save dialog)
                        if (isRecording)
                            StopRecording();

                        // Save manual buffer if present and not recorded via StopRecording
                        if (hasManualRecording && !isRecording)
                        {
                            var dlgManual = new Microsoft.Win32.SaveFileDialog
                            {
                                FileName = DateTime.Now.ToString("yyyy-MM-dd_HH_mm", CultureInfo.InvariantCulture) + ".camrec",
                                DefaultExt = ".camrec",
                                Filter = "CAM Recording (*.camrec)|*.camrec|All files (*.*)|*.*",
                                Title = "Save manual CAM recording"
                            };
                            if (dlgManual.ShowDialog() == true)
                            {
                                WriteCamrecWithCenter(dlgManual.FileName, recordedManualCamMessages ?? new List<string>());
                                MessageBox.Show("Manual CAM recording saved to:\n" + dlgManual.FileName, "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                                recordedManualCamMessages?.Clear();
                            }
                        }

                        // Save live RS485 buffer if present
                        if (hasLiveBuffer)
                        {
                            SaveLiveCamBuffer();
                        }

                        // Stop timeshift after saving
                        if (_timeshiftEnabled) StopTimeshiftSession();
                    }
                    else // No -> stop and discard recordings/buffers
                    {
                        if (isRecording)
                        {
                            isRecording = false;
                            recordedManualCamMessages?.Clear();
                        }

                        if (hasManualRecording)
                            recordedManualCamMessages?.Clear();
                        if (hasLiveBuffer)
                            recordedCamMessages?.Clear();

                        if (_timeshiftEnabled) StopTimeshiftSession();
                    }
                }

                // proceed with disconnect
                try
                {
                    if (serialPort != null && serialPort.IsOpen)
                    {
                        try { serialPort.DataReceived -= SerialPort_DataReceived; } catch { }
                        serialPort.Close();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERR] Error while closing port: " + ex.Message);
                    MessageBox.Show("Error while closing port: " + ex.Message, "Disconnect", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                ClearLiveVehiclesAndRsu();
                StopSrvAutoTimer();
                _isConnected = false;

                UpdateUiEnabledState();
                Console.WriteLine($"[DISCONNECT] User disconnected from selected serial port.");
                MessageBox.Show("Disconnected from serial port.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERR] Error disconnecting: " + ex.Message);
                MessageBox.Show("Error disconnecting: " + ex.Message);
            }
        }

        /// <summary>
        /// Clears all live vehicles, drawn trams, RSU markers, and the radius circle from the canvas.
        /// Called on disconnect to leave the map clean for the next session.
        /// </summary>
        private void ClearLiveVehiclesAndRsu()
        {
            // 1. Cancel all pending trail-cleanup tokens
            foreach (var kv in vehicleTrailCleanupTokens.ToList())
            {
                try { kv.Value.Cancel(); kv.Value.Dispose(); } catch { }
            }
            vehicleTrailCleanupTokens.Clear();

            // 2. Remove all live CAM / SRV vehicles
            foreach (var kv in activeVehicles.ToList())
                RemoveVehicleCompletely(kv.Key, kv.Value);
            activeVehicles.Clear();

            // 3. Remove all drawn (manual) trams
            for (int i = 0; i < drawnTrams.Length; i++)
            {
                if (drawnTrams[i] != null)
                    RemoveDrawnTramCompletely(i, drawnTrams[i]);
            }

            // 4. Remove any RSU-tagged canvas elements created by the Protobuf SRV path
            //    (these are added directly to TileCanvas without going through activeVehicles)
            var rsuEllipses = TileCanvas.Children.OfType<Ellipse>()
                .Where(el => el.Tag is string t && t.StartsWith("RSU", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var el in rsuEllipses) TileCanvas.Children.Remove(el);

            var rsuLabels = TileCanvas.Children.OfType<TextBlock>()
                .Where(tb => tb.Tag is string t && t.StartsWith("RSU", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var tb in rsuLabels) TileCanvas.Children.Remove(tb);

            // 5. Remove the radius circle and reset SRV position state
            if (radiusEllipse != null)
            {
                TileCanvas.Children.Remove(radiusEllipse);
                radiusEllipse = null;
            }
            srvLatitude = null;
            srvLongitude = null;

            // 6. Clear supporting state so the next session starts fresh
            TramTable.Clear();
            vehicleColorMap.Clear();
            _lastLatLon.Clear();
            _lastHeadingLive.Clear();
            _lastLiveAccuracyById.Clear();
            _liveAccuracyTextById.Clear();
            _vehicleActiveZones.Clear();
            _vehicleZoneValidEntry.Clear();

            Console.WriteLine("[DISCONNECT] RSU and all vehicle visuals cleared.");
        }



        /// <summary>
        /// Handler for save to xml button, saves map drawings to an xml file.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SaveToXML_Click(object? sender, RoutedEventArgs? e)
        {
            // consider activation zones and railways too
            bool hasZones = activationZones.Count > 0 || (ActivationZonesCollection?.Count > 0);

            bool hasAnything =
                hasZones ||
                points.Count > 0 ||
                mapRectangles.Count > 0 ||
                connectionLine.Points.Count > 0;

            if (!hasAnything)
            {
                var confirm = MessageBox.Show(
                    "Drawing is empty. Do you still want to save?",
                    "Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "map_rectangles",
                DefaultExt = ".xml",
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "Save map into XML"
            };

            bool? result = dlg.ShowDialog();
            if (result != true) return;

            string fileNameToSave = dlg.FileName;
            loadedFileName = fileNameToSave;

            SaveXML(fileNameToSave);
            MessageBox.Show("Data saved into file:\n" + fileNameToSave, "Data saved.", MessageBoxButton.OK, MessageBoxImage.Information);

            isDirty = false;
        }



        /// <summary>
        /// Handler for load from xml button, loads drawings from an xml file.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LoadFromXML_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                DefaultExt = ".xml",
                Filter = "XML files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "Select an XML file to load onto the map"
            };

            bool? result = dlg.ShowDialog();

            if (result == true)
            {
                LoadXML(dlg.FileName);
                MessageBox.Show("Data loaded from file:\n" + dlg.FileName, "Data loaded", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// Handler for play movement button, starts replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PlayMovement_Click(object sender, RoutedEventArgs e)
        {
            if (_keyframes == null || _keyframes.Count == 0)
                BuildPlaybackKeyframes();

            if (_keyframes == null || _keyframes.Count == 0)
            {
                MessageBox.Show("No CAM frames to play.", "Playback", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (isPlaying) return;

            PurgeLiveCamVehiclesForPlayback();

            // Use ReplaySlider for classic replay
            ReplaySlider.Minimum = 0;
            ReplaySlider.Maximum = Math.Max(0, _keyframes.Count - 1);
            ReplaySlider.TickFrequency = 1;
            ReplaySlider.SmallChange = 1;
            ReplaySlider.LargeChange = Math.Max(1, _keyframes.Count / 20);
            ReplaySlider.IsSnapToTickEnabled = true;
            ReplaySlider.Value = _playbackIndex;

            var t0 = _keyframes[_playbackIndex];
            playbackElapsedTime = t0;
            RedrawPlaybackToTime(t0);
            UpdateReplayTimerLabel();

            SendPlaybackCamForTime(t0);

            _isPlaybackSessionActive = true;
            isPlaying = true;
            playbackStartTime = DateTime.Now - t0;
            if (playbackTimer == null)
            {
                playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                playbackTimer.Tick += PlaybackTimer_Tick;
            }
            playbackTimer.Start();

            UpdateUiEnabledState();
        }

        /// <summary>
        /// Handler for record movement button, records replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnRecordMovement_Click(object sender, RoutedEventArgs e)
        {
            // removing all active vehicles
            foreach (var vehicleId in activeVehicles.Keys.ToList())
            {
                if (activeVehicles.TryGetValue(vehicleId, out var vehicle))
                    RemoveVehicleCompletely(vehicleId, vehicle);
            }
            activeVehicles.Clear();

            // removing of all drawn trams
            for (int i = 0; i < drawnTrams.Length; i++)
            {
                if (drawnTrams[i] != null)
                    RemoveDrawnTramCompletely(i, drawnTrams[i]);
                drawnTrams[i] = null;
                drawnTramTrailPoints[i].Clear();
                if (drawnTramTrails[i] != null)
                {
                    TileCanvas.Children.Remove(drawnTramTrails[i]);
                    drawnTramTrails[i] = null;
                }
            }

            // Vymazání tabulky tramvají
            TramTable.Clear();

            StartRecording();
        }

        /// <summary>
        /// Handler for stop recording button, stops recording.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnStopRecording_Click(object sender, RoutedEventArgs e)
        {
            StopRecording();
        }

        /// <summary>
        /// Handler for map controls button, shows map controls.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MapControls_Click(object sender, RoutedEventArgs e)
        {

            MessageBox.Show(ControlsMessage, "Map controls");

        }

        /// <summary>
        /// Handler for baudrate textbox, saves baudrate for future connection.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BaudrateTB_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = BaudrateTB?.Text?.Trim() ?? string.Empty;

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int baud) && baud > 0)
            {
                try
                {
                    // Apply to the current serial port only when it's closed (safe change)
                    if (serialPort != null && !serialPort.IsOpen)
                    {
                        serialPort.BaudRate = baud;
                    }
                    // If connected, UI prevents editing; no action needed here.
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to apply baudrate: {ex.Message}", "Baudrate", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            // If invalid input, do nothing; Connect_Click will still validate and prompt.
        }



        /// <summary>
        /// Resets all tram trails if trams are live and if the position on the map changes.
        /// </summary>
        private void ResetAllTramTrails()
        {
            // Active vehicles (RS485/live)
            // Remove any polyline tagged as tram trail and clear dots + buffer
            var allTrailLines = TileCanvas.Children
                .OfType<Polyline>()
                .Where(pl => pl.Tag is string s && s.StartsWith("tram_trail", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var l in allTrailLines)
                TileCanvas.Children.Remove(l);

            foreach (var kv in activeVehicles.ToList())
            {
                var v = kv.Value;
                if (v.TrailDots != null)
                {
                    foreach (var dot in v.TrailDots.ToList())
                        TileCanvas.Children.Remove(dot);
                    v.TrailDots.Clear();
                }
                // Clear canvas-based history so new trail starts fresh with next CAMs
                v.MovementFrames?.Clear();
            }

            // Drawn trams (manual X/C)
            for (int i = 0; i < drawnTrams.Length; i++)
            {
                if (drawnTramTrails[i] != null)
                {
                    TileCanvas.Children.Remove(drawnTramTrails[i]);
                    drawnTramTrails[i] = null;
                }
                drawnTramTrailPoints[i].Clear();
                drawnTramTrailGeoPoints[i].Clear();

                var tram = drawnTrams[i];
                if (tram?.TrailDots != null)
                {
                    foreach (var dot in tram.TrailDots.ToList())
                        TileCanvas.Children.Remove(dot);
                    tram.TrailDots.Clear();
                }
                tram?.MovementFrames?.Clear();
            }
        }


        /// <summary>
        /// Redraws vehicle trails.
        /// </summary>
        private void RedrawVehicleTrails()
        {
            // Remove any existing tram trail polylines (both new/old tag variants)
            var tramPolylines = TileCanvas.Children
                .OfType<Polyline>()
                .Where(pl => pl.Tag is string s && s.StartsWith("tram_trail", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var l in tramPolylines)
                TileCanvas.Children.Remove(l);

            foreach (var vehicle in activeVehicles.Values)
            {
                if (!TileCanvas.Children.Contains(vehicle.Ellipse))
                    TileCanvas.Children.Add(vehicle.Ellipse);
                if (!TileCanvas.Children.Contains(vehicle.Text))
                    TileCanvas.Children.Add(vehicle.Text);

                Panel.SetZIndex(vehicle.Ellipse, 1000);
                Panel.SetZIndex(vehicle.Text, 1000);

                // Only rebuild if we decide to redraw history (normally we reset trails after pan/zoom/refresh)
                if (vehicle.MovementFrames != null && vehicle.MovementFrames.Count > 1)
                {
                    var polyline = new Polyline
                    {
                        Stroke = vehicle.Ellipse?.Fill,
                        StrokeThickness = 2,
                        Tag = $"tram_trail_{vehicle.Label}"
                    };

                    foreach (var mf in vehicle.MovementFrames)
                        polyline.Points.Add(mf.Position);

                    TileCanvas.Children.Add(polyline);
                    Panel.SetZIndex(polyline, 999);
                }
            }
        }

        /// <summary>
        /// Brings all overlays to front safely.
        /// </summary>
        public async Task BringAllOverlaysToFrontSafeAsync()
        {
            if (TileCanvas == null) return;

            await Dispatcher.InvokeAsync(() =>
            {
                // Ensure rectangles are above map tiles
                if (mapRectangles != null)
                {
                    foreach (var mr in mapRectangles.Where(m => m?.Shape != null))
                    {
                        var shape = mr.Shape;
                        if (shape != null && TileCanvas.Children.Contains(shape))
                            Panel.SetZIndex(shape, 100);
                    }
                }

                // Ensure points are above rectangles
                if (points != null)
                {
                    foreach (var pt in points.Where(p => p != null))
                    {
                        if (pt.Ellipse != null && TileCanvas.Children.Contains(pt.Ellipse))
                            Panel.SetZIndex(pt.Ellipse, 200);
                        if (pt.Text != null && TileCanvas.Children.Contains(pt.Text))
                            Panel.SetZIndex(pt.Text, 201);
                    }
                }

                // Connection line above rectangles, below points
                if (connectionLine != null && TileCanvas.Children.Contains(connectionLine))
                    Panel.SetZIndex(connectionLine, 150);

                // Vehicle overlays
                if (activeVehicles != null)
                {
                    foreach (var vehicle in activeVehicles.Values.Where(v => v != null))
                    {
                        if (vehicle.Ellipse != null && TileCanvas.Children.Contains(vehicle.Ellipse))
                            Panel.SetZIndex(vehicle.Ellipse, 1000);
                        if (vehicle.Text != null && TileCanvas.Children.Contains(vehicle.Text))
                            Panel.SetZIndex(vehicle.Text, 1000);
                    }
                }

                // Activation zones
                if (activationZones != null)
                {
                    foreach (var zone in activationZones.Values.Where(z => z?.Rectangle != null))
                    {
                        var r = zone.Rectangle;
                        if (r != null && TileCanvas.Children.Contains(r))
                            Panel.SetZIndex(r, 100);
                    }
                }

                // Drawn trams
                if (drawnTrams != null && drawnTramTrails != null)
                {
                    for (int idx = 0; idx < drawnTrams.Length; idx++)
                    {
                        var tram = drawnTrams[idx];
                        if (tram == null) continue;

                        if (tram.Ellipse != null && TileCanvas.Children.Contains(tram.Ellipse))
                            Panel.SetZIndex(tram.Ellipse, 1000);
                        if (tram.Text != null && TileCanvas.Children.Contains(tram.Text))
                            Panel.SetZIndex(tram.Text, 1000);
                        if (idx >= 0 && idx < drawnTramTrails.Length && drawnTramTrails[idx] != null &&
                            TileCanvas.Children.Contains(drawnTramTrails[idx]))
                            Panel.SetZIndex(drawnTramTrails[idx], 999);

                        if (tram.TrailDots != null)
                        {
                            foreach (var dot in tram.TrailDots.Where(d => d != null))
                                if (TileCanvas.Children.Contains(dot))
                                    Panel.SetZIndex(dot, 1001);
                        }
                    }
                }

                // Tram trails with legacy tag
                foreach (var poly in TileCanvas.Children.OfType<Polyline>().Where(l => (l.Tag as string) == "tram_trail"))
                    Panel.SetZIndex(poly, 999);

                // Dimension textblock always on top
                if (dimensionTextBlock != null && TileCanvas.Children.Contains(dimensionTextBlock))
                    Panel.SetZIndex(dimensionTextBlock, int.MaxValue);
            });
        }

        /// <summary>
        /// Logic for checked radius box.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CheckBox_Checked(object sender, RoutedEventArgs e)
        {
            RadiusComboBox.IsEnabled = true;
            DrawRadiusCircle();
        }

        /// <summary>
        /// Logic for unchecked radius box.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            RadiusComboBox.IsEnabled = false;

            if (radiusEllipse != null)
            {
                TileCanvas.Children.Remove(radiusEllipse);
            }
        }

        /// <summary>
        /// Logic for radius combo box selection.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RadiusComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CircleCheckBox.IsChecked == true)
                DrawRadiusCircle();

        }

        /// <summary>
        /// Logic for clearing all objects.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            Console.WriteLine($"[CLEAR] Clear all objects requested");

            var result = MessageBox.Show("Are you sure you want to delete everything?",
                                         "Confirmation",
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                Console.WriteLine($"[CLEAR] Cancelled by user\n");
                return;
            }

            Console.WriteLine($"[CLEAR] Clearing all objects...");

            ClearAll();
        }

        /// <summary>
        /// Clears polyline direction arrows and their wings
        /// </summary>
        private void ClearPolylineDirectionArrows()
        {
            try
            {
                // Work on a snapshot to avoid collection-modification issues
                var allEntries = _polylineDirectionArrows.ToList();

                foreach (var kv in allEntries)
                {
                    var segments = kv.Value;
                    if (segments == null)
                        continue;

                    foreach (var seg in segments.ToList())
                    {
                        if (seg == null)
                            continue;

                        // If the segment is currently attached to a panel, remove it safely on UI thread
                        if (seg.Parent is System.Windows.Controls.Panel parentPanel)
                        {
                            parentPanel.Dispatcher.Invoke(() =>
                            {
                                if (parentPanel.Children.Contains(seg))
                                    parentPanel.Children.Remove(seg);
                            });
                        }
                        else
                        {
                            // Fallback: try removing via main window dispatcher searching common canvases
                            Application.Current?.Dispatcher.Invoke(() =>
                            {
                                // Try common canvas names if available (TileCanvas, MapCanvas, etc.)
                                // This is defensive: removal above should normally succeed.
                                if (TileCanvas != null && TileCanvas.Children.Contains(seg))
                                    TileCanvas.Children.Remove(seg);
                            });
                        }
                    }

                    // clear the list for this polyline
                    segments.Clear();
                    _polylineDirectionArrows.Remove(kv.Key);
                }

                // ensure dictionary is empty
                _polylineDirectionArrows.Clear();
            }
            catch (Exception ex)
            {
                // don't crash UI when cleaning up; log for diagnostics
                Console.WriteLine($"[UI CLEANUP] ClearPolylineDirectionArrows: {ex.Message}");
            }
        }



        /// <summary>
        /// Clears everything from the map
        /// </summary>
        private void ClearAll()
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
                else if (child is System.Windows.Shapes.Path path)
                {
                    var tag = path.Tag as string;
                    // Support all polyline-related tags
                    if (tag != null && (tag.StartsWith("PolylineZone") ||
                                        tag.StartsWith("PolylineSegment") ||
                                        tag.StartsWith("PolylineCenterLine") ||
                                        tag == "PolylineZoneMerged"))
                    {
                        elementsToRemove.Add(child);
                    }
                }
                else if (child is Ellipse ellipse)
                {
                    var tag = ellipse.Tag as string;

                    if (tag == "PolylineZoneCircle" || tag == "PolylineVertex")
                    {
                        elementsToRemove.Add(child);
                    }
                    else if (ellipse.Tag == null ||
                        (tag != "Tram" &&
                         tag != "Srv" &&
                         tag != "Stop"))
                    {
                        elementsToRemove.Add(child);
                    }
                }
                else if (child is TextBlock textBlock)
                {
                    if (textBlock.Tag == null ||
                        (textBlock.Tag.ToString() != "Tram" &&
                         textBlock.Tag.ToString() != "Srv" &&
                         textBlock.Tag.ToString() != "Stop" && 
                         textBlock.Tag.ToString() != "Signal"))
                    {
                        elementsToRemove.Add(child);
                    }
                }
            }

            foreach (var el in elementsToRemove)
            {
                TileCanvas.Children.Remove(el);
            }

            points.RemoveAll(p => p.Ellipse == null || p.Ellipse.Tag == null ||
                                  (p.Ellipse.Tag.ToString() != "Tram" &&
                                   p.Ellipse.Tag.ToString() != "Srv" &&
                                   p.Ellipse.Tag.ToString() != "Stop"));

            mapRectangles.Clear();
            activationZones.Clear();

            // Clear polyline segments from ActivationZonesCollection
            var polylineSegmentsToRemove = ActivationZonesCollection.Where(z => z.PolylineId.HasValue).ToList();
            foreach (var seg in polylineSegmentsToRemove)
            {
                ActivationZonesCollection.Remove(seg);
            }

            _polylineRows.Clear();
            _polylineToSegmentZones.Clear();

            // Clear remaining activation zones
            ActivationZonesCollection.Clear();

            connectionLine.Points.Clear();
            TramTable.Clear();

            // Clear ALL polyline data structures
            _polylineVertexMap.Clear();
            _polylineVertexToCircle.Clear();
            _polylineToSegments.Clear();
            _polylineGeoPoints.Clear();
            _currentPolylineCircles.Clear();
            _currentPolylineSegments.Clear();
            _drawnPolylines.Clear();

            // Clear active drawing state
            currentPolyline = null;
            polylinePoints.Clear();
            polylineVertexDots.Clear();
            _isDrawingPolyline = false;

            for (int i = 0; i < drawnTrams.Length; i++)
            {
                drawnTrams[i] = null;
                drawnTramTrailPoints[i].Clear();
                drawnTramTrailGeoPoints[i].Clear();
                drawnTramLat[i] = null;
                drawnTramLon[i] = null;

                if (drawnTramTrails?[i] != null)
                {
                    TileCanvas.Children.Remove(drawnTramTrails[i]);
                    drawnTramTrails[i] = null;
                }
            }

            bool isEmpty = points.Count == 0 && mapRectangles.Count == 0 && connectionLine.Points.Count == 0;

            if (isEmpty)
            {
                if (!string.IsNullOrEmpty(loadedFileName))
                {
                    var folder = System.IO.Path.GetDirectoryName(loadedFileName);
                    loadedFileName = System.IO.Path.Combine(folder ?? string.Empty, "default_empty_save.xml");
                }
                else
                {
                    loadedFileName = "default_empty_save.xml";
                }
            }

            isDirty = true;

            rectPhase = RectangleDrawPhase.None;
            isDrawing = false;
            isSelectionMode = true;
            currentDrawingMode = DrawingMode.None;
            UpdateHitTestForSelectableElements();
            CancelAllDrawing();

            Console.WriteLine($"[CLEAR] Complete - all polylines and zones cleared");
        }



        /// <summary>
        /// Logic for Tram1 text box.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Tram1TB_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTramTextChanged) return;

            var tb = sender as TextBox;
            if (tb == null) return;

            // only for 4 characters
            if (tb.Text?.Length > 4)
            {
                _suppressTramTextChanged = true;
                int caret = Math.Min(4, tb.SelectionStart);
                tb.Text = tb.Text.Substring(0, 4);
                tb.SelectionStart = caret;
                _suppressTramTextChanged = false;
            }

            if (isPlaying) return;

            if (!string.IsNullOrWhiteSpace(tb.Text))
                drawnTramIds[0] = "000000" + tb.Text.PadLeft(4, '0');
            else
                drawnTramIds[0] = "0000009999";
        }

        /// <summary>
        /// Logic for Tram2 text box.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Tram2TB_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTramTextChanged) return;

            var tb = sender as TextBox;
            if (tb == null) return;

            // 4 chars
            if (tb.Text?.Length > 4)
            {
                _suppressTramTextChanged = true;
                int caret = Math.Min(4, tb.SelectionStart);
                tb.Text = tb.Text.Substring(0, 4);
                tb.SelectionStart = caret;
                _suppressTramTextChanged = false;
            }

            if (isPlaying) return;

            if (!string.IsNullOrWhiteSpace(tb.Text))
                drawnTramIds[1] = "000000" + tb.Text.PadLeft(4, '0');
            else
                drawnTramIds[1] = "0000001111";
        }


        /// <summary>
        /// Playback file button logic.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnPlaybackFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                DefaultExt = ".camrec",
                Filter = "CAM Recording (*.camrec)|*.camrec|XML files (*.xml)|*.xml|All files (*.*)|*.*",
                Title = "Select recording for playback"
            };

            bool? result = dlg.ShowDialog();

            if (result == true)
            {
                LoadPlaybackFile(dlg.FileName);
            }
        }


        /// <summary>
        /// Pause replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_playbackLoaded)
            {
                if (playbackTimer != null && playbackTimer.IsEnabled)
                    playbackTimer.Stop();
                isPlaying = false;

                SyncTramTableForReplay(playbackElapsedTime); // ensure table reflects paused time

                UpdateUiEnabledState();
                return;
            }

            _timeshiftPaused = true;
            _suppressLiveRender = true;
            _timeshiftFollowLive = false;
            _timeshiftPlaybackCts?.Cancel();
            UpdateUiEnabledState();
        }


        /// <summary>
        /// Resume replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void btnPlay_Click(object sender, RoutedEventArgs e)
        {
            // Classic replay first
            if (_playbackLoaded)
            {
                if (_keyframes == null || _keyframes.Count == 0)
                    BuildPlaybackKeyframes();
                if (_keyframes.Count == 0)
                    return;

                // Make sure slider state won't block updates
                isReplaySliderDragging = false;

                // Start from current slider position
                _playbackIndex = (int)Math.Round(ReplaySlider.Value);
                _playbackIndex = Math.Clamp(_playbackIndex, 0, Math.Max(0, _keyframes.Count - 1));

                // If at the end and no loop, step back one frame so timer can advance
                if (_playbackIndex >= _keyframes.Count - 1)
                {
                    if (LoopCheckbox?.IsChecked == true && _keyframes.Count > 0)
                    {
                        _playbackIndex = 0;
                        ReplaySlider.Value = 0;
                    }
                    else if (_keyframes.Count > 1)
                    {
                        _playbackIndex = _keyframes.Count - 2;
                        ReplaySlider.Value = _playbackIndex;
                    }
                    else
                    {
                        // Single-frame replay; cannot play
                        isPlaying = false;
                        _isPlaybackSessionActive = true;
                        UpdateReplayTimerLabel();
                        UpdateUiEnabledState();
                        return;
                    }
                }

                var t = _keyframes[_playbackIndex];

                playbackElapsedTime = t;
                playbackStartTime = DateTime.Now - playbackElapsedTime;
                RedrawPlaybackToTime(t);
                UpdateReplayTimerLabel();

                UpdateReplayStatsForTime(t);

                // Ensure timer exists and is running
                if (playbackTimer == null)
                {
                    playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                    playbackTimer.Tick += PlaybackTimer_Tick;
                }

                _isPlaybackSessionActive = true;
                isPlaying = true;

                if (!playbackTimer.IsEnabled)
                    playbackTimer.Start();

                UpdateUiEnabledState();
                return;
            }

            // Timeshift resume – only when no classic replay is loaded
            if (_timeshiftEnabled)
            {
                _timeshiftPaused = false;
                _suppressLiveRender = false;
                _timeshiftFollowLive = false;
                StartTimeshiftCatchupFromSliderAsync();
            }

            UpdateUiEnabledState();
        }


        /// <summary>
        /// Logic for when the selected COM port is changed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ComPortsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }


        /// <summary>
        /// Refresh all COM ports.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void RefreshComPorts_Click(object sender, RoutedEventArgs e)
        {
            LoadAvailableComPorts();
        }

        /// <summary>
        /// Handle mouse events while replay is active and user is dragging playback slider.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PlaybackSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            isSliderDragging = true;

            if (_timeshiftEnabled)
            {
                _timeshiftFollowLive = false;
                _timeshiftPaused = true;
                _suppressLiveRender = true;
                _timeshiftPlaybackCts?.Cancel();
            }

            if (playbackTimer != null)
                playbackTimer.Stop();
        }


        /// <summary>
        /// Update timer label while replay is active.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void UpdateTimerLabel()
        {
            try
            {
                string text;
                if (_replayStartUtc.HasValue)
                {
                    // Show absolute replay time (local)
                    var curUtc = _replayStartUtc.Value.Add(playbackElapsedTime);
                    var curLocal = TimeZoneInfo.ConvertTimeFromUtc(curUtc.ToUniversalTime(), czechTimeZone);
                    text = $"{curLocal:HH:mm:ss}";
                }
                else
                {
                    // Fallback: elapsed timeline
                    text = $"{playbackElapsedTime:hh\\:mm\\:ss}";
                }

                if (CurrentTimeLabel != null)
                    CurrentTimeLabel.Content = text;
            }
            catch { /* ignore if label not present */ }
        }

        /// <summary>
        /// Loop replay.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void LoopCheckbox_Checked(object sender, RoutedEventArgs e)
        {
            if (LoopCheckbox?.IsChecked == true && _keyframes.Count > 0 &&
                (!isPlaying || _playbackIndex >= _keyframes.Count - 1))
            {
                _playbackIndex = 0;
                playbackElapsedTime = _keyframes[0];
                playbackStartTime = DateTime.Now - playbackElapsedTime;

                RedrawPlaybackToTime(playbackElapsedTime);

                if (playbackTimer == null)
                {
                    playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                    playbackTimer.Tick += PlaybackTimer_Tick;
                }
                if (!playbackTimer.IsEnabled)
                    playbackTimer.Start();

                _isPlaybackSessionActive = true;
                isPlaying = true;

                if (!isReplaySliderDragging)
                    ReplaySlider.Value = 0;

                UpdateTimerLabel();
                UpdateReplayTimerLabel();
            }
        }

        /// <summary>
        /// Set custom speed for simulated tram.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SpeedBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var text = SpeedBox.Text?.Trim() ?? "";
            text = text.Replace("km/h", "", StringComparison.OrdinalIgnoreCase).Trim();
            text = text.Replace(',', '.');

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var spd) && spd >= 0)
                _manualCamSpeedKmh = spd;
            else
                _manualCamSpeedKmh = 0.0;
        }

        /// <summary>
        /// Send test SRV message.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SendSrv_Click(object sender, RoutedEventArgs e)
        {
            SendSrvMessage();
        }

        /// <summary>
        /// Send test SRV messages periodically.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SrvCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                SendSrvMessage();
                StartSrvAutoTimerIfEnabled();
            }
        }


        /// <summary>
        /// Send test SRV message.
        /// </summary>
        private void SendSrvMessage()
        {
            double lat = latitude;
            double lon = longitude;

            string logicalId = "RE108031";
            string dt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            double alt = 0.0;

            string service = $"<service logicalId=\"{logicalId}\" dt=\"{dt}\" lat=\"{lat.ToString(CultureInfo.InvariantCulture)}\" lng=\"{lon.ToString(CultureInfo.InvariantCulture)}\" alt=\"{alt.ToString(CultureInfo.InvariantCulture)}\" />";

            ushort crc = ComputeCRC(service);
            string xml = $"<SRV>{service}<crc>{crc}</crc></SRV>";

            try
            {
                if (serialPort != null && serialPort.IsOpen)
                {
                    lock (_serialIoLock)
                    {
                        serialPort.Write(xml);
                        serialPort.Write(serialPort.NewLine);
                    }
                }
                else
                {
                    Console.WriteLine("[TX][SRV] Serial port is not open. Skipping SRV transmit.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TX][SRV] Error sending SRV over serial: " + ex.Message);
            }

            if (_timeshiftEnabled)
            {
                recordedSrvMessages.Add(xml);
            }

            try
            {
                var parsed = V2XMessageParser.ParseV2XMessage(xml);
                HandleV2XMessage(parsed, xml);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[SRV] Local parse failed: " + ex.Message);
            }

            Console.WriteLine("[TX][SRV] " + xml);
        }


        /// <summary>
        /// Send test SRV messages periodically (default: every minute).
        /// </summary>

        private void StartSrvAutoTimerIfEnabled()
        {
            if (SrvCheckBox?.IsChecked != true)
                return;

            if (_srvTimer == null)
            {
                _srvTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _srvTimer.Tick += (s, e) => SendSrvMessage();
            }
            if (!_srvTimer.IsEnabled)
                _srvTimer.Start();
        }


        /// <summary>
        /// Stops timer for auto sending messages.
        /// </summary>
        private void StopSrvAutoTimer()
        {
            if (_srvTimer != null)
                _srvTimer.Stop();
        }


        /// <summary>
        /// Update UI enabled states.
        /// </summary>
        private void UpdateUiEnabledState()
        {
            // Connected-gated (serial)
            Disconnect?.SetValue(IsEnabledProperty, _isConnected);
            SendSrv?.SetValue(IsEnabledProperty, _isConnected);
            SrvCheckBox?.SetValue(IsEnabledProperty, _isConnected);

            Connect?.SetValue(IsEnabledProperty, !_isConnected);

            // COM/baud
            ComPortsComboBox?.SetValue(IsEnabledProperty, !_isConnected);
            if (BaudrateTB != null)
            {
                BaudrateTB.IsReadOnly = _isConnected;
                BaudrateTB.IsEnabled = !_isConnected;
            }
            RefreshComPorts?.SetValue(IsEnabledProperty, !_isConnected);

            // Drawing and tram simulation
            AccuracyCB?.SetValue(IsEnabledProperty, _isConnected);
            FilterTram?.SetValue(IsEnabledProperty, _isConnected);

            // Manual recording buttons
            if (btnStartRecording != null)
                btnStartRecording.IsEnabled = _isConnected && !isRecording;
            if (btnStopRecording != null)
                btnStopRecording.IsEnabled = _isConnected && isRecording;

            // Classic replay controls
            PlayMovement?.SetValue(IsEnabledProperty, _playbackLoaded);
            LoopCheckbox?.SetValue(IsEnabledProperty, _playbackLoaded);
            StopReplay?.SetValue(IsEnabledProperty, _playbackLoaded);
            ReloadReplay?.SetValue(IsEnabledProperty, _playbackLoaded);
            ReplaySlider?.SetValue(IsEnabledProperty, _playbackLoaded);
            TramBox?.SetValue(IsEnabledProperty, _playbackLoaded);

            // Timeshift controls
            btnPlay?.SetValue(IsEnabledProperty, _playbackLoaded);
            btnPause?.SetValue(IsEnabledProperty, _playbackLoaded);
            FilterCheckBox?.SetValue(IsEnabledProperty, _isConnected || _playbackLoaded);
            ExportButton?.SetValue(IsEnabledProperty, ActivationZonesCollection != null && ActivationZonesCollection.Count > 0);
            CircleCheckBox?.SetValue(IsEnabledProperty, _isConnected);
            PrevCam?.SetValue(IsEnabledProperty, _playbackLoaded);
            NextCam?.SetValue(IsEnabledProperty, _playbackLoaded);
        }


        /// <summary>
        /// Reproject activation zones while dragging.
        /// </summary>
        public void ReprojectActivationZonesOnMapChange()
        {
            // Reproject all non-switch zones from geo -> canvas, using per-zone latitude for MPP
            foreach (var zone in ActivationZonesCollection.Where(z => z != null && !IsSwitchZone(z)))
            {
                if (zone.Rectangle == null) continue;

                // meters -> pixels at current zoom (local latitude for better accuracy)
                double mppLocal = MetersPerPixel(zone.Latitude, zoom);
                double widthPx = zone.Width / mppLocal;
                double heightPx = zone.Height / mppLocal;

                zone.Rectangle.Width = widthPx;
                zone.Rectangle.Height = heightPx;

                // Start point from stored Lat/Lon
                var (sx, sy) = ConvertLatLonToCanvasXY(zone.Latitude, zone.Longitude);
                zone.StartPoint = new Point(sx, sy);

                UpdateRectanglePositionFromStartPoint(zone); // anchors to base center and re-applies rotation
                UpdateActivationZoneBounds(zone);

                Panel.SetZIndex(zone.Rectangle, 100);
                if (!TileCanvas.Children.Contains(zone.Rectangle))
                    TileCanvas.Children.Add(zone.Rectangle);
            }
        }


        /// <summary>
        /// Reprojects active vehicles if the map changes (panning, zooming, etc.)
        /// </summary>
        private void ReprojectActiveVehiclesOnMapChange()
        {
            foreach (var kv in activeVehicles)
            {
                var id = kv.Key;
                var mp = kv.Value;
                if (!_lastLatLon.TryGetValue(id, out var ll)) continue;

                var (x, y) = ConvertLatLonToCanvasXY(ll.lat, ll.lon);
                bool isSrv = string.Equals(mp.Ellipse?.Tag?.ToString(), "Srv", StringComparison.OrdinalIgnoreCase);
                var color = mp.VehicleColor ?? mp.Ellipse?.Fill ?? Brushes.Black;

                UpdateVehicleCanvasPosition(mp, new Point(x, y), color, isSrv, mp.Label);

                Panel.SetZIndex(mp.Ellipse, 1000);
                if (mp.Text != null) Panel.SetZIndex(mp.Text, 1000);

                if (!isSrv && _lastHeadingLive.TryGetValue(id, out var hdg))
                {
                    var liveHeadingAdj = (hdg - 180 + 360) % 360;
                    UpdateOrCreateVehicleBox(id, new Point(x, y), color, liveHeadingAdj);
                }
            }
        }


        /// <summary>
        /// Reprojects all zones if the map changes (panning, zooming, etc.)
        /// </summary>
        public async Task ReprojectAllZonesOnMapChange()
        {
            ReprojectActivationZonesOnMapChange();
            ReprojectSwitchZonesOnMapChange();
            ReprojectActiveVehiclesOnMapChange();
            ReprojectReplayOnMapChange();
            ReprojectDrawnTramsOnMapChange();
            ReprojectPolylines();
            UpdatePolylinePositions();
            DrawStopsOnCanvasSafe();
            UpdateTramSignalPositions();
            await BringAllOverlaysToFrontSafeAsync();
        }

        // place near other reproject methods
        private void ReprojectReplayOnMapChange()
        {
            if (_replayGeoFrames.Count == 0) return;

            // Rebuild canvas-space frames from stored geo
            _replayFrames.Clear();
            foreach (var kv in _replayGeoFrames)
            {
                var id = kv.Key;
                var geo = kv.Value;
                var frames = new List<MovementFrame>(geo.Count);
                foreach (var g in geo)
                {
                    var (x, y) = ConvertLatLonToCanvasXY(g.lat, g.lon);
                    frames.Add(new MovementFrame { Timestamp = g.ts, Position = new Point(x, y) });
                }
                _replayFrames[id] = frames;
            }

            // Redraw current replay visuals at the same time index
            RedrawPlaybackToTime(playbackElapsedTime);
        }


        /// <summary>
        /// Redraws polyline.
        /// </summary>
        private void ReprojectPolylines()
        {
            UpdatePolylinePositions();
        }


        /// <summary>
        /// Redraws trams if the user is panning, zooming, etc.
        /// </summary>
        private void ReprojectDrawnTramsOnMapChange()
        {
            for (int idx = 0; idx < drawnTrams.Length; idx++)
            {
                var tram = drawnTrams[idx];

                // Do we have anything to render for this slot?
                bool hasGeo = (drawnTramLat[idx].HasValue && drawnTramLon[idx].HasValue)
                              || drawnTramTrailGeoPoints[idx].Count > 0;

                // Lazily create MapPoint when we have geo but no visuals yet
                if (tram == null)
                {
                    if (!hasGeo)
                        continue;

                    tram = new MapPoint
                    {
                        Label = drawnTramIds[idx],
                        VehicleColor = drawnTramColors[idx],
                        TrailDots = new List<Ellipse>(),
                        MovementFrames = new List<MovementFrame>(),
                        IsRecorded = true,
                        LastUpdate = DateTime.Now
                    };
                    drawnTrams[idx] = tram;
                }

                // Ensure ellipse/text exist
                EnsureMapPointVisuals(tram, drawnTramColors[idx], isSrv: false);

                // Reposition to last known geo
                if (drawnTramLat[idx].HasValue && drawnTramLon[idx].HasValue)
                {
                    var (x, y) = ConvertLatLonToCanvasXY(drawnTramLat[idx]!.Value, drawnTramLon[idx]!.Value);
                    UpdateVehicleCanvasPosition(tram, new Point(x, y), drawnTramColors[idx], false, tram.Label);
                    if (tram.Ellipse != null) Panel.SetZIndex(tram.Ellipse, 1000);
                    if (tram.Text != null) Panel.SetZIndex(tram.Text, 1000);

                    // Update body heading if we have at least 2 geo points
                    if (drawnTramTrailGeoPoints[idx].Count >= 2)
                    {
                        var (plat, plon) = drawnTramTrailGeoPoints[idx][^2];
                        var (px, py) = ConvertLatLonToCanvasXY(plat, plon);
                        var headingDeg = CalculateAzimuth(new Point(px, py), new Point(x, y));
                        //headingDeg = (headingDeg - 180 + 360) % 360; // keep manual flip rule
                        UpdateOrCreateVehicleBox(drawnTramIds[idx], new Point(x, y), drawnTramColors[idx], headingDeg);
                    }
                    else
                    {
                        if (_vehicleBoxes.TryGetValue(drawnTramIds[idx], out var rect))
                        {
                            TileCanvas.Children.Remove(rect);
                            _vehicleBoxes.Remove(drawnTramIds[idx]);
                        }
                    }
                }

                // Rebuild manual trail from geo points
                if (drawnTramTrailGeoPoints[idx].Count > 0)
                {
                    if (drawnTramTrails[idx] == null)
                    {
                        drawnTramTrails[idx] = new Polyline
                        {
                            Stroke = drawnTramColors[idx],
                            StrokeThickness = 2,
                            IsHitTestVisible = false
                        };
                        TileCanvas.Children.Add(drawnTramTrails[idx]);
                    }

                    var pl = drawnTramTrails[idx];
                    pl.Points.Clear();

                    // Clear old dots safely
                    if (tram.TrailDots == null) tram.TrailDots = new List<Ellipse>();
                    foreach (var d in tram.TrailDots.ToList())
                        TileCanvas.Children.Remove(d);
                    tram.TrailDots.Clear();

                    for (int i = 0; i < drawnTramTrailGeoPoints[idx].Count; i++)
                    {
                        var (lat, lon) = drawnTramTrailGeoPoints[idx][i];
                        var (tx, ty) = ConvertLatLonToCanvasXY(lat, lon);
                        pl.Points.Add(new Point(tx, ty));

                        // black dot at intermediate points
                        if (i < drawnTramTrailGeoPoints[idx].Count - 1)
                        {
                            var dot = new Ellipse
                            {
                                Width = 5,
                                Height = 5,
                                Fill = Brushes.Black,
                                IsHitTestVisible = false
                            };
                            Canvas.SetLeft(dot, tx - 2.5);
                            Canvas.SetTop(dot, ty - 2.5);
                            TileCanvas.Children.Add(dot);
                            Panel.SetZIndex(dot, 1001);
                            tram.TrailDots.Add(dot);
                        }
                    }

                    Panel.SetZIndex(pl, 999);
                }
            }
        }


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
            if (e.Key == Key.Enter && _pendingNewZone != null)
            {
                // Commit current editor values first
                ActivationZonesDataGrid?.CommitEdit(DataGridEditingUnit.Cell, true);
                ActivationZonesDataGrid?.CommitEdit(DataGridEditingUnit.Row, true);

                TryFinalizePendingNewZone();
                e.Handled = true; // prevent default Enter navigation
            }
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
                    "The new activation/switch zone is missing required properties.\n\n" +
                    "Required: Name, Latitude, Longitude, Azimuth (0–359), Width (>0), Height (>0).",
                    "Incomplete row",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.No)
                {
                    ActivationZonesCollection.Remove(zone);
                    _switchRows.Remove(zone);
                    _pendingNewZone = null;
                    return true;
                }

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
                    var prevZone = activationZones.Values.FirstOrDefault(z => ReferenceEquals(z.Rectangle, _highlightedRect));
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

        private void EmphasizeZoneWithOwnColor(ActivationZone zone, TimeSpan? revertAfter = null)
        {
            if (zone?.Rectangle == null) return;

            var rect = zone.Rectangle;
            var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;

            rect.Stroke = brush;

            bool isSelected = _highlightedRect == rect;

            rect.StrokeThickness = 6;

            if (!isSelected)
                rect.Fill = MakeAlphaBrush((SolidColorBrush)brush, 40);

            if (revertAfter.HasValue && !isSelected)
            {
                var t = new DispatcherTimer { Interval = revertAfter.Value };
                t.Tick += (s, e) =>
                {
                    ((DispatcherTimer)s).Stop();
                    if (zone.Rectangle != null && !zone.IsActive)
                    {
                        zone.Rectangle.StrokeThickness = 2;
                        zone.Rectangle.Fill = Brushes.Transparent;
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

        // ADD this helper near ValidateSubzoneContinuity


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
                    "The order of zones is not valid:\n\n" + errors,
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
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (res == MessageBoxResult.No)
                {
                    SwitchZonesCollection.Remove(zone);
                    _pendingNewSwitchZone = null;
                    return true;
                }

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
                Console.WriteLine($"[ROTATE] Zone '{zone.Name}' rotated to {zone.Azimuth}°");
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

            // ✅ Calculate arrow position relative to rectangle's unrotated position
            double arrowLeft = left + (w - arrowW) / 2.0;
            double arrowTop = top + Math.Max(4.0, h * 0.06);

            var brush = TryBrushFromColor(zone.Color) ?? Brushes.Gray;
            arrow.Fill = brush;
            arrow.Stroke = brush;

            // ✅ Apply the same rotation as the rectangle, but adjust the center point
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
                            // ✅ Remove arrow before removing rectangle
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
            int desiredArrows = 4;
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
                    recordedSrvMessages.Add(fakeXml);
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
        private void TestProtobuf_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // První kliknutí - dekódovat a uložit zprávu
                if (string.IsNullOrEmpty(_protobufTestDecoded))
                {
                    string testMessage = "COvDAxILCJrg8c8GEKTWiiFShwEKCwia4PHPBhCk1oohEhAKCjIzNTg5NDUwMTcQPBgKUjoKCQkAAACgmRlsQBEBP/Tu2etIQBnCNdKtMkgyQFIbUB6iAQUN4XqkQJIDDgoFDeF6pEASBQ3heqRAXTMzP0FlZuaqQ6UBuKeAQ/IBGQgUogEEMTMzOfIBBDIwMDSABQDCDAPwAQA=";

                    if (!ProtobufParser.TryDecodeProtobufFromHex(testMessage, out _protobufTestDecoded))
                    {
                        Console.WriteLine("ERROR: Failed to decode");
                        return;
                    }

                    Console.WriteLine("Click to toggle tram position");
                    _protobufTestLatOffset = 0.0;
                    _protobufTestDirection = true;
                }

                // Parsovat zprávu
                var protoCam = ProtoCam.ParseFromJson(_protobufTestDecoded);
                if (protoCam == null)
                {
                    Console.WriteLine("ERROR: Parse failed");
                    return;
                }

                // Přepnout směr
                _protobufTestDirection = !_protobufTestDirection;

                double jumpMeters = 50.0;
                double latDelta = jumpMeters / 111000.0;

                if (_protobufTestDirection)
                {
                    _protobufTestLatOffset += latDelta;
                }
                else
                {
                    _protobufTestLatOffset -= latDelta;
                }

                // Aplikovat offset
                protoCam.Latitude = (protoCam.Latitude ?? 0.0) + _protobufTestLatOffset;
                protoCam.Timestamp = DateTime.UtcNow;
                protoCam.Speed = 15.0;
                protoCam.Heading = _protobufTestDirection ? 0.0 : 180.0;

                // Převést a vykreslit
                var v2xMsg = protoCam.ToV2XMessage();
                v2xMsg.MessageType = "CAM";

                var fakeXml = $@"<vehPt lat=""{v2xMsg.Latitude}"" lon=""{v2xMsg.Longitude}"" speed=""{v2xMsg.Speed}"" heading=""{v2xMsg.Heading}"" accuracy=""{protoCam.AccuracyInMeters ?? 0.0}"" />";

                var shortId = v2xMsg.VehicleID.Length > 4 ? v2xMsg.VehicleID[^4..] : v2xMsg.VehicleID;
                var arrow = _protobufTestDirection ? "▲" : "▼";
                Console.WriteLine($"{arrow} JUMP {jumpMeters}m | {shortId} | Lat={v2xMsg.Latitude:F6} | Lon={v2xMsg.Longitude} | Total delta={_protobufTestLatOffset * 111000:F0}m");

                // Vykreslit na mapu
                HandleV2XMessage(v2xMsg, fakeXml);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: {ex.Message}", Brushes.Red);
            }
        }

        public void StartProtobufTest(string base64Message)
        {
            // Už nepotřebujeme - používáme TestProtobuf_Click
        }

        public void StopProtobufTest()
        {
            _protobufTestDecoded = "";
            _protobufTestLatOffset = 0.0;
            _protobufTestDirection = true;
        }

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

            Console.WriteLine($"[SIGNAL LIVE] Intersection {intersectionId} → {direction}");

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

                Console.WriteLine("[SIGNAL PARSE] No matching state → None");
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

    }

}

public class PolylinePointData
{
    public int VertexIndex { get; set; }
    public Point CanvasPosition { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTime Timestamp { get; set; } 
}

public class PolylineData
{
    public Guid PolylineId { get; set; }
    public List<PolylinePointData> Vertices { get; set; } = new List<PolylinePointData>();
    public List<PolylineSegmentData> Segments { get; set; } = new List<PolylineSegmentData>();
    public DateTime CreatedAt { get; set; }
    public double TotalLengthMeters { get; set; }
    public string ColorHex { get; set; } = "#000000";
}

public class PolylineSegmentData
{
    public int SegmentIndex { get; set; }
    public PolylinePointData? StartPoint { get; set; }
    public PolylinePointData? EndPoint { get; set; }
    public double LengthMeters { get; set; }
    public int AzimuthDegrees { get; set; }
    public double WidthMeters { get; set; }
    public string SegmentType { get; set; } = "";
}