using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

/**********************************************************************************************************
 * V2X Controller - MapPoint.cs
 * Author: Michal Švrček
 * Version: 1.1.2
 * Description: Represents a simulated tram in the V2X Controller application, containing position, label, 
 *              and visual properties.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/


namespace V2XController
{
    internal class MapPoint
    {
        public Point Position { get; set; }
        public string Label { get; set; }
        public Ellipse Ellipse { get; set; }
        public TextBlock Text { get; set; }
        public TextBlock Speed { get; set; }
        public List<MovementFrame> MovementFrames { get; set; } = new();
        public DateTime LastUpdate { get; set; } = DateTime.MinValue;
        public Brush VehicleColor { get; set; }
        public string VehicleID { get; set; }
        public List<Ellipse> TrailDots { get; set; } = new List<Ellipse>();

        // Add geo trail points for smooth map panning
        public List<(double lat, double lon)> TrailGeoPoints { get; set; } = new List<(double lat, double lon)>();

        public bool IsRecorded { get; set; }
    }
}