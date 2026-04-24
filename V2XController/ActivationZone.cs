using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Shapes;

/**********************************************************************************************************
 * V2X Controller - ActivationZone.cs
 * Author: Michal Švrček
 * Version: 1.0.7
 * Description: Represents an activation zone within the V2X Controller application. Handles properties such as
 *              name, rectangle, width, height, azimuth, last tram ID, bounds, active state, color, start point,
 *              latitude, longitude, main zone, sub zone, and switch zone status. Provides property change
 *              notifications for data binding in the UI.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/

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

    private Guid? polylineId;
    private int segmentIndex = -1;
    private string segmentType = "";

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

    private static bool IsPbvSegmentType(string st)
    {
        if (string.IsNullOrWhiteSpace(st))
            return false;

        var s = st.Trim().ToUpperInvariant();

        // Common explicit markers
        if (s == "RTV")
            return true;

        // Single-letter markers
        if (s.Length == 1 && (s == "P" || s == "B" || s == "V"))
            return true;

        // Czech words or longer markers -> check for substrings (case-insensitive)
        s = st.ToLowerInvariant();
        if (s.Contains("přibl") || s.Contains("pribl") || s.Contains("blok") || s.Contains("vzdal"))
            return true;

        return false;
    }

    public void UpdateName()
    {
        // Displayed sub-zone index: clamp to 1..7 per requirement (internal stored is 0-based)
        int sub = Math.Clamp(subZone, 0, 6) + 1;

        if (IsPolylineSegment)
        {
            // Choose PBV naming if segment type looks like RTV/P/B/V or if this activation zone is marked as switch.
            bool usePbvNaming = IsPbvSegmentType(segmentType);

            if (usePbvNaming)
            {
                if (mainZone <= 1)
                {
                    int adjustedMain = mainZone + 1;
                    Name = $"P{adjustedMain}-{sub}";
                }
                else if (mainZone == 2)
                {
                    Name = $"B{sub}";
                }
                else
                {
                    int adjustedMain = mainZone - 2;
                    Name = $"V{adjustedMain}-{sub}";
                }
            }
            else
            {
                // Default WLC naming
                Name = $"Z{mainZone + 1}-{sub}";
            }
            return;
        }

        // Non-polyline (regular switch / normal areas)
        if (isSwitchZone)
        {
            switch (mainZone)
            {
                case 0:
                    Name = $"P1-{sub}";
                    break;
                case 1:
                    Name = $"P2-{sub}";
                    break;
                case 2:
                    Name = $"B{sub}";
                    break;
                case 3:
                    Name = $"V1-{sub}";
                    break;
                case 4:
                    Name = $"V2-{sub}";
                    break;
                default:
                    Name = $"Switch {mainZone + 1}-{sub}";
                    break;
            }
        }
        else
        {
            Name = $"Z{mainZone + 1}-{sub}";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string propName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}