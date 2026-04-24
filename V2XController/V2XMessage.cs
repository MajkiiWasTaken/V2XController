using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

/**********************************************************************************************************
 * V2X Controller - V2XMessage.cs
 * Author: Michal Švrček
 * Version: 1.0.0
 * Description: Represents a V2X message within the V2X Controller application. Handles properties such as
 *              VehicleID, MessageID, Timestamp, Latitude, Longitude, Speed, Heading, Altitude, MessageType,
 *              RawContent, DistanceMeters, and IsManual. Provides a data structure for storing and processing             
 *              V2X messages in real-time.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/


namespace V2XController
{
    internal class V2XMessage
    {

        public string VehicleID { get; set; }
        public string MessageID { get; set; }
        public DateTime Timestamp { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Speed { get; set; }
        public double Heading { get; set; }
        public double Altitude { get; set; }
        public string MessageType { get; set; } //BSM,CAM, etc.
        public string RawContent { get; set; }
        public double DistanceMeters { get; set; }
        public bool IsManual { get; set; } = false;

    }
}
