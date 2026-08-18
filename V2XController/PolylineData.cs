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

