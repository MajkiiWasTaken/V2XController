using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Shapes;

public class ActivationZone : INotifyPropertyChanged
{
    private string name;
    private Rectangle rectangle;
    private double width;
    private double height;
    private int azimuth;
    private string lastTramId;
    private Rect bounds;
    private bool isActive;
    private string color = "#FF0000";
    private Point startPoint;
    private double latitude;
    private double longitude;
    private int mainZone = 0;
    private int subZone = 0;
    public bool isSwitchZone = false;

    // NEW: Polyline segment tracking
    private Guid? polylineId;
    private int segmentIndex = -1;
    private string segmentType = ""; // "Přibližovací", "Blokovací", "Vzdalovací"

    public Guid? PolylineId
    {
        get => polylineId;
        set { polylineId = value; OnPropertyChanged(); }
    }

    public int SegmentIndex
    {
        get => segmentIndex;
        set { segmentIndex = value; OnPropertyChanged(); }
    }

    public string SegmentType
    {
        get => segmentType;
        set { segmentType = value; OnPropertyChanged(); UpdateName(); }
    }

    public bool IsPolylineSegment => polylineId.HasValue && segmentIndex >= 0;

    public Point StartPoint
    {
        get => startPoint;
        set { startPoint = value; OnPropertyChanged(); }
    }

    public string Name
    {
        get => name;
        set { name = value; OnPropertyChanged(); }
    }

    public Rectangle Rectangle
    {
        get => rectangle;
        set { rectangle = value; OnPropertyChanged(); }
    }

    public double Width
    {
        get => width;
        set { width = Math.Round(value, 2); OnPropertyChanged(); }
    }

    public double Height
    {
        get => height;
        set { height = Math.Round(value, 2); OnPropertyChanged(); }
    }

    public string LastTramId
    {
        get => lastTramId;
        set { lastTramId = value; OnPropertyChanged(); }
    }

    public Rect Bounds
    {
        get => bounds;
        set { bounds = value; OnPropertyChanged(); }
    }

    public bool IsActive
    {
        get => isActive;
        set { isActive = value; OnPropertyChanged(); }
    }

    public string Color
    {
        get => color;
        set { color = value; OnPropertyChanged(); }
    }

    public double Latitude
    {
        get => latitude;
        set { latitude = value; OnPropertyChanged(); }
    }

    public double Longitude
    {
        get => longitude;
        set { longitude = value; OnPropertyChanged(); }
    }

    public int Azimuth
    {
        get => azimuth;
        set { azimuth = value; OnPropertyChanged(); }
    }

    public int MainZone
    {
        get => mainZone;
        set
        {
            if (mainZone != value)
            {
                mainZone = value;
                OnPropertyChanged();
                UpdateName();
            }
        }
    }

    public int SubZone
    {
        get => subZone;
        set
        {
            if (subZone != value)
            {
                subZone = value;
                OnPropertyChanged();
                UpdateName();
            }
        }
    }

    public bool IsSwitchZone
    {
        get => isSwitchZone;
        set { isSwitchZone = value; OnPropertyChanged(); UpdateName(); }
    }

    public void UpdateName()
    {
        if (IsPolylineSegment)
        {
            if (!string.IsNullOrWhiteSpace(segmentType))
            {
                Name = $"{segmentType}-{segmentIndex + 1}";
            }
            else
            {
                Name = $"Segment {segmentIndex + 1}";
            }
            return;
        }

        if (isSwitchZone)
        {
            switch (mainZone)
            {
                case 0:
                    Name = $"P1-{subZone + 1}";
                    break;
                case 1:
                    Name = $"P2-{subZone + 1}";
                    break;
                case 2:
                    Name = $"B{subZone + 1}";
                    break;
                case 3:
                    Name = $"V1-{subZone + 1}";
                    break;
                case 4:
                    Name = $"V2-{subZone + 1}";
                    break;
                default:
                    Name = $"Switch {mainZone + 1}-{subZone + 1}";
                    break;
            }
        }
        else
        {
            Name = $"Z{mainZone + 1}-{subZone + 1}";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}