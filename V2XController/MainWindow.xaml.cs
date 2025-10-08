using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using System.Collections.Specialized;
using System.Text.Json;
using System.Windows.Data;
using System.Collections.Concurrent;
using System.Windows.Documents;
using System.Windows.Media.Animation;
using ComCommon;
using ModbusNewLib;
using Logger;

//TODO:


namespace V2XController
{

    public partial class MainWindow : Window
    {
        //||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        // all the variables and objects used in the application

        //!!!!!!PORT and BAUDRATE!!!!!!!!!
        private SerialPort serialPort = new SerialPort("COM10", 57600);
        private readonly object _serialIoLock = new();

        //Everything for the map 
        private int zoom = 18;
        private double latitude = 49.842432;
        private double longitude = 18.276736;

        //Grid
        private const int TileSize = 256; //in px (map tile is 256x256 px)
        const int TileCount = 3;
        const int CanvasSize = 694;
        const int TilePixelSize = CanvasSize / TileCount;

        private int _currentTopLeftTileX;
        private int _currentTopLeftTileY;

        //tram table
        public ObservableCollection<TramInfo> TramTable { get; set; }
        private Dictionary<string, DateTime> lastCamTimes = new();
        private Dictionary<string, DateTime> prevCamTimes = new();

        //TIME ZONE
        TimeZoneInfo czechTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");

        //points
        private Point DragStart;
        private TranslateTransform translate = new TranslateTransform();
        private ScaleTransform scale = new ScaleTransform(1, 1);
        private TransformGroup transformGroup = new TransformGroup();


        //drawing
        private List<MapRectangle> mapRectangles = new List<MapRectangle>();
        private MapRectangle _currentMapRectangle;

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

        private Line tempHeightLine;
        private Line tempWidthLine;
        private Polygon previewRect;
        private Ellipse startPointEllipse;
        private Ellipse secondPointEllipse;

        private List<string> recordedCamMessages = new();

        private enum DrawingMode
        {
            None,
            Rectangle,
            Railway,
            Point           //drawing selection
        }

        private DrawingMode currentDrawingMode = DrawingMode.None;
        private bool isDrawing = false;
        private Rectangle currentRect;
        private Point startPoint;
        private Point lineStartPoint;
        private Line currentLine;
        private List<Line> railwayLines = new List<Line>();

        private List<Point> trailPoints = new List<Point>();
        private Polyline tramTrail;

        private Brush _strokeBrush = Brushes.Red; //default brush color

        private List<MapPoint> points = new();
        private Polyline connectionLine = new Polyline
        {
            Stroke = Brushes.Red,
            StrokeThickness = 2
        };

        private Point? _rectangleStartPoint = null;
        private Rectangle? _currentRectangle = null;
        private TextBlock? _currentSizeLabel = null;
        private bool isSelectionMode = false;
        private Rectangle selectedRectangle = null;


        private MapPoint[] drawnTrams = new MapPoint[2]; // 0 = X, 1 = C
        private int currentDrawnTramIndex = 0; // 0 = X, 1 = C
        private string[] drawnTramIds = new[] { "0000009999", "0000001111" };
        private string[] drawnTramNames = new[] { "tram-test1", "tram-test2" };
        private Brush[] drawnTramColors = new[] { Brushes.Red, Brushes.Blue };
        private Polyline[] drawnTramTrails = new Polyline[2];
        private List<Point>[] drawnTramTrailPoints = new List<Point>[2] { new List<Point>(), new List<Point>() };
        private readonly Dictionary<string, CancellationTokenSource> vehicleTrailCleanupTokens = new();

        //moving objects (RMB)
        private UIElement selectedElement = null;
        private Point mouseOffset;

        //rectangle dimensions textblock
        private TextBlock dimensionTextBlock;

        private Stack<Action> undoStack = new();
        private Stack<Action> redoStack = new();

        //recording movement
        private bool isPlaying = false;
        private DateTime playbackStartTime;
        private DispatcherTimer playbackTimer;
        private TimeSpan playbackElapsedTime;
        private bool isRecording = false;
        private DateTime recordingStartTime;

        private Dictionary<string, MapPoint> activeVehicles = new();

        //panning
        private Point panStart;
        private bool isPanning = false;
        private Point middleMousePanStart;
        private bool isMiddleMousePanning = false;

        private string[] controls = {
            "Zoom: MWheel",
            "Pan: Press MWhell (MB3)",
            "Draw: LMB",
            "Move objects: RMB",
            "Delete objects: RMB + Del",
            "Stop drawing: ESC",
            "Undo: Ctrl + Z",
            "Redo: Ctrl + Y"
        };
        private string ControlsMessage => string.Join(" \n", controls);

        //CAM STATUSES
        int camOkCount = 0;
        int camErrorCount = 0;
        private V2XMessage? lastV2XMessage = null;


        //SRV STATUSES
        SRVMessage msg;
        int srvOkCount = 0;
        int srvErrorCount = 0;
        private double? srvLatitude = null;
        private double? srvLongitude = null;


        //Activation zones
        private Dictionary<Rectangle, ActivationZone> activationZones = new();
        private Ellipse radiusEllipse;
        public ObservableCollection<ActivationZone> ActivationZonesCollection { get; set; } = new();


        //dict for last cam updates 
        Dictionary<string, DateTime> lastCamUpdates = new Dictionary<string, DateTime>();
        private DispatcherTimer cleanupTimer;

        //colors for tram points
        private readonly List<Brush> vehicleColors = new List<Brush>
        {
            Brushes.DarkViolet,
            Brushes.Blue,
            Brushes.Green,
            Brushes.Orange,
            Brushes.Purple,
            Brushes.Cyan,
            Brushes.Magenta,
            Brushes.Brown,
            Brushes.Yellow,
        };
        private readonly Dictionary<string, Brush> vehicleColorMap = new Dictionary<string, Brush>();

        //timer for cam updates (seconds)
        private DispatcherTimer camTimer;

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

        // fields – přidej k ostatním timerům
        private DispatcherTimer _srvTimer;

        private bool _isConnected = false;
        private bool _playbackLoaded = false;



        // pod stávajícími poli pro drawnTram*
        private double?[] drawnTramLat = new double?[2];
        private double?[] drawnTramLon = new double?[2];
        private List<(double lat, double lon)>[] drawnTramTrailGeoPoints = new List<(double lat, double lon)>[2]
        {
             new List<(double lat, double lon)>(),
             new List<(double lat, double lon)>()
        };


        private List<TimeSpan> _keyframes = new();
        private int _playbackIndex = 0;


        // debounce zoom preview
        private DispatcherTimer _zoomDebounceTimer;
        private int _pendingZoom;
        private Point _lastWheelPos;

        // playback helpers
        private Dictionary<string, double> _playbackSpeedByIdAndTs = new(); // key = $"{vehId}|{ts.Ticks}"


        private ActivationZone _pendingNewZone;

        // near other fields controlling playback state
        private string _lastReplayFile;

        // near other fields controlling playback state
        private bool _isPlaybackSessionActive = false;


        // near other fields (playback/recording)
        private bool _timeshiftEnabled = false;       // recording continuously after connect
        private bool _timeshiftPaused = false;        // pause suppresses live rendering but keeps buffering
        private bool _suppressLiveRender = false;     // gate for HandleV2XMessage during timeshift pause
        private DateTime _timeshiftStartUtc;          // session start (after Connect)
        private DispatcherTimer _timeshiftUiTimer;    // updates slider while live
        private DateTime? _markInUtc = null;          // export range start
        private DateTime? _markOutUtc = null;

        // global buffers
        private List<string> recordedManualCamMessages = new();     // manual: only simulated (drawn) CAMs while recording

        // near other timeshift fields
        private bool _isTimeshiftPlaybackActive;           // catch-up playback running
        private CancellationTokenSource _timeshiftPlaybackCts;
        private bool _timeshiftFollowLive;                 // auto-follow live edge when true


        private bool isReplaySliderDragging = false;
        private bool wasPlayingBeforeReplayDrag = false;

        private Rectangle _highlightedRect;
        private Brush _highlightedRectOldBrush;
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
        private ActivationZone _pendingNewSwitchZone;

        // Add near other fields
        private bool _drawToSwitchZones;

        // add near other fields
        private readonly HashSet<ActivationZone> _switchRows = new();

        // helper: current drawing/adding mode comes from radio buttons
        private bool IsSwitchMode() => SwitchRadio?.IsChecked == true;

        // helper: per-row type detection (prefer Tag, fallback to Name prefix)
        private static bool IsSwitchZone(ActivationZone z) =>
            z != null &&
            (string.Equals(z?.Rectangle?.Tag as string, "SwitchZone", StringComparison.OrdinalIgnoreCase)
             || (!string.IsNullOrWhiteSpace(z?.Name) && z.Name.StartsWith("Switch", StringComparison.OrdinalIgnoreCase)));

        public List<Stop> stops = new List<Stop>();

        private TerminalWindow _terminalWindow;
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
        private CancellationTokenSource _tilesCts;

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
        private int baseTileX;
        private int baseTileY;




        //||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        //Main window constructor
        public MainWindow()
        {
            InitializeComponent();
            this.WindowStyle = WindowStyle.SingleBorderWindow;
            this.ResizeMode = ResizeMode.CanMinimize;

            LoadAvailableComPorts();
            Console.WriteLine("Application started.");

            // main data context
            this.DataContext = this;

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

            // in constructor, extend the Loaded handler
            // v konstruktoru, v Loaded handleru, přidej první řádek:
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
            _ = LoadTilesSmoothAsync(centerX - TileCount / 2, centerY - TileCount / 2);


            //Mouse wheel events for zooming
            TileCanvas.MouseWheel += TileCanvas_MouseWheel;
            this.MouseWheel += Window_MouseWheel;

            //records table (not in the app anymore)

            ActivationZonesCollection.CollectionChanged += ActivationZonesCollection_CollectionChanged;

            //csv loading
            var filePath = "export.csv";
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

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
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

        private void LoadAvailableComPorts()
        {
            ComPortsComboBox.Items.Clear();
            var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();
            foreach (var port in ports)
                ComPortsComboBox.Items.Add(port);

            if (ComPortsComboBox.Items.Count > 0)
                ComPortsComboBox.SelectedIndex = 0;
        }

        // add inside class MainWindow (near other small helpers)
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

        //calculating tile location
        private static (int tileX, int tileY) LatLonToTileXY(double lat, double lon, int zoom)
        {
            int tileX = (int)Math.Floor((lon + 180.0) / 360.0 * (1 << zoom));
            double latRad = lat * Math.PI / 180.0;
            int tileY = (int)Math.Floor(
                (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * (1 << zoom)
            );
            return (tileX, tileY);
        }


        //Position on the map
        public (double x, double y) ConvertLatLonToCanvasXY(double lat, double lon)
        {
            // fractional tile coords
            double u = LonToTileX(lon, zoom);
            double v = LatToTileY(lat, zoom);

            double x = (u - _currentTopLeftTileX) * TilePixelSize;
            double y = (v - _currentTopLeftTileY) * TilePixelSize;
            return (x, y);
        }


        //CONVERT PIXELS TO LAT/LON
        // Replace this method body
        public (double Latitude, double Longitude) ConvertCanvasXYToLatLon(double x, double y, int zoom)
        {
            // Convert canvas pixel (relative to current tile grid) to lon/lat using current top-left tile anchor
            var lonlat = CanvasPixelsToLatLon(new Point(x, y), latitude, longitude, zoom); // returns Point(lon, lat)
            return (lonlat.Y, lonlat.X);
        }

        // Loading map tiles on the grid
        private async Task LoadTilesSmoothAsync(int startX, int startY, double offsetX = 0, double offsetY = 0)
        {
            _tileCts?.Cancel();
            _tileCts?.Dispose();
            _tileCts = new CancellationTokenSource();
            var ct = _tileCts.Token;

            try
            {
                _currentTopLeftTileX = startX;
                _currentTopLeftTileY = startY;

                isDrawing = false;
                currentRect = null;
                _currentMapRectangle = null;

                var order = GenerateSpiralOrder(TileCount);
                var newImages = new List<Image>(TileCount * TileCount);
                var tasks = new List<Task>();

                for (int idx = 0; idx < order.Count; idx++)
                {
                    if (ct.IsCancellationRequested) break;

                    var (offX, offY) = order[idx];
                    int tileX = startX + (offX + TileCount / 2);
                    int tileY = startY + (offY + TileCount / 2);

                    // vizuální pozice se zohledněním offsetu
                    double canvasX = (offX + TileCount / 2) * TilePixelSize + offsetX;
                    double canvasY = (offY + TileCount / 2) * TilePixelSize + offsetY;

                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await _tileSemaphore.WaitAsync(ct).ConfigureAwait(false);
                            var bmp = await FetchTileAsync(zoom, tileX, tileY, ct).ConfigureAwait(false);

                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (ct.IsCancellationRequested) return;

                                Image img = CreateTileImage(bmp);
                                Canvas.SetLeft(img, canvasX);
                                Canvas.SetTop(img, canvasY);
                                TileCanvas.Children.Add(img);
                                newImages.Add(img);
                            });
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Load tile {zoom}/{tileX}/{tileY} failed: {ex.Message}");
                        }
                        finally
                        {
                            _tileSemaphore.Release();
                        }
                    }, ct);

                    tasks.Add(task);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                if (!ct.IsCancellationRequested)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var toRemove = TileCanvas.Children
                            .OfType<Image>()
                            .Where(img => !newImages.Contains(img))
                            .ToList();

                        foreach (var r in toRemove)
                            TileCanvas.Children.Remove(r);

                        // Overlayy zpět nahoru
                        if (!TileCanvas.Children.Contains(connectionLine)) TileCanvas.Children.Add(connectionLine);

                        foreach (var pt in points)
                        {
                            if (!TileCanvas.Children.Contains(pt.Ellipse)) TileCanvas.Children.Add(pt.Ellipse);
                            if (!TileCanvas.Children.Contains(pt.Text)) TileCanvas.Children.Add(pt.Text);
                        }

                        foreach (var rect in mapRectangles)
                        {
                            if (!TileCanvas.Children.Contains(rect.Shape)) TileCanvas.Children.Add(rect.Shape);
                            Panel.SetZIndex(rect.Shape, 100);
                        }

                        foreach (var pt in points)
                        {
                            Panel.SetZIndex(pt.Ellipse, 200);
                            Panel.SetZIndex(pt.Text, 201);
                        }

                        Panel.SetZIndex(connectionLine, 150);
                    });

                    ReprojectAllZonesOnMapChange();
                    ReprojectDrawnTramsOnMapChange();
                    ReprojectActiveVehiclesOnMapChange();
                    ReprojectReplayOnMapChange();
                    DrawStopsOnCanvasSafe();
                    await BringAllOverlaysToFrontSafeAsync();
                }
            }
            catch (TaskCanceledException) { }
        }


        private async Task<BitmapSource?> FetchTileAsync(int z, int x, int y, CancellationToken ct)
        {
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

                _tileCache[(z, x, y)] = bmp;
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
            var fade = new DoubleAnimation(0.0, 1.0, new Duration(TimeSpan.FromMilliseconds(250)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            img.BeginAnimation(UIElement.OpacityProperty, fade);

            return img;
        }

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



        private void TileCanvas_MouseWheel(object sender, MouseWheelEventArgs e) { }

        // Prefer SRV (RSU) polohu, fallback střed mapy; lazy fetch + cache
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

        // Altitude providers: OpenTopodata first, then Open‑Meteo fallback
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

        // Extrakce alt z CAM XML (<vehPt ... alt="123.4" .../>)
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

        // Live CAM: true = filtrovat (nevykreslit), false = povolit
        private bool TryFilterCamByAltitude(string rawXml)
        {
            if (FilterCheckBox?.IsChecked != true) return false;

            // kick-off fetch, but don't block UI
            _ = EnsureLocalAreaAltitudeAsync();

            if (!_localAltitudeMeters.HasValue) return false;
            if (!TryExtractAltitudeFromCamXml(rawXml, out var camAlt)) return false;

            return Math.Abs(camAlt - _localAltitudeMeters.Value) > 50.0;
        }

        // Replay: filter by altitude OR invalid ID only when checkbox is ON
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

        private static bool IsInvalidVehicleId(string vehicleId)
        {
            if (string.IsNullOrWhiteSpace(vehicleId)) return true;
            if (vehicleId.Length < 10) return true;

            string shortId = vehicleId.Length >= 4 ? vehicleId[^4..] : vehicleId;
            if (int.TryParse(shortId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return n >= 3000;

            return true;
        }

        // Helper: apply the ID rule only when the checkbox is ON
        private bool ShouldFilterLiveById(string vehicleId)
        {
            return (FilterCheckBox?.IsChecked == true) && IsInvalidVehicleId(vehicleId);
        }

        //zooming on the map
        private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            int delta = e.Delta > 0 ? 1 : -1;
            int newZoom = Math.Clamp(zoom + delta, 1, 18);
            _pendingZoom = newZoom;

            // vizuální náhled zoomu pomocí ScaleTransform
            _lastWheelPos = e.GetPosition(TileCanvas);
            scale.CenterX = _lastWheelPos.X;
            scale.CenterY = _lastWheelPos.Y;
            // poměr mezi cílovým a aktuálním zoomem (OSM používá 2x škálování na jeden level)
            double previewScale = Math.Pow(2, _pendingZoom - zoom);
            scale.ScaleX = previewScale;
            scale.ScaleY = previewScale;

            // debounce commit (načtení dlaždic) po krátké pauze
            if (_zoomDebounceTimer == null)
            {
                _zoomDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                _zoomDebounceTimer.Tick += async (s, ev) =>
                {
                    _zoomDebounceTimer.Stop();

                    zoom = _pendingZoom;
                    scale.ScaleX = 1;
                    scale.ScaleY = 1;

                    var (centerX, centerY) = LatLonToTileXY(latitude, longitude, zoom);
                    await LoadTilesSmoothAsync(centerX - TileCount / 2, centerY - TileCount / 2);


                    // Also refresh altitude for new center/zoom area
                    _ = EnsureLocalAreaAltitudeAsync(force: true);

                    ResetAllTramTrails();
                    ReprojectActiveVehiclesOnMapChange();
                    ReprojectReplayOnMapChange();
                    DrawRadiusCircle();

                    if (lastV2XMessage != null)
                    {
                        UpdateVehicleTrail(lastV2XMessage);
                    }
                };
            }
            else
            {
                _zoomDebounceTimer.Stop();
            }

            _zoomDebounceTimer.Start();
        }

        // Dragging the map with MMB
        private void TileCanvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                middleMousePanStart = e.GetPosition(this);
                isMiddleMousePanning = true;
                TileCanvas.CaptureMouse();
                e.Handled = true;
            }
        }

        // MouseMove - continuous pan and tile loading
        private void TileCanvas_MouseMove_MiddlePan(object sender, MouseEventArgs e)
        {
            if (!isMiddleMousePanning) return;

            Point current = e.GetPosition(this);
            Vector delta = current - middleMousePanStart;

            tileOffsetX += delta.X;
            tileOffsetY += delta.Y;

            // Posun všech dlaždic o aktuální delta
            foreach (var img in TileCanvas.Children.OfType<Image>())
            {
                Canvas.SetLeft(img, Canvas.GetLeft(img) + delta.X);
                Canvas.SetTop(img, Canvas.GetTop(img) + delta.Y);
            }

            // Overlay body
            MoveOverlays(delta.X, delta.Y);

            middleMousePanStart = current;

            // Pokud offset překročil velikost dlaždice, posuň top-left index a resetni offset
            int shiftX = 0, shiftY = 0;

            if (Math.Abs(tileOffsetX) >= TilePixelSize)
            {
                shiftX = (int)(tileOffsetX / TilePixelSize);
                _currentTopLeftTileX -= shiftX;
                tileOffsetX -= shiftX * TilePixelSize;
            }

            if (Math.Abs(tileOffsetY) >= TilePixelSize)
            {
                shiftY = (int)(tileOffsetY / TilePixelSize);
                _currentTopLeftTileY -= shiftY;
                tileOffsetY -= shiftY * TilePixelSize;
            }

            // Načíst nové dlaždice jen pokud posun o celou dlaždici
            if (shiftX != 0 || shiftY != 0)
            {
                _ = LoadTilesSmoothAsync(_currentTopLeftTileX, _currentTopLeftTileY);
            }

            e.Handled = true;
        }

        //moving overlays

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
        }


        // Shift all Image tiles by tile offset
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


        // MouseUp - commit pan
        private async void TileCanvas_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle) return;

            isMiddleMousePanning = false;
            TileCanvas.ReleaseMouseCapture();

            // Logický střed mapy podle aktuálních dlaždic
            var canvasCenter = new Point(CanvasSize / 2.0, CanvasSize / 2.0);
            var lonlat = CanvasPixelsToLatLon(canvasCenter, latitude, longitude, zoom);
            SetMapCenter(lat: lonlat.Y, lon: lonlat.X, updateTextBoxes: true);

            // Načti aktuální dlaždice
            var (centerX, centerY) = LatLonToTileXY(latitude, longitude, zoom);
            await LoadTilesSmoothAsync(centerX - TileCount / 2, centerY - TileCount / 2);

            // Reproject overlaye
            ReprojectAllZonesOnMapChange();
            ReprojectActiveVehiclesOnMapChange();
            ReprojectReplayOnMapChange();
            await BringAllOverlaysToFrontSafeAsync();
        }


        // ======================================
        // Kontrola a načtení nových dlaždic během panování
        // ======================================
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

        // RefreshMap: keep at current center and start trails fresh (remove re-draw of old trails)
        public async void RefreshMap()
        {
            double centerTileX = LonToTileX(longitude, zoom); 
            double centerTileY = LatToTileY(latitude, zoom);  

            double leftTopTileX = centerTileX - TileCount / 2.0;
            double leftTopTileY = centerTileY - TileCount / 2.0;

            baseTileX = (int)Math.Floor(leftTopTileX);
            baseTileY = (int)Math.Floor(leftTopTileY);

            double fracX = leftTopTileX - baseTileX;
            double fracY = leftTopTileY - baseTileY;

            double offsetX = -fracX * TilePixelSize;
            double offsetY = -fracY * TilePixelSize;

            await LoadTilesSmoothAsync(baseTileX, baseTileY, offsetX, offsetY);

            // Canvas.SetLeft(TileCanvas, baseTileX);
            // Canvas.SetTop(TileCanvas, baseTileY);

            _ = EnsureLocalAreaAltitudeAsync(force: true);
            ResetAllTramTrails();

            ReprojectAllZonesOnMapChange();
            ReprojectActiveVehiclesOnMapChange();
            ReprojectReplayOnMapChange();
            ReprojectSwitchZonesOnMapChange();
            DrawRadiusCircle();

            await Task.Delay(1);
            await BringAllOverlaysToFrontSafeAsync();
        }


        //Meters per px
        private double MetersPerPixel(double latitude, int zoom)
        {
            double earthCircumference = 40075016.686; // in meters
            double latitudeRad = latitude * Math.PI / 180.0;
            return earthCircumference * Math.Cos(latitudeRad) / Math.Pow(2, zoom + 8); // +8 because of 256 px
        }


        //Get the parenet element in the canvas of an object
        private UIElement GetParentElementInCanvas(UIElement element)
        {
            while (element != null && !TileCanvas.Children.Contains(element))
            {
                element = VisualTreeHelper.GetParent(element) as UIElement;
            }
            return element;
        }

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

        //LMB pressed

        private void TileCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Block drawing while playback is running
            if (isPlaying)
            {
                e.Handled = true;
                return;
            }

            var pos = e.GetPosition(TileCanvas);

            if (currentDrawingMode == DrawingMode.Point)
            {
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
                UpdateVehicleCanvasPosition(tram, pos, drawnTramColors[idx], false, tram.Label, 0);

                // Manual tram body: draw only from the second point using heading from (prev -> current)
                if (drawnTramTrailPoints[idx].Count > 0)
                {
                    var prev = drawnTramTrailPoints[idx].Last();
                    var headingDeg = CalculateAzimuth(prev, pos);

                    // Flip 180°: compensate for +180 inside UpdateOrCreateBox
                    headingDeg = (headingDeg - 180 + 360) % 360;

                    UpdateOrCreateVehicleBox(drawnTramIds[idx], new Point(pos.X, pos.Y), drawnTramColors[idx], headingDeg);
                }
                else
                {
                    // First click: ensure no leftover box is shown
                    if (_vehicleBoxes.TryGetValue(drawnTramIds[idx], out var leftover))
                    {
                        TileCanvas.Children.Remove(leftover);
                        _vehicleBoxes.Remove(drawnTramIds[idx]);
                    }
                }

                if (isRecording)
                {
                    tram.MovementFrames.Add(new MovementFrame
                    {
                        Timestamp = DateTime.Now - recordingStartTime,
                        Position = pos
                    });
                }

                // Trail polyline body
                drawnTramTrailPoints[idx].Add(pos);
                if (drawnTramTrailPoints[idx].Count > _maxTrailLength + 1)
                    drawnTramTrailPoints[idx].RemoveAt(0);

                // Ensure trail exists (could be null after RefreshMap/ResetAllTramTrails)
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

                // uložit aktuální geodetickou pozici tramvaje
                drawnTramLat[idx] = latitudeWgs;
                drawnTramLon[idx] = longitudeWgs;

                // správa trailu i v Lat/Lon
                drawnTramTrailGeoPoints[idx].Add((latitudeWgs, longitudeWgs));
                if (drawnTramTrailGeoPoints[idx].Count > _maxTrailLength + 1)
                    drawnTramTrailGeoPoints[idx].RemoveAt(0);

                SendPointAsCamMessage(drawnTramIds[idx], latitudeWgs, longitudeWgs, speed: _manualCamSpeedKmh);
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
                // Capture target tab at the start of rectangle drawing
                if (rectPhase == RectangleDrawPhase.None)
                {
                    _drawToSwitchZones = IsSwitchMode();
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
                        SubZone = nextSub
                    };

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
                    ActivationZonesCollection.Add(zone);

                    zone.PropertyChanged += ActivationZone_PropertyChanged;

                    rect.MouseEnter += Rectangle_MouseEnter;
                    rect.MouseLeave += Rectangle_MouseLeave;
                    rect.MouseLeftButtonDown += Rectangle_MouseLeftButtonDown;

                    ClearTempElements();
                    rectPhase = RectangleDrawPhase.None;
                }
            }

            if (currentDrawingMode == DrawingMode.Railway)
            {
                isDrawing = true;
                startPoint = pos;

                currentLine = new Line
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 2,
                    X1 = startPoint.X,
                    Y1 = startPoint.Y,
                    X2 = startPoint.X,
                    Y2 = startPoint.Y,
                    IsHitTestVisible = false
                };

                TileCanvas.Children.Add(currentLine);
                return;
            }
        }

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
        }

        private void TileCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(TileCanvas);

            if (rectPhase == RectangleDrawPhase.HeightDefinition && tempHeightLine != null)
            {
                tempHeightLine.X2 = pos.X;
                tempHeightLine.Y2 = pos.Y;

                double heightPx = (pos - rectFirstPoint).Length;
                double widthPx = 0;

                double mpp = MetersPerPixel(latitude, zoom);
                double widthMeters = widthPx * mpp;
                double heightMeters = heightPx * mpp;

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

                dimensionTextBlock.Text = $"{widthMeters:F1} m × {heightMeters:F1} m";
                dimensionTextBlock.Visibility = Visibility.Visible;
                Canvas.SetLeft(dimensionTextBlock, pos.X + 10);
                Canvas.SetTop(dimensionTextBlock, pos.Y + 10);
            }

            else if (currentDrawingMode == DrawingMode.Railway && currentLine != null)
            {
                currentLine.X2 = pos.X;
                currentLine.Y2 = pos.Y;
            }
            else
            {
                dimensionTextBlock.Visibility = Visibility.Collapsed;
            }
        }


        //stop drawing when mouse button is released
        private void TileCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            AddUndoRedo(
                undo: () =>
                {
                    TileCanvas.Children.Remove(currentLine);
                    railwayLines.Remove(currentLine);
                },
                redo: () =>
                {
                    TileCanvas.Children.Add(currentLine);
                    railwayLines.Add(currentLine);


                }
            );

            if (currentDrawingMode == DrawingMode.Railway && currentLine != null)
            {
                currentLine.IsHitTestVisible = true;

                railwayLines.Add(currentLine);

                isDirty = true; // Mark as modified

                currentLine = null;
            }
        }

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
        }

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




        //moving objects with RMB
        private void TileCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var position = e.GetPosition(TileCanvas);
            var hit = VisualTreeHelper.HitTest(TileCanvas, position);
            if (hit == null) return;

            var element = hit.VisualHit as UIElement;
            if (element == null) return;

            // Ignore the SRV radius circle and any click-through visuals
            if ((radiusEllipse != null && ReferenceEquals(element, radiusEllipse)) || element.IsHitTestVisible == false)
                return;

            // Allow dragging only for intended types, but block SRV marker itself
            if (element is Rectangle || element is Ellipse || element is TextBlock || element is Line)
            {
                // Do not allow dragging SRV point or its label
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


        //mouse dragging
        private void TileCanvas_MouseMoveForDrag(object sender, MouseEventArgs e)
        {
            if (selectedElement == null || e.RightButton != MouseButtonState.Pressed) return;

            var currentPos = e.GetPosition(TileCanvas);
            var dx = currentPos.X - mouseOffset.X;
            var dy = currentPos.Y - mouseOffset.Y;

            if (selectedElement is FrameworkElement element)
            {
                double left = Canvas.GetLeft(element);
                double top = Canvas.GetTop(element);

                double newLeft = left + dx;
                double newTop = top + dy;

                Canvas.SetLeft(element, newLeft);
                Canvas.SetTop(element, newTop);

                if (selectedElement is TextBlock tb)
                {
                    var matchingPoint = points.FirstOrDefault(p => p.Text == tb);
                    if (matchingPoint != null)
                    {
                        var newPos = new Point(newLeft + 5, newTop + 10);
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
                // Base center = left + width/2, top + height (pre-rotation)
                double left = Canvas.GetLeft(rect);
                double top = Canvas.GetTop(rect);
                var baseCenter = new Point(left + rect.Width / 2.0, top + rect.Height);

                // Avoid recursive reposition while syncing model
                isUpdatingActivationZone = true;
                try
                {
                    zone.StartPoint = baseCenter;

                    // Sync Lat/Lon from base center
                    var lonlat = CanvasPixelsToLatLon(baseCenter, latitude, longitude, zoom);
                    zone.Longitude = lonlat.X;
                    zone.Latitude = lonlat.Y;
                }
                finally
                {
                    isUpdatingActivationZone = false;
                }

                // Refresh bounds after move
                UpdateActivationZoneBounds(zone);
            }
        }


        //RMB release updater
        private void TileCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (selectedElement is Rectangle rect)
            {

            }
            selectedElement = null;
            TileCanvas.Cursor = Cursors.Arrow;
        }



        //Keystrokes (esc, arrows, etc.)
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

                // ignore Esc if not drawing
                e.Handled = true;
                return;
            }

            // Add the shortcut inside Window_PreviewKeyDown(...)
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
                TileCanvas.Children.Remove(selectedElement);

                if (selectedElement is Ellipse ellipse)
                {
                    var pointToRemove = points.FirstOrDefault(p => p.Ellipse == ellipse);
                    if (pointToRemove != null)
                    {
                        TileCanvas.Children.Remove(pointToRemove.Text);
                        points.Remove(pointToRemove);
                        RecalculateConnectionLine(); //recalculate the connection line after removing the point
                    }
                }
                else if (selectedElement is TextBlock tb)
                {
                    var pointToRemove = points.FirstOrDefault(p => p.Text == tb);
                    if (pointToRemove != null)
                    {
                        TileCanvas.Children.Remove(pointToRemove.Ellipse);
                        points.Remove(pointToRemove);
                        RecalculateConnectionLine();
                    }
                }
                else if (selectedElement is Rectangle rect)
                {
                    var mapRect = mapRectangles.FirstOrDefault(r => r.Shape == rect);
                    if (mapRect != null)
                        mapRectangles.Remove(mapRect);

                    if (activationZones.TryGetValue(rect, out var zone))
                    {
                        ActivationZonesCollection.Remove(zone);
                        activationZones.Remove(rect);
                    }

                    TileCanvas.Children.Remove(rect);
                }

                selectedElement = null;
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


        // Helper: cancel any in-progress drawing and switch to selection mode
        private bool CancelAllDrawing()
        {
            bool didSomething = false;

            // Cancel rectangle drawing (activation zones)
            if (rectPhase != RectangleDrawPhase.None ||
                tempHeightLine != null || tempWidthLine != null ||
                previewRect != null || startPointEllipse != null || secondPointEllipse != null)
            {
                ClearTempElements();
                rectPhase = RectangleDrawPhase.None;
                isDrawing = false;
                didSomething = true;
            }

            // Cancel railway drawing
            if (currentDrawingMode == DrawingMode.Railway && currentLine != null)
            {
                // currentLine ještě není uložen do railwayLines, jen jej smaž z canvasu
                if (TileCanvas.Children.Contains(currentLine))
                    TileCanvas.Children.Remove(currentLine);
                currentLine = null;
                isDrawing = false;
                didSomething = true;
            }

            // Cancel point drawing session (bez mazání existujících bodů)
            if (currentDrawingMode == DrawingMode.Point && isDrawing)
            {
                isDrawing = false;
                didSomething = true;
            }

            // Stop middle-mouse panning if active
            if (isMiddleMousePanning)
            {
                isMiddleMousePanning = false;
                isPanning = false;
                TileCanvas.ReleaseMouseCapture();
                didSomething = true;
            }

            // Hide dimensions overlay
            if (dimensionTextBlock != null && dimensionTextBlock.Visibility == Visibility.Visible)
            {
                dimensionTextBlock.Visibility = Visibility.Collapsed;
                didSomething = true;
            }

            // Deselect element and reset cursor
            if (selectedElement != null)
            {
                DeselectElement();
                didSomething = true;
            }
            TileCanvas.Cursor = Cursors.Arrow;

            // Switch to selection mode so further clicks nebudou kreslit
            SetSelectionMode();

            return didSomething;
        }

        //checking for close points => drawing only one point on one place
        private bool ArePointsClose(Point p1, Point p2, double threshold = 2.0)
        {
            return Math.Abs(p1.X - p2.X) < threshold && Math.Abs(p1.Y - p2.Y) < threshold;
        }



        //recalculating connection line between points after dragging or removing point(s)
        private void RecalculateConnectionLine()
        {
            connectionLine.Points.Clear();

            foreach (var pt in points)
            {
                connectionLine.Points.Add(pt.Position);
            }
        }


        //updating thickness of rectangles on mouse events
        private void Rectangle_MouseEnter(object sender, MouseEventArgs e)
        {
            if (isSelectionMode)
            {
                if (sender is Rectangle rect)
                {
                    rect.StrokeThickness = 5;
                }
            }
        }

        private void Rectangle_MouseLeave(object sender, MouseEventArgs e)
        {
            if (isSelectionMode)
            {
                if (sender is Rectangle rect)
                {
                    rect.StrokeThickness = 2;
                }
            }
        }

        private void Rectangle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!isSelectionMode)
            {
                return;
            }

            var rectangle = sender as Rectangle;
            if (rectangle != null)
            {
                SelectElement(rectangle);
            }

            e.Handled = true;
        }

        private void UpdateHitTestForSelectableElements()
        {
            bool allowSelection = isSelectionMode || currentDrawingMode == DrawingMode.Rectangle;

            foreach (var rect in TileCanvas.Children.OfType<Rectangle>())
            {
                rect.IsHitTestVisible = allowSelection;
            }

        }


        private void SetDrawingMode(DrawingMode mode)
        {
            currentDrawingMode = mode;
            isSelectionMode = false;
            UpdateHitTestForSelectableElements();
        }


        private void SetSelectionMode()
        {
            isSelectionMode = true;
            currentDrawingMode = DrawingMode.None;
            UpdateHitTestForSelectableElements();
        }


        //activation zone handler
        private void ActivationZone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }


        // srv radius circle drawing

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
            }
            else
            {
                // If we can't draw (e.g., missing coords), ensure it is removed
                if (radiusEllipse != null)
                {
                    TileCanvas.Children.Remove(radiusEllipse);
                    radiusEllipse = null;
                }
                Console.WriteLine("Circle cannot be drawn - missing coordinates");
            }
        }


        // Cleanup old vehicles that haven't been updated in the last 60 seconds
        private void CleanupOldVehicles(object sender, EventArgs e)
        {
            var now = DateTime.Now;

            var toRemoveFromTable = TramTable
                .Where(t => (now - t.LastMessageTimestamp)?.TotalSeconds > TableRowTimeout.TotalSeconds)
                .ToList(); ;

            foreach (var item in toRemoveFromTable)
            {
                // keep drawn simulated trams exempt
                if (drawnTramIds.Any(id => id.EndsWith(item.VehicleId)))
                    continue;

                // find the full vehicle ID in activeVehicles by suffix match
                var fullId = activeVehicles.Keys.FirstOrDefault(k => k.EndsWith(item.VehicleId));
                if (fullId != null && activeVehicles.TryGetValue(fullId, out var vehicle))
                {
                    // if not already cleaning up, start gradual trail removal (2s per segment)
                    if (!vehicleTrailCleanupTokens.ContainsKey(fullId))
                    {
                        var cts = new CancellationTokenSource();
                        vehicleTrailCleanupTokens[fullId] = cts;
                        _ = RemoveTrailGradually(fullId, vehicle, cts.Token);
                    }

                    // Do NOT remove the table row here; it will be removed in RemoveVehicleCompletely
                    continue;
                }

                // No visual exists anymore – allow a small grace (2s) before removing the row
                if (item.SecondsSinceLastCam > TableRowTimeout.TotalSeconds + 2)
                    TramTable.Remove(item);
            }

            for (int i = 0; i < drawnTrams.Length; i++)
            {
                var tram = drawnTrams[i];
                if (tram == null) continue;

                if (tram.IsRecorded)
                {
                    if (isPlaying) continue;

                    if ((now - tram.LastUpdate).TotalSeconds > 60)
                    {
                        RemoveDrawnTramCompletely(i, tram);
                    }
                }
                else
                {
                    // existing non-recorded cleanup ...
                    if ((now - tram.LastUpdate).TotalSeconds > 10 && drawnTramTrails[i] != null && drawnTramTrails[i].Points.Count > 0)
                    {
                        string tramKey = $"drawn_{i}_trail";
                        if (!vehicleTrailCleanupTokens.ContainsKey(tramKey))
                        {
                            var cts = new CancellationTokenSource();
                            vehicleTrailCleanupTokens[tramKey] = cts;
                            _ = RemoveDrawnTramTrailGradually(i, tram, cts.Token);
                        }
                    }

                    if ((now - tram.LastUpdate).TotalSeconds > 23)
                    {
                        RemoveDrawnTramCompletely(i, tram);
                    }
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

                bool isTram = vehicle.Ellipse?.Tag?.ToString() == "Tram";
                if (!isTram)
                {
                    RemoveVehicleCompletely(vehicleId, vehicle);
                    continue;
                }

                double speed = -1;
                if (vehicle.Speed != null && double.TryParse(vehicle.Speed.Text.Replace("km/h", "").Trim(), out double parsedSpeed))
                    speed = parsedSpeed;

                if (vehicleTrailCleanupTokens.ContainsKey(vehicleId))
                    continue;

                if (speed == 0 || speed < 0)
                {
                    var cts = new CancellationTokenSource();
                    vehicleTrailCleanupTokens[vehicleId] = cts;
                    _ = RemoveTrailGradually(vehicleId, vehicle, cts.Token);
                }
                else
                {
                    RemoveVehicleCompletely(vehicleId, vehicle);
                }
            }
        }

        private void RemoveVehicleCompletely(string vehicleId, MapPoint vehicle)
        {
            Dispatcher.Invoke(() =>
            {
                TileCanvas.Children.Remove(vehicle.Ellipse);
                TileCanvas.Children.Remove(vehicle.Text);
                if (vehicle.Speed != null)
                    TileCanvas.Children.Remove(vehicle.Speed);

                var linesToRemove = TileCanvas.Children.OfType<Polyline>()
                    .Where(pl => pl.Tag != null && string.Equals(pl.Tag.ToString(), $"tram_trail_{vehicleId}", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var line in linesToRemove)
                    TileCanvas.Children.Remove(line);

                if (vehicle.TrailDots != null)
                {
                    foreach (var dot in vehicle.TrailDots)
                        TileCanvas.Children.Remove(dot);
                    vehicle.TrailDots.Clear();
                }

                activeVehicles.Remove(vehicleId);

                if (vehicleTrailCleanupTokens.TryGetValue(vehicleId, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                    vehicleTrailCleanupTokens.Remove(vehicleId);
                }

                if (_vehicleBoxes.TryGetValue(vehicleId, out var vRect))
                {
                    TileCanvas.Children.Remove(vRect);
                    _vehicleBoxes.Remove(vehicleId);
                }

                var tramInfo = TramTable.FirstOrDefault(t => t.VehicleId == (vehicleId.Length > 4 ? vehicleId[^4..] : vehicleId));
                if (tramInfo != null)
                    TramTable.Remove(tramInfo);
            });
        }

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
                drawnTramTrailGeoPoints[idx].Clear();   // nově
                drawnTramLat[idx] = null;               // nově
                drawnTramLon[idx] = null;               // nově
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



        // async method for removing vehicle trail gradually

        private async Task RemoveTrailGradually(string vehicleId, MapPoint vehicle, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    bool hadTrail = false;
                    Dispatcher.Invoke(() =>
                    {
                        var trail = TileCanvas.Children.OfType<Polyline>()
                            .FirstOrDefault(pl => pl.Tag != null && pl.Tag.ToString() == $"tram_trail_{vehicleId}");

                        if (trail != null && trail.Points.Count > 1)
                        {
                            trail.Points.RemoveAt(0);
                            hadTrail = true;

                            if (vehicle.TrailDots != null && vehicle.TrailDots.Count > 0)
                            {
                                TileCanvas.Children.Remove(vehicle.TrailDots[0]);
                                vehicle.TrailDots.RemoveAt(0);
                            }
                        }
                        else if (trail != null && trail.Points.Count == 1)
                        {
                            TileCanvas.Children.Remove(trail);
                            hadTrail = false;
                        }
                    });

                    if (!hadTrail)
                        break;

                    await Task.Delay(2000, token); // remove one segment every 2s
                }

                // remove any remaining dots
                Dispatcher.Invoke(() =>
                {
                    if (vehicle.TrailDots != null && vehicle.TrailDots.Count > 0)
                    {
                        foreach (var dot in vehicle.TrailDots.ToList())
                        {
                            TileCanvas.Children.Remove(dot);
                        }
                        vehicle.TrailDots.Clear();
                    }
                });

                // Extra 2s after last segment disappears before removing the last point/row
                await Task.Delay(2000, token);

                RemoveVehicleCompletely(vehicleId, vehicle);
            }
            catch (TaskCanceledException)
            {
                // ignored
            }
        }

        private async Task RemoveDrawnTramTrailGradually(int idx, MapPoint tram, CancellationToken token)
        {
            try
            {
                while (true)
                {
                    bool hadTrail = false;
                    Dispatcher.Invoke(() =>
                    {
                        var trail = drawnTramTrails[idx];

                        if (trail != null && trail.Points.Count > 1)
                        {
                            trail.Points.RemoveAt(0);
                            hadTrail = true;

                            if (tram.TrailDots != null && tram.TrailDots.Count > 0)
                            {
                                TileCanvas.Children.Remove(tram.TrailDots[0]);
                                tram.TrailDots.RemoveAt(0);
                            }
                        }
                        else if (trail != null)
                        {
                            // Remove any remaining short segment and stop
                            TileCanvas.Children.Remove(trail);
                            drawnTramTrails[idx] = null;
                            hadTrail = false;
                        }
                    });

                    if (!hadTrail)
                        break;

                    await Task.Delay(2000, token);
                }

                Dispatcher.Invoke(() =>
                {
                    if (tram.TrailDots != null && tram.TrailDots.Count > 0)
                    {
                        foreach (var dot in tram.TrailDots.ToList())
                            TileCanvas.Children.Remove(dot);
                        tram.TrailDots.Clear();
                    }
                });
            }
            catch (TaskCanceledException)
            {
                // ignore
            }
            finally
            {
                // Clear the token entry for this tram's trail cleanup
                var key = $"drawn_{idx}_trail";
                if (vehicleTrailCleanupTokens.TryGetValue(key, out var cts))
                {
                    vehicleTrailCleanupTokens.Remove(key);
                    cts.Dispose();
                }
            }
        }

        //Undo and redo (for the key button function via. above)
        private void AddUndoRedo(Action undo, Action redo)
        {
            undoStack.Push(undo);
            redoStack.Clear();
        }


        private void Undo()
        {
            if (undoStack.Count > 0)
            {
                var action = undoStack.Pop();
                action.Invoke();
                redoStack.Push(action);
            }
        }


        private void Redo()
        {
            if (redoStack.Count > 0)
            {
                var action = redoStack.Pop();
                action.Invoke();
                undoStack.Push(action);
            }
        }


        //element selection and deselection
        private void SelectElement(UIElement element)
        {
            // Deselect only if selecting a different element
            if (selectedElement != null && selectedElement != element)
                DeselectElement();

            selectedElement = element;

            if (element is Rectangle rect)
            {
                selectedRectangle = rect;
                rect.Stroke = Brushes.Gold;
            }
            else
            {
                selectedRectangle = null;
            }
        }

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

        // increment cam ok messages count (crc must be ok)
        private void IncrementCamOkCount()
        {
            camOkCount++;

            Dispatcher.Invoke(() =>
                CamOkLabel.Content = $"CAM OK: {camOkCount}");
        }

        //increment cam error messages count (crc is not ok)
        private void IncrementCamErrorCount()
        {
            camErrorCount++;

            Dispatcher.Invoke(() =>
            {
                CamErrorLabel.Content = $"CAM Errors: {camErrorCount}";
            });
        }

        // increment srv ok messages count
        private void IncrementSrvOkCount()
        {
            srvOkCount++;

            Dispatcher.Invoke(() =>
                SrvOkLabel.Content = $"SRV OK: {srvOkCount}");
        }

        // increment srv error messages count
        private void IncrementSrvErrorCount()
        {
            srvErrorCount++;
            Dispatcher.Invoke(() =>
                SrvErrorLabel.Content = $"SRV Errors: {srvErrorCount}");
        }



        //||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||
        // METHODS FOR EXPORTING AND SAVING


        //Exporting canvas into an image
        private void ExportCanvasToPng(Canvas canvas, string filePath)
        {
            int width = (int)canvas.ActualWidth;
            int height = (int)canvas.ActualHeight;
            int dpi = 96;

            if (width == 0 || height == 0)
            {
                Console.WriteLine("Canvas size is zero, export skipped.");
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


        //saving map into an XML file (lines, rectangles, points)
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

                zonesElement.Add(new XElement("Zone",
                    new XAttribute("Name", zone.Name),
                    new XAttribute("Latitude", latlon.Y.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Longitude", latlon.X.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("StartX", startX.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("StartY", startY.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Width", zone.Width.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Height", zone.Height.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Azimuth", zone.Azimuth.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Color", zone.Color),
                    // NEW
                    new XAttribute("MainZone", zone.MainZone.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("SubZone", zone.SubZone.ToString(CultureInfo.InvariantCulture))
                ));
            }
            root.Add(zonesElement);

            XElement railwaysElement = new XElement("Railways");
            foreach (var line in railwayLines)
            {
                railwaysElement.Add(new XElement("Line",
                    new XAttribute("X1", line.X1.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Y1", line.Y1.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("X2", line.X2.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Y2", line.Y2.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("Stroke", ((SolidColorBrush)line.Stroke).Color.ToString()),
                    new XAttribute("StrokeThickness", line.StrokeThickness.ToString(CultureInfo.InvariantCulture))
                ));
            }
            root.Add(railwaysElement);

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


        //loading XML file
        private async void LoadXML(string filePath)
        {
            if (!File.Exists(filePath)) return;

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


            _ = EnsureLocalAreaAltitudeAsync(force: true);

            var zonesElement = root.Element("ActivationZones");
            if (zonesElement != null)
            {
                foreach (var zoneElem in zonesElement.Elements("Zone"))
                {
                    string name = zoneElem.Attribute("Name")?.Value ?? "Unnamed";
                    double latitudeZone = double.Parse(zoneElem.Attribute("Latitude")?.Value ?? "0", CultureInfo.InvariantCulture);
                    double longitudeZone = double.Parse(zoneElem.Attribute("Longitude")?.Value ?? "0", CultureInfo.InvariantCulture);
                    double width = double.Parse(zoneElem.Attribute("Width")?.Value ?? "0", CultureInfo.InvariantCulture);
                    double height = double.Parse(zoneElem.Attribute("Height")?.Value ?? "0", CultureInfo.InvariantCulture);
                    int azimuth = int.Parse(zoneElem.Attribute("Azimuth")?.Value ?? "0");
                    string color = zoneElem.Attribute("Color")?.Value ?? "#FF0000";

                    int mainZone = int.Parse(zoneElem.Attribute("MainZone")?.Value ?? "0", CultureInfo.InvariantCulture);
                    int subZone = int.Parse(zoneElem.Attribute("SubZone")?.Value ?? "0", CultureInfo.InvariantCulture);

                    if (ZoneAlreadyExists(name, latitudeZone, longitudeZone, width, height, azimuth, color))
                        continue;

                    double startXZone = double.Parse(zoneElem.Attribute("StartX")?.Value ?? "0", CultureInfo.InvariantCulture);
                    double startYZone = double.Parse(zoneElem.Attribute("StartY")?.Value ?? "0", CultureInfo.InvariantCulture);
                    Point startPoint = new Point(startXZone, startYZone);

                    double mpp = MetersPerPixel(latitude, zoom);
                    double widthPx = width / mpp;
                    double heightPx = height / mpp;

                    var rect = new Rectangle
                    {
                        Stroke = (SolidColorBrush)(new BrushConverter().ConvertFromString(color)),
                        StrokeThickness = 2,
                        Fill = Brushes.Transparent,
                        Tag = "DrawnRectangle",
                        Uid = name,
                        Width = widthPx,
                        Height = heightPx
                    };

                    var activationZone = new ActivationZone
                    {
                        Name = name,
                        Latitude = latitudeZone,
                        Longitude = longitudeZone,
                        Width = width,
                        Height = height,
                        Azimuth = azimuth,
                        Color = color,
                        Rectangle = rect,
                        StartPoint = startPoint,
                        // NEW
                        MainZone = Math.Clamp(mainZone, 0, 3),
                        SubZone = Math.Clamp(subZone, 0, 4)
                    };

                    activationZones[rect] = activationZone;
                    ActivationZonesCollection.Add(activationZone);
                    activationZone.PropertyChanged += ActivationZone_PropertyChanged;

                    UpdateRectanglePositionFromStartPoint(activationZone);
                    TileCanvas.Children.Add(rect);
                    UpdateActivationZoneBounds(activationZone);
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

            var railwaysElement = root.Element("Railways");
            if (railwaysElement != null)
            {
                railwayLines.Clear();
                foreach (var lineElem in railwaysElement.Elements("Line"))
                {
                    double x1 = double.Parse(lineElem.Attribute("X1")?.Value ?? "0", CultureInfo.InvariantCulture);
                    double y1 = double.Parse(lineElem.Attribute("Y1")?.Value ?? "0", CultureInfo.InvariantCulture);
                    double x2 = double.Parse(lineElem.Attribute("X2")?.Value ?? "0", CultureInfo.InvariantCulture);
                    double y2 = double.Parse(lineElem.Attribute("Y2")?.Value ?? "0", CultureInfo.InvariantCulture);
                    var strokeColor = (Color)ColorConverter.ConvertFromString(lineElem.Attribute("Stroke")?.Value ?? "#000000");
                    double thickness = double.Parse(lineElem.Attribute("StrokeThickness")?.Value ?? "1", CultureInfo.InvariantCulture);

                    // Skip drawing if identical line already exists on the map
                    if (RailwayLineAlreadyExists(x1, y1, x2, y2, strokeColor, thickness))
                        continue;

                    Line line = new Line
                    {
                        X1 = x1,
                        Y1 = y1,
                        X2 = x2,
                        Y2 = y2,
                        Stroke = new SolidColorBrush(strokeColor),
                        StrokeThickness = thickness,
                        IsHitTestVisible = false
                    };
                    TileCanvas.Children.Add(line);
                    railwayLines.Add(line);
                }
            }

            foreach (var vehicle in activeVehicles.Values)
            {
                CheckActivationZones(vehicle.Position, vehicle.Label);
            }

            Console.WriteLine($"Loaded file: {filePath}");
        }

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

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }


        //Playback timer ticks

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


        //start recording method
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
                        "No manual points were recorded.\n\nDo you want to save the live RS485 CAM buffer instead?",
                        "Save live buffer",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (ask == MessageBoxResult.Yes)
                    {
                        SaveLiveCamBuffer();
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

        // Helper: uloží live (RS485) buffer
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

        private void SaveCamRecording(string filePath)
        {
            WriteCamrecWithCenter(filePath, recordedCamMessages);
        }

        //updating point position on the map
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
        // StartV2XListenerAsync – po otevření portu rovnou pošli SRV a spusť minutu timer (pokud je checkbox zaškrtnutý)
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

                        // do not mix with classic replay or timeshift catch-up rendering
                        if (_isPlaybackSessionActive || _isTimeshiftPlaybackActive) continue;

                        int xmlStart = rawLine.IndexOf('<');
                        if (xmlStart < 0) continue;
                        string rawXml = rawLine.Substring(xmlStart);

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
                                    TerminalLog($"CAM {shortId} lat={msg.Latitude:F6} lon={msg.Longitude:F6} spd={msg.Speed:F1} hdg={msg.Heading:F0} [{crcTxt}]", Brushes.Beige);
                                    if (valid) IncrementCamOkCount();
                                    else
                                    {
                                        IncrementCamErrorCount();
                                        TerminalLog($"ERROR: CAM CRC invalid for {shortId}", Brushes.Red);
                                    }
                                });
                            }
                            else if (msg.MessageType == "SRV")
                            {
                                // record SRV into timeshift buffer
                                if (_timeshiftEnabled)
                                    recordedSrvMessages.Add(rawXml);
                            }

                            // while paused: keep buffering (above) but suppress live rendering
                            if (_timeshiftEnabled && _timeshiftPaused)
                                continue;

                            // normal live rendering
                            Dispatcher.Invoke(() => HandleV2XMessage(msg, rawXml));
                        }
                        catch (Exception ex)
                        {
                            // In the catch (Exception ex) of StartV2XListenerAsync receive loop, append:
                            Dispatcher.Invoke(() =>
                            {
                                IncrementCamErrorCount();
                                TerminalLog($"ERROR: Parse failed - {ex.Message}", Brushes.Red);
                            });
                        }
                    }
                }
                catch (Exception loopEx)
                {
                    Dispatcher.Invoke(() => Console.WriteLine($"Serial listen loop error: {loopEx.Message}"));
                }
            });

            return Task.CompletedTask;
        }


        //Asynchronous read line from serial port
        private Task<string> ReadLineAsync(SerialPort port)
        {
            return Task.Run(() => port.ReadLine());
        }


        //Handling V2X messages !!!!!!!
        private void HandleV2XMessage(V2XMessage msg, string rawXml)
        {
            if (msg.IsManual)
                return;

            // ID filter only when FilterCheckBox is ON
            if (msg.MessageType == "CAM" && ShouldFilterLiveById(msg.VehicleID))
                return;

            if (msg.MessageType == "CAM")
            {
                if (TryFilterCamByAltitude(rawXml))
                    return;

                if (!vehicleColorMap.TryGetValue(msg.VehicleID, out Brush tramColor))
                {
                    int colorIndex = vehicleColorMap.Count % vehicleColors.Count;
                    tramColor = vehicleColors[colorIndex];
                    vehicleColorMap[msg.VehicleID] = tramColor;
                }

                if (activeVehicles.TryGetValue(msg.VehicleID, out var existing))
                {
                    bool tableHasRow = IsTramTableRowPresentForId(msg.VehicleID);
                    bool tooOld = (DateTime.Now - existing.LastUpdate) > TableRowTimeout;

                    if (!tableHasRow || tooOld)
                    {
                        ResetVehicleInstance(msg.VehicleID);
                    }
                }
                // NOTE: no buffering here; timeshift buffering is done in StartV2XListenerAsync

                if (!(msg.VehicleID?.StartsWith("000000") ?? false) || isPlaying == true)
                {
                    if (activeVehicles.TryGetValue(msg.VehicleID, out var point))
                    {
                        var (x, y) = ConvertLatLonToCanvasXY(msg.Latitude, msg.Longitude);
                        point.Position = new Point(x, y);
                        point.LastUpdate = DateTime.Now;
                        UpdateVehicleCanvasPosition(point, new Point(x, y), tramColor, false, point.Label, msg.Speed);

                        // store last geo + heading for reprojection
                        _lastLatLon[msg.VehicleID] = (msg.Latitude, msg.Longitude);
                        _lastHeadingLive[msg.VehicleID] = msg.Heading;

                        var topCenter = new Point(x, y);
                        var liveHeadingAdj = (msg.Heading - 180 + 360) % 360;
                        UpdateOrCreateVehicleBox(msg.VehicleID, topCenter, tramColor, liveHeadingAdj);
                    }
                    else
                    {
                        var (x, y) = ConvertLatLonToCanvasXY(msg.Latitude, msg.Longitude);

                        var ellipse = new Ellipse
                        {
                            Width = 12,
                            Height = 12,
                            Fill = tramColor,
                            Tag = "Tram"
                        };
                        Canvas.SetLeft(ellipse, x - ellipse.Width / 2);
                        Canvas.SetTop(ellipse, y - ellipse.Height / 2);

                        var text = new TextBlock
                        {
                            Text = msg.VehicleID,
                            Foreground = tramColor,
                            FontWeight = FontWeights.Bold,
                            Tag = "Tram"
                        };
                        Canvas.SetLeft(text, x + 8);
                        Canvas.SetTop(text, y - 6);

                        var speedText = new TextBlock
                        {
                            Text = $"{msg.Speed:F1} km/h",
                            Foreground = tramColor,
                            FontWeight = FontWeights.Bold,
                            Tag = "Tram"
                        };
                        Canvas.SetLeft(speedText, x + 8);
                        Canvas.SetTop(speedText, y + 6);

                        var newPoint = new MapPoint
                        {
                            Position = new Point(x, y),
                            Label = msg.VehicleID,
                            Ellipse = ellipse,
                            Text = text,
                            Speed = speedText,
                            LastUpdate = DateTime.Now,
                            VehicleColor = tramColor
                        };

                        activeVehicles[msg.VehicleID] = newPoint;

                        TileCanvas.Children.Add(ellipse);
                        TileCanvas.Children.Add(text);
                        TileCanvas.Children.Add(speedText);

                        _lastLatLon[msg.VehicleID] = (msg.Latitude, msg.Longitude);
                        _lastHeadingLive[msg.VehicleID] = msg.Heading;

                        // create the oriented box
                        var topCenterNew = new Point(x, y);
                        var liveHeadingAdj = (msg.Heading - 180 + 360) % 360;
                        UpdateOrCreateVehicleBox(msg.VehicleID, topCenterNew, tramColor, liveHeadingAdj); ;
                    }
                }

                if (lastCamTimes.TryGetValue(msg.VehicleID, out var lastTime))
                    prevCamTimes[msg.VehicleID] = lastTime;
                lastCamTimes[msg.VehicleID] = msg.Timestamp;

                string statId = string.IsNullOrEmpty(msg.VehicleID) ? "-" : msg.VehicleID;
                string camIdShort = statId.Length > 4 ? statId[^4..] : statId;
                CamIdLabel.Content = $"TRAM ID: {camIdShort}";

                UpdateOrAddVehicleData(camIdShort, msg.Speed, msg.Timestamp);

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

                        var shortId = logicalId.Length > 4 ? logicalId[^4..] : logicalId;
                        TerminalLog($"SRV {shortId} lat={srvMsg.Latitude:F6} lon={srvMsg.Longitude:F6}", Brushes.HotPink);
                        if (!isValid)
                            TerminalLog($"ERROR: SRV CRC invalid for {shortId}", Brushes.Red);
                    }
                }
                catch (Exception ex)
                {
                    IncrementSrvErrorCount();
                    TerminalLog($"ERROR: SRV parse failed - {ex.Message}", Brushes.Red);
                }

                LastSrvLabel.Content = $"Last SRV: {lastRecTime}";
                SrvIdLabel.Content = $"SRV ID: {logicalId}";

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

                        _lastLatLon[srvV2xMsg.VehicleID] = (srvV2xMsg.Latitude, srvV2xMsg.Longitude);

                        if (CircleCheckBox?.IsChecked == true)
                            DrawRadiusCircle();
                    }

                    if (activeVehicles.TryGetValue(srvV2xMsg.VehicleID, out var point))
                    {
                        point.Position = new Point(srvV2xMsg.Longitude, srvV2xMsg.Latitude);
                        point.LastUpdate = DateTime.Now;
                    }
                    else
                    {
                        var ellipse = new Ellipse
                        {
                            Width = 12,
                            Height = 12,
                            Fill = Brushes.Red,
                            Tag = "Srv"
                        };

                        var text = new TextBlock
                        {
                            Text = srvV2xMsg.VehicleID,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brushes.Black,
                            Tag = "Srv"
                        };

                        var newPoint = new MapPoint
                        {
                            Position = new Point(srvV2xMsg.Longitude, srvV2xMsg.Latitude),
                            Label = srvV2xMsg.VehicleID,
                            Ellipse = ellipse,
                            Text = text,
                            LastUpdate = DateTime.Now
                        };

                        activeVehicles[srvV2xMsg.VehicleID] = newPoint;

                        TileCanvas.Children.Add(ellipse);
                        TileCanvas.Children.Add(text);
                    }

                    UpdateVehicleTrail(srvV2xMsg);
                }
            }
        }

        // Validate SRV by computing CRC over the <service .../> tag (same CRC as CAM)
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

        // updating points and vehicle trails on the map
        
        private void UpdateVehicleTrail(V2XMessage msg)
        {
            var isSrv = msg.MessageType == "SRV";
            if (msg.IsManual) return;

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
                var ellipse = new Ellipse { Width = 12, Height = 12, Fill = tramColor };
                var text = new TextBlock
                {
                    Text = isSrv ? msg.VehicleID : (msg.VehicleID?.Length > 4 ? msg.VehicleID[^4..] : msg.VehicleID),
                    FontWeight = FontWeights.Bold,
                    Foreground = isSrv ? Brushes.Black : tramColor
                };
                var speedtext = new TextBlock
                {
                    Text = "",
                    FontWeight = FontWeights.Bold,
                    Foreground = isSrv ? Brushes.Black : tramColor
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
                    TrailDots = new List<Ellipse>()
                };
                activeVehicles[msg.VehicleID] = vehicle;
            }

            var (x, y) = ConvertLatLonToCanvasXY(msg.Latitude, msg.Longitude);

            var frame = new MovementFrame { Timestamp = msg.Timestamp.TimeOfDay, Position = new Point(x, y) };
            vehicle.MovementFrames ??= new List<MovementFrame>();
            vehicle.MovementFrames.Add(frame);

            // Cap frames to at most (_maxTrailLength + 1) points => _maxTrailLength segments
            while (vehicle.MovementFrames.Count > _maxTrailLength + 1)
                vehicle.MovementFrames.RemoveAt(0);

            vehicle.TrailDots ??= new List<Ellipse>();

            // Create a new dot at the previous frame (if any and not SRV)
            if (vehicle.MovementFrames.Count > 1 && !isSrv)
            {
                var prevFrame = vehicle.MovementFrames[^2];
                var trailDot = new Ellipse { Width = 5, Height = 5, Fill = Brushes.Black, IsHitTestVisible = false };
                Canvas.SetLeft(trailDot, prevFrame.Position.X - 2.5);
                Canvas.SetTop(trailDot, prevFrame.Position.Y - 2.5);
                vehicle.TrailDots.Add(trailDot);
                TileCanvas.Children.Add(trailDot);
                Panel.SetZIndex(trailDot, 1001);
            }

            // Cap dots to _maxTrailLength
            while (vehicle.TrailDots.Count > _maxTrailLength)
            {
                TileCanvas.Children.Remove(vehicle.TrailDots[0]);
                vehicle.TrailDots.RemoveAt(0);
            }

            UpdateVehicleCanvasPosition(vehicle, new Point(x, y), tramColor, isSrv, msg.VehicleID, msg.Speed);

            if (!isSrv)
            {
                // Rebuild the tram trail polyline from capped frames
                var oldLines = TileCanvas.Children.OfType<Polyline>()
                    .Where(l => l.Tag is string && (string)l.Tag == $"tram_trail_{msg.VehicleID}")
                    .ToList();
                foreach (var l in oldLines) TileCanvas.Children.Remove(l);

                if (vehicle.MovementFrames.Count > 1)
                {
                    var polyline = new Polyline
                    {
                        Stroke = tramColor,
                        StrokeThickness = 2,
                        Tag = $"tram_trail_{msg.VehicleID}"
                    };
                    foreach (var mf in vehicle.MovementFrames)
                        polyline.Points.Add(mf.Position);
                    TileCanvas.Children.Add(polyline);
                    Panel.SetZIndex(polyline, 999);
                }
            }

            Panel.SetZIndex(vehicle.Ellipse, 1000);
            Panel.SetZIndex(vehicle.Text, 1000);
        }



        //drawing SRV point on the map
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


        //checking if the cam message is valid (CRC check, substring check)
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


        //COMPUTING CRC
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


        // Serial port data received event handler
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

            // Manual recording only (simulated IDs of drawn trams)
            if (isRecording && (vehicleId == drawnTramIds[0] || vehicleId == drawnTramIds[1]))
            {
                recordedManualCamMessages.Add(xml);
            }
        }

        private void CheckActivationZones(Point pos, string vehicleId)
        {
            // Use only last 4 digits for the table
            string shortId = string.IsNullOrEmpty(vehicleId) ? "-" :
                             (vehicleId.Length > 4 ? vehicleId[^4..] : vehicleId);

            foreach (var zone in activationZones.Values)
            {
                if (zone.Bounds.Contains(pos))
                {
                    zone.LastTramId = shortId;
                    zone.IsActive = true;

                    // always make the rectangle thicker when activated
                    if (zone.Rectangle != null)
                        zone.Rectangle.StrokeThickness = 6;

                    // timer for deactivation
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
                    timer.Tick += (s, e) =>
                    {
                        zone.IsActive = false;
                        if (zone.Rectangle != null)
                            zone.Rectangle.StrokeThickness = 2;
                        ((DispatcherTimer)s).Stop();
                    };
                    timer.Start();
                    break;
                }
            }
        }

        // Keep CAM replay vehicles independent of simulated tram IDs; show real IDs and speed in m/s; cap trail to last 6 points
        private async void LoadPlaybackFile(string fileName)
        {
            StopPlaybackAndReset();
            _replayGeoFrames.Clear();

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
                    foreach (var zoneElem in zonesElement.Elements("Zone"))
                    {
                        string name = zoneElem.Attribute("Name")?.Value ?? "Unnamed";
                        double latitudeZone = double.Parse(zoneElem.Attribute("Latitude")?.Value ?? "0", CultureInfo.InvariantCulture);
                        double longitudeZone = double.Parse(zoneElem.Attribute("Longitude")?.Value ?? "0", CultureInfo.InvariantCulture);
                        double width = double.Parse(zoneElem.Attribute("Width")?.Value ?? "0", CultureInfo.InvariantCulture);
                        double height = double.Parse(zoneElem.Attribute("Height")?.Value ?? "0", CultureInfo.InvariantCulture);
                        int azimuth = int.Parse(zoneElem.Attribute("Azimuth")?.Value ?? "0");
                        string color = zoneElem.Attribute("Color")?.Value ?? "#FF0000";
                        // NEW defaults if absent
                        int mainZone = int.Parse(zoneElem.Attribute("MainZone")?.Value ?? "0", CultureInfo.InvariantCulture);
                        int subZone = int.Parse(zoneElem.Attribute("SubZone")?.Value ?? "0", CultureInfo.InvariantCulture);

                        if (ZoneAlreadyExists(name, latitudeZone, longitudeZone, width, height, azimuth, color))
                            continue;

                        double startXZone = double.Parse(zoneElem.Attribute("StartX")?.Value ?? "0", CultureInfo.InvariantCulture);
                        double startYZone = double.Parse(zoneElem.Attribute("StartY")?.Value ?? "0", CultureInfo.InvariantCulture);
                        Point startPoint = new Point(startXZone, startYZone);

                        double mpp = MetersPerPixel(latitude, zoom);
                        double widthPx = width / mpp;
                        double heightPx = height / mpp;

                        var rect = new Rectangle
                        {
                            Stroke = (SolidColorBrush)(new BrushConverter().ConvertFromString(color)),
                            StrokeThickness = 2,
                            Fill = Brushes.Transparent,
                            Tag = "DrawnRectangle",
                            Uid = name,
                            Width = widthPx,
                            Height = heightPx
                        };

                        var activationZone = new ActivationZone
                        {
                            Name = name,
                            Latitude = latitudeZone,
                            Longitude = longitudeZone,
                            Width = width,
                            Height = height,
                            Azimuth = azimuth,
                            Color = color,
                            Rectangle = rect,
                            StartPoint = startPoint,
                            MainZone = Math.Clamp(mainZone, 0, 3),
                            SubZone = Math.Clamp(subZone, 0, 4)
                        };

                        activationZones[rect] = activationZone;
                        ActivationZonesCollection.Add(activationZone);
                        activationZone.PropertyChanged += ActivationZone_PropertyChanged;

                        UpdateRectanglePositionFromStartPoint(activationZone);
                        TileCanvas.Children.Add(rect);
                        UpdateActivationZoneBounds(activationZone);
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

                var railwaysElement = root.Element("Railways");
                if (railwaysElement != null)
                {
                    if (railwayLines != null && railwayLines.Count > 0)
                    {
                        foreach (var oldLine in railwayLines)
                            TileCanvas.Children.Remove(oldLine);
                        railwayLines.Clear();
                    }

                    foreach (var lineElem in railwaysElement.Elements("Line"))
                    {
                        Line line = new Line
                        {
                            X1 = double.Parse(lineElem.Attribute("X1")?.Value ?? "0", CultureInfo.InvariantCulture),
                            Y1 = double.Parse(lineElem.Attribute("Y1")?.Value ?? "0", CultureInfo.InvariantCulture),
                            X2 = double.Parse(lineElem.Attribute("X2")?.Value ?? "0", CultureInfo.InvariantCulture),
                            Y2 = double.Parse(lineElem.Attribute("Y2")?.Value ?? "0", CultureInfo.InvariantCulture),
                            Stroke = new SolidColorBrush(
                                (Color)ColorConverter.ConvertFromString(lineElem.Attribute("Stroke")?.Value ?? "#000000")),
                            StrokeThickness = double.Parse(lineElem.Attribute("StrokeThickness")?.Value ?? "1", CultureInfo.InvariantCulture),
                            IsHitTestVisible = false
                        };

                        TileCanvas.Children.Add(line);
                        Panel.SetZIndex(line, 90);
                        if (railwayLines != null) railwayLines.Add(line);
                        else return;
                    }

                    await BringAllOverlaysToFrontSafeAsync();
                }
            }

            // CAM elements
            var camElements = doc.Descendants("CAM").ToList();
            var srvElements = doc.Descendants("SrvMessages").Descendants("SRV").ToList();

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

            var firstTime = earliest;                  // unified origin
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

            // Update replay UTC span from both CAM and SRV
            _replayStartUtc = minUtc;
            _replayEndUtc = maxUtc;

            _replayStartUtc = minUtc;
            _replayEndUtc = maxUtc;
            _playbackLoaded = true;
            _lastReplayFile = fileName;
            BuildPlaybackKeyframes();
            UpdateUiEnabledState();
            MessageBox.Show("Playback data loaded. Use Play button to start playback.", "Playback ready", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // .camrec loader: do not treat recorded IDs as simulated; show real IDs, and cap trails to last 6 points
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

            var camMessages = lines.Where(l => l.Contains("<vehPt", StringComparison.OrdinalIgnoreCase)).ToList();
            if (camMessages.Count == 0)
            {
                MessageBox.Show("Recording is empty.");
                return;
            }

            // detect up to 2 vehicles
            var vehicleIds = camMessages
                .Select(l =>
                {
                    try
                    {
                        var msg = V2XMessageParser.ParseV2XMessage(l);
                        return msg?.VehicleID;
                    }
                    catch { return null; }
                })
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .Take(2)
                .ToList();

            _replayFrames.Clear();
            _replayVehicles.Clear();

            var tramFrames = new List<MovementFrame>[drawnTrams.Length];
            for (int i = 0; i < tramFrames.Length; i++)
                tramFrames[i] = new List<MovementFrame>();

            _playbackSpeedByIdAndTs.Clear();
            _replayGeoFrames.Clear();

            DateTime? minUtc = null, maxUtc = null;
            DateTime? firstTime = null;
            foreach (var raw in camMessages)
            {
                try
                {
                    var msg = V2XMessageParser.ParseV2XMessage(raw);
                    if (msg == null || msg.MessageType != "CAM") continue;

                    var tsUtc = msg.Timestamp.ToUniversalTime();
                    if (!minUtc.HasValue || tsUtc < minUtc) minUtc = tsUtc;
                    if (!maxUtc.HasValue || tsUtc > maxUtc) maxUtc = tsUtc;

                    if (!_replayFrames.TryGetValue(msg.VehicleID, out var list))
                    {
                        list = new List<MovementFrame>();
                        _replayFrames[msg.VehicleID] = list;
                    }

                    if (firstTime == null) firstTime = msg.Timestamp;

                    var (rx, ry) = ConvertLatLonToCanvasXY(msg.Latitude, msg.Longitude);
                    var relTs = msg.Timestamp - firstTime.Value;
                    list.Add(new MovementFrame { Timestamp = relTs, Position = new Point(rx, ry) });

                    if (!_replayGeoFrames.TryGetValue(msg.VehicleID, out var geoList))
                    {
                        geoList = new List<(TimeSpan ts, double lat, double lon)>();
                        _replayGeoFrames[msg.VehicleID] = geoList;
                    }
                    geoList.Add((relTs, msg.Latitude, msg.Longitude));

                    string keyHead = $"{msg.VehicleID}|{relTs.Ticks}";
                    _playbackHeadingByIdAndTs[keyHead] = msg.Heading;

                    string keyAll = $"{msg.VehicleID}|{relTs.Ticks}";
                    _playbackSpeedByIdAndTs[keyAll] = msg.Speed; // m/s expected

                    int idx = vehicleIds.IndexOf(msg.VehicleID);
                    if (idx >= 0 && idx < tramFrames.Length)
                    {
                        tramFrames[idx].Add(new MovementFrame
                        {
                            Timestamp = relTs,
                            Position = new Point(rx, ry)
                        });

                        string key = $"{msg.VehicleID}|{relTs.Ticks}";
                        _playbackSpeedByIdAndTs[key] = msg.Speed; // m/s expected
                    }

                    if (TryExtractAltitudeFromCamXml(raw, out var altVal))
                    {
                        string keyAlt = $"{msg.VehicleID}|{relTs.Ticks}";
                        _playbackAltitudeByIdAndTs[keyAlt] = altVal;
                    }
                }
                catch
                {
                    // ignore bad lines
                }
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

            playbackMaxTime = tramFrames.Max(frames => frames.Count > 0 ? frames.Last().Timestamp : TimeSpan.Zero);

            _replayStartUtc = minUtc;
            _replayEndUtc = maxUtc;
            _playbackLoaded = true;
            _lastReplayFile = fileName;
            BuildPlaybackKeyframes();
            UpdateUiEnabledState();
            UpdateReplayTimerLabel();
            MessageBox.Show("CAM recording loaded. Use Play to start playback.", "Playback ready", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Show speed in m/s for live vehicles; keep simulated filter logic unchanged
        // Replace whole method
        private void UpdateVehicleCanvasPosition(MapPoint vehicle, Point newPos, Brush color, bool isSrv, string label, double? speed = null)
        {
            if (vehicle == null) return;

            // Recreate visuals if they were removed (e.g., during replay/refresh)
            EnsureMapPointVisuals(vehicle, color, isSrv);

            // dot
            Canvas.SetLeft(vehicle.Ellipse, newPos.X - 6);
            Canvas.SetTop(vehicle.Ellipse, newPos.Y - 6);

            // id label
            vehicle.Text.Text = isSrv ? label : (label?.Length > 4 ? label[^4..] : label);
            vehicle.Text.Foreground = isSrv ? Brushes.Black : (color ?? vehicle.Text.Foreground);
            Canvas.SetLeft(vehicle.Text, newPos.X + 5);
            Canvas.SetTop(vehicle.Text, newPos.Y - 10);
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
                Canvas.SetLeft(vehicle.Speed, newPos.X + 5);
                Canvas.SetTop(vehicle.Speed, newPos.Y + 5);
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


        // Updating or adding vehicle data in the table
        private void UpdateOrAddVehicleData(string vehicleId, double speed, DateTime messageTime, bool isReplay = false)
        {
            // Display uses the recording/local time (camTimeStr shown in the table)
            DateTime displayLocalTime = TimeZoneInfo.ConvertTimeFromUtc(messageTime.ToUniversalTime(), czechTimeZone);
            string camTimeStr = displayLocalTime.ToString("HH:mm:ss");

            // SecondsSinceLastCam (and cleanup) should be driven by arrival time, not device clock
            DateTime lastMsgStamp = DateTime.Now; // live arrival
            if (isReplay) lastMsgStamp = DateTime.Now; // keep “now” in replay so rows live 23s from processed frame

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
                existing.SecondsSinceLastCam = 0; // reset on each CAM
                existing.LastMessageTimestamp = lastMsgStamp;
            }
        }


        // cam cleanup timer
        private void StartCleanupTimer()
        {
            cleanupTimer = new DispatcherTimer();
            cleanupTimer.Interval = TimeSpan.FromSeconds(1);
            cleanupTimer.Tick += CleanupOldVehicles;
            cleanupTimer.Start();
        }

        private void ActivationZonesCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // odstraněné položky z tabulky => smaž z mapy i z dictionary
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
                        if (TileCanvas.Children.Contains(kvp.Key))
                            TileCanvas.Children.Remove(kvp.Key);
                        activationZones.Remove(kvp.Key);
                    }
                }
            }

            // Added zones => create rectangles like XML load and wire everything
            if (e.NewItems != null)
            {
                double mpp = MetersPerPixel(latitude, zoom);

                foreach (ActivationZone zone in e.NewItems)
                {
                    if (zone == null) continue;

                    // Ensure color
                    var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;

                    // Compute rectangle px size from meters
                    double widthPx = zone.Width > 0 ? zone.Width / mpp : 0;
                    double heightPx = zone.Height > 0 ? zone.Height / mpp : 0;
                    if (widthPx <= 0 || heightPx <= 0)
                    {
                        // Skip until valid dimensions present
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

                        // Base center from latitude/longitude
                        var (sx, sy) = ConvertLatLonToCanvasXY(zone.Latitude, zone.Longitude);
                        zone.StartPoint = new Point(sx, sy);

                        // Position, rotation, bounds
                        UpdateRectanglePositionFromStartPoint(zone);
                        ApplyZoneRotation(zone);
                        UpdateActivationZoneBounds(zone);

                        // Add to canvas + dictionaries
                        TileCanvas.Children.Add(rect);
                        activationZones[rect] = zone;

                        // Events and z-order
                        rect.MouseEnter += Rectangle_MouseEnter;
                        rect.MouseLeave += Rectangle_MouseLeave;
                        rect.MouseLeftButtonDown += Rectangle_MouseLeftButtonDown;
                        Panel.SetZIndex(rect, 100);

                        // Track future edits
                        zone.PropertyChanged += ActivationZone_PropertyChanged;

                        isDirty = true;
                    }
                    else
                    {
                        // Rectangle already exists: ensure dimensions and reproject
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
                    }
                }
            }

            UpdateUiEnabledState();
        }

        // changing properties of the activation zone table
        private void ActivationZone_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (isUpdatingActivationZone) return;
            isUpdatingActivationZone = true;

            try
            {
                var zone = sender as ActivationZone;
                if (zone == null || zone.Rectangle == null) return;

                double mpp = MetersPerPixel(latitude, zoom);

                if (e.PropertyName == nameof(ActivationZone.Width) || e.PropertyName == nameof(ActivationZone.Height))
                {
                    double widthPx = zone.Width / mpp;
                    double heightPx = zone.Height / mpp;

                    zone.Rectangle.Width = widthPx;
                    zone.Rectangle.Height = heightPx;

                    UpdateRectanglePositionFromStartPoint(zone);
                    UpdateActivationZoneBounds(zone);

                    // sync zeměpisných souřadnic základny (start pointu)
                    var baseCenterPoint = new Point(
                        Canvas.GetLeft(zone.Rectangle) + zone.Rectangle.Width / 2.0,
                        Canvas.GetTop(zone.Rectangle) + zone.Rectangle.Height);
                    var latlon = CanvasPixelsToLatLon(baseCenterPoint, latitude, longitude, zoom);
                    zone.Latitude = latlon.Y;
                    zone.Longitude = latlon.X;
                }
                else if (e.PropertyName == nameof(ActivationZone.Azimuth))
                {
                    ApplyZoneRotation(zone);
                    UpdateActivationZoneBounds(zone);
                }
                else if (e.PropertyName == nameof(ActivationZone.Color))
                {
                    try
                    {
                        // nastav novou barvu
                        var brush = TryBrushFromColor(zone.Color) ?? Brushes.Red;
                        zone.Rectangle.Stroke = brush;
                        // krátký “pulse” zvýraznění vlastní barvou (800 ms)
                        EmphasizeZoneWithOwnColor(zone, TimeSpan.FromMilliseconds(800));
                    }
                    catch { }
                }
                else if (e.PropertyName == nameof(ActivationZone.Latitude) ||
                         e.PropertyName == nameof(ActivationZone.Longitude))
                {
                    // Uživatel změnil latitude/longitude => přepočítej StartPoint a posuň obdélník
                    var (x, y) = ConvertLatLonToCanvasXY(zone.Latitude, zone.Longitude);
                    zone.StartPoint = new Point(x, y);

                    UpdateRectanglePositionFromStartPoint(zone);
                    UpdateActivationZoneBounds(zone);

                    isDirty = true;
                }
            }
            finally
            {
                isUpdatingActivationZone = false;
            }
        }

        // Add near other exporting/saving helpers
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

        private void RotateRectangleAroundStartPoint(Rectangle rect, double angleDegrees, Point startPointCanvas, double rectWidth, double rectHeight)
        {
            if (rect == null) return;
            rect.RenderTransform = new RotateTransform(angleDegrees, rect.Width / 2.0, rect.Height);
        }




        private double LonToTileX(double lon, int zoom)
        {
            return (lon + 180.0) / 360.0 * (1 << zoom);
        }

        private double LatToTileY(double lat, int zoom)
        {
            double latRad = lat * Math.PI / 180.0;
            return (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * (1 << zoom);
        }

        private double TileXToLon(double x, int zoom)
        {
            return x / Math.Pow(2, zoom) * 360.0 - 180.0;
        }

        private double TileYToLat(double y, int zoom)
        {
            double n = Math.PI - (2.0 * Math.PI * y) / Math.Pow(2, zoom);
            return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
        }

        private Point CanvasPixelsToLatLon(Point pixelPoint, double centerLat, double centerLon, int zoom)
        {
            // Use the actual loaded top-left tile anchor
            double tileX = _currentTopLeftTileX + (pixelPoint.X / TilePixelSize);
            double tileY = _currentTopLeftTileY + (pixelPoint.Y / TilePixelSize);

            double lon = TileXToLon(tileX, zoom);
            double lat = TileYToLat(tileY, zoom);
            return new Point(lon, lat);
        }


        public void ApplyZoneRotation(ActivationZone zone)
        {
            if (zone?.Rectangle == null) return;

            var rect = zone.Rectangle;
            double w = rect.Width;
            double h = rect.Height;

            rect.RenderTransform = new RotateTransform(zone.Azimuth, w / 2.0, h);
        }
        //|||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||||


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
                string name = el.TryGetProperty("tags", out var tags) &&
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

        // Replace the existing static DrawStops(...) with this instance method
        private void DrawStopsOnCanvasSafe()
        {
            if (TileCanvas == null) return;

            // Použij Dispatcher
            TileCanvas.Dispatcher.Invoke(() =>
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


        // Funkce pro přepočet Lat/Lon → globální pixely
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

        //WPF Component methods

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

        private void rectButton_Click(object sender, RoutedEventArgs e)
        {
            SetDrawingMode(DrawingMode.Rectangle);
            Keyboard.Focus(this);
        }

        private void StopDrawing_Click(object sender, RoutedEventArgs e)
        {
            SetSelectionMode();
            isSelectionMode = true;
        }

        private void RailwayButton_Click(object sender, RoutedEventArgs e)
        {
            isSelectionMode = false;
            currentDrawingMode = DrawingMode.Railway;
            Keyboard.Focus(this);
        }

        private void DrawPoints_Click(object sender, RoutedEventArgs e)
        {
            SetDrawingMode(DrawingMode.Point);
            Keyboard.Focus(this);
        }







        // add near other small constants/fields inside MainWindow

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

            // inside RedrawPlaybackToTime(TimeSpan time), replace the foreach over _replayFrames with this:
            foreach (var kv in _replayFrames)
            {
                var id = kv.Key;
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

                // If we have no frame for this time, skip drawing (vehicle will appear only at/after its first CAM)
                if (!hasFrame) continue;

                // HIDE vehicles with last CAM older than 23s at this timeline position
                var age = time - curFrame.Timestamp;
                if (age > ReplayVisibilityTimeout)
                    continue;

                if (FilterReplayByAltitude(id, curFrame.Timestamp))
                    continue;

                var pos = curFrame.Position;

                if (!vehicleColorMap.TryGetValue(id, out Brush color))
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
                Canvas.SetLeft(text, pos.X + 5);
                Canvas.SetTop(text, pos.Y - 10);
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
                Canvas.SetLeft(speedTb, pos.X + 5);
                Canvas.SetTop(speedTb, pos.Y + 5);
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

                CheckActivationZones(pos, id);
                Panel.SetZIndex(ellipse, 1000);
                Panel.SetZIndex(text, 1000);
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
                    CheckActivationZones(last.Position, pt.Label);
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
                Canvas.SetLeft(text, sx + 8);
                Canvas.SetTop(text, sy - 6);
                TileCanvas.Children.Add(text);

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
            // at the end of RedrawPlaybackToTime(TimeSpan time), before updating ReplaySlider.Value:
            // at the end of RedrawPlaybackToTime(TimeSpan time), replace the seconds update with:
            SyncTramTableForReplay(time);

            if (!isReplaySliderDragging)
                ReplaySlider.Value = _playbackIndex;
        }


        // helper: update TramTable + counters + CamIdLabel for frames exactly at time 't' (replay)
        private void UpdateReplayStatsForTime(TimeSpan t)
        {
            if (_replayFrames == null || _replayFrames.Count == 0) return;

            // Rekonstruuj "wall-clock" čas zprávy, aby LastCamTime seděl na čas z nahrávky
            var msgUtc = _replayStartUtc?.Add(t) ?? DateTime.UtcNow;

            string lastShortId = null;

            foreach (var kv in _replayFrames)
            {
                var id = kv.Key;
                var frames = kv.Value;

                if (FilterReplayByAltitude(id, t))
                    continue;

                // jen rámce, které nastaly přesně v čase t (skokové přehrávání)
                var frame = frames.FirstOrDefault(f => f.Timestamp == t);
                if (frame == null) continue;

                // CAM OK + tabulka
                IncrementCamOkCount();

                lastShortId = !string.IsNullOrEmpty(id) && id.Length > 4 ? id[^4..] : id;

                double speed = 0.0;
                var key = $"{id}|{t.Ticks}";
                if (_playbackSpeedByIdAndTs.TryGetValue(key, out var spd))
                    speed = spd; // m/s

                // Zapíšeme do tabulky (převede si na lokální čas)
                UpdateOrAddVehicleData(lastShortId, speed, msgUtc);
                // inside UpdateReplayStatsForTime(TimeSpan t)
                UpdateOrAddVehicleData(lastShortId, speed, msgUtc, isReplay: true);

            }

            // Poslední zpracované ID do labelu
            if (!string.IsNullOrEmpty(lastShortId))
                CamIdLabel.Content = $"TRAM ID: {lastShortId}";

        }

        private void RefreshMap_Click(object sender, RoutedEventArgs e)
        {
            RefreshMap();
        }

        private void ExportMap_Click(object sender, RoutedEventArgs e)
        {
            ExportCanvasToPng(TileCanvas, "map_export.png");
            Console.WriteLine("Map exported into: map_export.png");
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string portName = ComPortsComboBox.SelectedItem as string;
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
                    MessageBox.Show($"Connected on {portName} at {baudRate} bps.");
                }
                else
                {
                    MessageBox.Show("Port already open.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect: {ex.Message}");
            }
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (serialPort.IsOpen)
                {
                    serialPort.DataReceived -= SerialPort_DataReceived;
                    serialPort.Close();
                    StopSrvAutoTimer();
                    _isConnected = false;

                    // STOP TIMESHIFT
                    StopTimeshiftSession();

                    UpdateUiEnabledState();
                    MessageBox.Show("Disconnected from serial port.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error disconnecting: " + ex.Message);
            }
        }


        private void SaveToXML_Click(object sender, RoutedEventArgs e)
        {
            // consider activation zones and railways too
            bool hasZones = activationZones.Count > 0 || (ActivationZonesCollection?.Count > 0);
            bool hasRailway = railwayLines.Count > 0;

            bool hasAnything =
                hasZones ||
                hasRailway ||
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

        private void PlayMovement_Click(object sender, RoutedEventArgs e)
        {
            if (_keyframes == null || _keyframes.Count == 0)
                BuildPlaybackKeyframes();

            if (_keyframes.Count == 0)
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


        private void btnStopRecording_Click(object sender, RoutedEventArgs e)
        {
            StopRecording();
        }

        private void MapControls_Click(object sender, RoutedEventArgs e)
        {

            MessageBox.Show(ControlsMessage, "Controls");

        }

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



        // Add this helper (near RedrawVehicleTrails/BringAllOverlaysToFront)
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


        // Fix RedrawVehicleTrails to use consistent tags and clean existing ones correctly
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
                        Stroke = vehicle.Ellipse.Fill,
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


        private void CheckBox_Checked(object sender, RoutedEventArgs e)
        {
            RadiusComboBox.IsEnabled = true;
            DrawRadiusCircle();
        }

        private void CheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            RadiusComboBox.IsEnabled = false;

            if (radiusEllipse != null)
            {
                TileCanvas.Children.Remove(radiusEllipse);
            }
        }

        private void RadiusComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CircleCheckBox.IsChecked == true)
                DrawRadiusCircle();

        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Are you sure you want to delete everything?",
                                         "Confirmation",
                                         MessageBoxButton.YesNo,
                                         MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var elementsToRemove = new List<UIElement>();

            foreach (UIElement child in TileCanvas.Children)
            {
                if (child is Rectangle || child is Polyline || child is Line)
                {
                    elementsToRemove.Add(child);
                }
                else if (child is Ellipse ellipse)
                {
                    if (ellipse.Tag == null || (ellipse.Tag.ToString() != "Tram" && ellipse.Tag.ToString() != "Srv"))
                    {
                        elementsToRemove.Add(child);
                    }
                }
                else if (child is TextBlock textBlock)
                {
                    if (textBlock.Tag == null || (textBlock.Tag.ToString() != "Tram" && textBlock.Tag.ToString() != "Srv"))
                    {
                        elementsToRemove.Add(child);
                    }
                }
            }

            foreach (var el in elementsToRemove)
            {
                TileCanvas.Children.Remove(el);
            }

            points.RemoveAll(p => p.Ellipse.Tag == null || (p.Ellipse.Tag.ToString() != "Tram" && p.Ellipse.Tag.ToString() != "Srv"));

            mapRectangles.Clear();
            activationZones.Clear();
            ActivationZonesCollection.Clear();
            connectionLine.Points.Clear();
            TramTable.Clear();

            for (int i = 0; i < drawnTrams.Length; i++)
            {
                drawnTrams[i] = null;
                drawnTramTrailPoints[i].Clear();
                drawnTramTrailGeoPoints[i].Clear(); // nově
                drawnTramLat[i] = null;             // nově
                drawnTramLon[i] = null;             // nově

                if (drawnTramTrails[i] != null)
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
                    loadedFileName = System.IO.Path.Combine(folder, "default_empty_save.xml");
                }
                else
                {
                    loadedFileName = "default_empty_save.xml";
                }
            }

            isDirty = true;

            // Reset drawing state so user can draw points again
            rectPhase = RectangleDrawPhase.None;
            isDrawing = false;
            isSelectionMode = true;
            currentDrawingMode = DrawingMode.Point;
            UpdateHitTestForSelectableElements();
        }

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

        // Úprava výběru přehrávacího souboru – přidej .camrec
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

        // Pause: prefer classic replay pause when a replay is loaded
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

            // Timeshift pause ...
            _timeshiftPaused = true;
            _suppressLiveRender = true;
            _timeshiftFollowLive = false;
            _timeshiftPlaybackCts?.Cancel();
            UpdateUiEnabledState();
        }

        // Resume: replay branch – honor current slider, handle "at end" smartly
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


        private void ComPortsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        private void RefreshComPorts_Click(object sender, RoutedEventArgs e)
        {
            LoadAvailableComPorts();
        }

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



        // Add this helper anywhere in the class (e.g., near other small helpers)
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

        // If loop starts playback from the end, consider session active
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

        private void SendSrv_Click(object sender, RoutedEventArgs e)
        {
            SendSrvMessage();
        }

        private void SrvCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (serialPort != null && serialPort.IsOpen)
            {
                SendSrvMessage();
                StartSrvAutoTimerIfEnabled();
            }
        }


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

        private void StopSrvAutoTimer()
        {
            if (_srvTimer != null)
                _srvTimer.Stop();
        }

        // UpdateUiEnabledState – zohledni stav nahrávání pro tlačítka
        // Keep classic replay slider enabled only when a replay is loaded
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
            rectButton?.SetValue(IsEnabledProperty, _isConnected);
            RailwayButton?.SetValue(IsEnabledProperty, _isConnected);
            StopDrawing?.SetValue(IsEnabledProperty, _isConnected);
            DrawPoints?.SetValue(IsEnabledProperty, _isConnected);
            Tram1TB?.SetValue(IsEnabledProperty, _isConnected);
            Tram2TB?.SetValue(IsEnabledProperty, _isConnected);
            SpeedBox?.SetValue(IsEnabledProperty, _isConnected);

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

            // Timeshift controls
            btnPlay?.SetValue(IsEnabledProperty, _playbackLoaded);
            btnPause?.SetValue(IsEnabledProperty, _playbackLoaded);
            FilterCheckBox?.SetValue(IsEnabledProperty, _isConnected || _playbackLoaded);
            ExportButton?.SetValue(IsEnabledProperty, ActivationZonesCollection != null && ActivationZonesCollection.Count > 0);
            ZoneRadio?.SetValue(IsEnabledProperty, _isConnected);
            SwitchRadio?.SetValue(IsEnabledProperty, _isConnected);
            CircleCheckBox?.SetValue(IsEnabledProperty, _isConnected);
        }

        // přidej do třídy
        // Replace the whole body of ReprojectActivationZonesOnMapChange()
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

        public async Task ReprojectAllZonesOnMapChange()
        {
            ReprojectActivationZonesOnMapChange();
            ReprojectSwitchZonesOnMapChange();
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
                        headingDeg = (headingDeg - 180 + 360) % 360; // keep manual flip rule
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

            // BuildPlaybackKeyframes() – include SRV timestamps as keyframes
            foreach (var srv in _replaySrvFramesById.Values)
                foreach (var f in srv)
                    set.Add(f.ts);

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

        // Send CAMs for frames that occur exactly at time 't'
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

        private void NewRow_Click(object sender, RoutedEventArgs e)
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

            _pendingNewZone = zone;

            // mark as switch-row if SwitchRadio is active (so finalize will tag it properly)
            if (IsSwitchMode())
                _switchRows.Add(zone);

            ActivationZonesCollection.Add(zone);

            // Focus first cell in the single grid
            Dispatcher.BeginInvoke(new Action(() => { FocusCell(zone, 0); }), DispatcherPriority.Background);
        }


        private bool TryFinalizePendingNewZone()
        {
            var zone = _pendingNewZone;
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
                // tag by intended type (hashset) or by name-prefix fallback
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

        // Validate and finalize new row on commit

        // Helper to focus a specific cell (by column index) for a given zone
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

        // Focus first missing required field in order: Name, Latitude, Longitude, Azimuth, Width, Height
        private void FocusFirstMissingField(ActivationZone zone)
        {
            if (string.IsNullOrWhiteSpace(zone.Name)) { FocusCell(zone, 0); return; }
            if (double.IsNaN(zone.Latitude)) { FocusCell(zone, 1); return; }
            if (double.IsNaN(zone.Longitude)) { FocusCell(zone, 2); return; }
            if (zone.Azimuth < 0 || zone.Azimuth > 359) { FocusCell(zone, 3); return; }
            if (zone.Width <= 0) { FocusCell(zone, 4); return; }
            if (zone.Height <= 0) { FocusCell(zone, 5); return; }
        }


        // Add helper to fully clear playback trams from canvas and state
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
        }

        // Add helper to stop playback and reset timeline + visuals
        // Stop completely (button Stop replay or programmatic stop)
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

            _replayFrames.Clear();
            _replayVehicles.Clear();
            _playbackHeadingByIdAndTs.Clear();
            foreach (var kv in _replayBoxes.ToList())
                TileCanvas.Children.Remove(kv.Value);
            _replayBoxes.Clear();

            try { UpdateTimerLabel(); UpdateReplayTimerLabel(); } catch { }
        }


        // Smaž všechny živé CAM tramvaje (ponech SRV)
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

        private void StopReplay_Click(object sender, RoutedEventArgs e)
        {
            StopPlaybackAndReset();
            UpdateUiEnabledState();
        }

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

        private void UpdateTimeshiftTimerLabel(TimeSpan? elapsedOverride = null)
        {
            try
            {
                var elapsed = elapsedOverride ?? (DateTime.UtcNow - _timeshiftStartUtc);
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;


            }
            catch { }
        }


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

        private void ReplaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_keyframes.Count == 0) return;

            // Vypočti cílový čas podle pozice slideru
            int idx = (int)Math.Round(ReplaySlider.Value);
            idx = Math.Clamp(idx, 0, Math.Max(0, _keyframes.Count - 1));
            var t = _keyframes[idx];

            if (isReplaySliderDragging)
            {
                _playbackIndex = idx;
                playbackElapsedTime = t;
                UpdateReplayTimerLabel();
                UpdateTimerLabel();
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

        private void ActivationZonesDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
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

                bool isSwitch = IsSwitchZone(zone);

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
        }

        private void ActivationZonesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Obnov předchozí styl
            if (_highlightedRect != null)
            {
                try
                {
                    _highlightedRect.Stroke = _highlightedRectOldBrush ?? _highlightedRect.Stroke;
                    _highlightedRect.StrokeThickness = _highlightedRectOldThickness > 0 ? _highlightedRectOldThickness : _highlightedRect.StrokeThickness;
                    // nevracíme Fill – ponecháme Transparent
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
            _highlightedRectOldBrush = _highlightedRect.Stroke;
            _highlightedRectOldThickness = _highlightedRect.StrokeThickness;

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
                    if (zone.Rectangle != null)
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

            // labels
            CamOkLabel.Content = "CAM OK: 0";
            CamErrorLabel.Content = "CAM Errors: 0";
            CamIdLabel.Content = "TRAM ID: --";

            SrvOkLabel.Content = "SRV OK: 0";
            SrvErrorLabel.Content = "SRV Errors: 0";
            SrvIdLabel.Content = "SRV ID: --";
            LastSrvLabel.Content = "Last SRV: --:--:--";

            // last message caches
            lastCamTimes.Clear();
            prevCamTimes.Clear();
            lastCamUpdates.Clear();
        }

        private static bool TryGetInterpolatedPosition(List<MovementFrame> frames, TimeSpan time, out Point pos, out MovementFrame prevFrameOut)
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

        private static bool TryGetStepPosition(List<MovementFrame> frames, TimeSpan time, out MovementFrame current, out MovementFrame prev)
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

        private void NextCam_Click(object sender, RoutedEventArgs e)
        {
            if (_replayFrames == null || _replayFrames.Count == 0) return;

            var current = playbackElapsedTime;
            if (!TryFindNextCamTime(current, out var t))
                return; // already at last CAM

            playbackElapsedTime = t;
            _playbackIndex = GetIndexForTime(t);
            playbackStartTime = DateTime.Now - playbackElapsedTime;

            RedrawPlaybackToTime(t);
            UpdateReplayTimerLabel();
            UpdateTimerLabel();

            UpdateReplayStatsForTime(t);
            SyncTramTableForReplay(t);

            // If we were playing, keep timer running; otherwise stay paused
            if (isPlaying && playbackTimer != null && !playbackTimer.IsEnabled)
                playbackTimer.Start();
        }

        private void PrevCam_Click(object sender, RoutedEventArgs e)
        {
            if (_replayFrames == null || _replayFrames.Count == 0) return;

            var current = playbackElapsedTime;
            if (!TryFindPrevCamTime(current, out var t))
                return; // already at first CAM

            playbackElapsedTime = t;
            _playbackIndex = GetIndexForTime(t);
            playbackStartTime = DateTime.Now - playbackElapsedTime;

            RedrawPlaybackToTime(t);
            UpdateReplayTimerLabel();
            UpdateTimerLabel();

            UpdateReplayStatsForTime(t);
            SyncTramTableForReplay(t);

            if (isPlaying && playbackTimer != null && !playbackTimer.IsEnabled)
                playbackTimer.Start();
        }

        // Add near other small helpers (e.g., under Lerp)
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

        private bool RailwayLineAlreadyExists(double x1, double y1, double x2, double y2, Color stroke, double thickness, double eps = 0.5)
        {
            var p1 = new Point(x1, y1);
            var p2 = new Point(x2, y2);

            foreach (var l in railwayLines)
            {
                var lp1 = new Point(l.X1, l.Y1);
                var lp2 = new Point(l.X2, l.Y2);

                // same endpoints (any direction)
                bool endpointsSame = (SamePoint(lp1, p1, eps) && SamePoint(lp2, p2, eps)) ||
                                     (SamePoint(lp1, p2, eps) && SamePoint(lp2, p1, eps));
                if (!endpointsSame) continue;

                // same style
                var lcolor = (l.Stroke as SolidColorBrush)?.Color ?? Colors.Black;
                if (lcolor != stroke) continue;
                if (!AlmostEqual(l.StrokeThickness, thickness, 1e-6)) continue;

                return true;
            }
            return false;
        }

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
            for (int i = 0; i < drawnTramTrailPoints.Length; i++)
            {
                while (drawnTramTrailPoints[i].Count > _maxTrailLength + 1)
                    drawnTramTrailPoints[i].RemoveAt(0);

                while (drawnTramTrailGeoPoints[i].Count > _maxTrailLength + 1)
                    drawnTramTrailGeoPoints[i].RemoveAt(0);

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
                    drawnTramTrails[i].Points.Clear();
                    foreach (var p in drawnTramTrailPoints[i])
                        drawnTramTrails[i].Points.Add(p);
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

        // place near GetIndexForTime or other small helpers
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
            return found;
        }

        // add near other small helpers (below UpdateSecondsSinceLastCamForReplay or replace its usage)
        private void SyncTramTableForReplay(TimeSpan t)
        {
            if (!_playbackLoaded || _replayFrames == null || _replayFrames.Count == 0) return;


            var keep = new HashSet<string>(StringComparer.Ordinal);

            foreach (var kv in _replayFrames)
            {
                var fullId = kv.Key;
                var frames = kv.Value;
                if (frames == null || frames.Count == 0) continue;

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

                // v SyncTramTableForReplay(TimeSpan t) před keep.Add(shortId):
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

        // Add near other small helpers (e.g., below SyncTramTableForReplay)
        private bool IsTramTableRowPresentForId(string fullId)
        {
            if (string.IsNullOrEmpty(fullId)) return false;
            string shortId = fullId.Length > 4 ? fullId[^4..] : fullId;
            return TramTable.Any(r => r.VehicleId == shortId);
        }

        private void ResetVehicleInstance(string id)
        {
            if (!activeVehicles.TryGetValue(id, out var vehicle)) return;

            // remove visuals and cached trail
            if (vehicle.Ellipse != null) TileCanvas.Children.Remove(vehicle.Ellipse);
            if (vehicle.Text != null) TileCanvas.Children.Remove(vehicle.Text);
            if (vehicle.Speed != null) TileCanvas.Children.Remove(vehicle.Speed);

            var oldLines = TileCanvas.Children.OfType<Polyline>()
                .Where(pl => string.Equals(pl.Tag as string, $"tram_trail_{id}", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var l in oldLines) TileCanvas.Children.Remove(l);

            if (vehicle.TrailDots != null)
            {
                foreach (var d in vehicle.TrailDots.ToList()) TileCanvas.Children.Remove(d);
                vehicle.TrailDots.Clear();
            }

            if (_vehicleBoxes.TryGetValue(id, out var box))
            {
                TileCanvas.Children.Remove(box);
                _vehicleBoxes.Remove(id);
            }

            if (vehicleTrailCleanupTokens.TryGetValue(id, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                vehicleTrailCleanupTokens.Remove(id);
            }

            activeVehicles.Remove(id);
        }

        // Add inside MainWindow class (near other small helpers)
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

        private void ActivationZonesDataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            _suspendZoneLiveSort = true;
            SetZonesLiveSorting(false);
        }

        private void ActivationZonesDataGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
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

        // Next automatic indices for switch zones (fill 0..6 in each main 0..4) within single collection
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
            return (0, 0);
        }

        private void ReadButtonMain_Click(object sender, RoutedEventArgs e)
        {
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
                    _highlightedRect.Stroke = _highlightedRectOldBrush ?? _highlightedRect.Stroke;
                    _highlightedRect.StrokeThickness = _highlightedRectOldThickness > 0 ? _highlightedRectOldThickness : _highlightedRect.StrokeThickness;
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
            _highlightedRectOldBrush = _highlightedRect.Stroke;
            _highlightedRectOldThickness = _highlightedRect.StrokeThickness;

            EmphasizeZoneWithOwnColor(zone, revertAfter: null);
        }

        private void SwitchZonesDataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            _suspendSwitchZoneLiveSort = true;
            SetSwitchZonesLiveSorting(false);
        }

        private void SwitchZonesDataGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
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

        private void SwitchZonesDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
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

        // Next automatic indices for switch zones (fill 0..6 in each main 0..4)


        private void ZoneRadio_Checked(object sender, RoutedEventArgs e)
        {
            // If user switches mode mid-draw, route new rectangles to zones
            if (currentDrawingMode == DrawingMode.Rectangle && rectPhase != RectangleDrawPhase.None)
                _drawToSwitchZones = false;

            // Clear current UI zones (map + table), do not touch MPC
            ClearMapAndTable();

            UpdateUiEnabledState();
        }

        private void SwitchRadio_Checked(object sender, RoutedEventArgs e)
        {
            // If user switches mode mid-draw, route new rectangles to switches
            if (currentDrawingMode == DrawingMode.Rectangle && rectPhase != RectangleDrawPhase.None)
                _drawToSwitchZones = true;

            // Clear current UI zones (map + table), do not touch MPC
            ClearMapAndTable();

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
        private void OpenTerminal_Click(object sender, RoutedEventArgs e)
        {
            if (_terminalWindow == null)
            {
                _terminalWindow = new TerminalWindow { Owner = this };
                _terminalWindow.Closed += (s, ev) => _terminalWindow = null;

                // flush buffered lines
                foreach (var (text, color) in _terminalBuffer)
                    _terminalWindow.Append(text, color);

                _terminalWindow.Show();
            }
            else
            {
                if (_terminalWindow.WindowState == WindowState.Minimized)
                    _terminalWindow.WindowState = WindowState.Normal;
                _terminalWindow.Activate();
            }
        }

        private void TerminalLog(string text, Brush color)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {text}";

            // buffer
            _terminalBuffer.Add((line, color));
            if (_terminalBuffer.Count > 2000)
                _terminalBuffer.RemoveAt(0);

            // live window
            if (_terminalWindow != null)
            {
                try { _terminalWindow.Append(line, color); } catch { /* ignore */ }
            }
        }

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
            Stop nearest = null;
            double best = double.MaxValue;
            foreach (var s in stops)
            {
                double d = HaversineMeters(cam.Latitude, cam.Longitude, s.Latitude, s.Longitude);
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
                    TerminalLog($"DEPART: {cam.VehicleID[^Math.Min(4, cam.VehicleID.Length)..]} from {prevName}", Brushes.LimeGreen);
                }

                string name = string.IsNullOrWhiteSpace(nearest.StopName) ? "(bez názvu)" : nearest.StopName;
                TerminalLog($"ARRIVE: {cam.VehicleID[^Math.Min(4, cam.VehicleID.Length)..]} at {name} ({best:F0} m)", Brushes.LimeGreen);
                _vehCurrentStop[cam.VehicleID] = nearest;
                return;
            }

            // depart (was inside before, now outside)
            if (!insideNow && current != null)
            {
                string name = string.IsNullOrWhiteSpace(current.StopName) ? "(bez názvu)" : current.StopName;
                TerminalLog($"DEPART: {cam.VehicleID[^Math.Min(4, cam.VehicleID.Length)..]} from {name}", Brushes.LimeGreen);
                _vehCurrentStop[cam.VehicleID] = null;
            }
        }
    }
}
    


