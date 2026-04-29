using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

/**********************************************************************************************************
 * V2X Controller - Table.cs
 * Author: Michal Švrček
 * Version: 2.1.2
 * Description: Represents a data table for the V2X Controller application. Contains properties for various
 *              attributes such as station ID, type, GPS coordinates, speed, azimuth, last received time,
 *              distance, line number, vehicle number, and embarkation status.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/



namespace V2XController
{

    public class Table
    {
        public string StatId { get; set; }
        public string Type { get; set; }
        public string Latitude { get; set; }
        public string Longitude { get; set; }
        public string Alt { get; set; }
        public string Speed { get; set; }
        public string Azimuth { get; set; }
        public string LastRec { get; set; }
        public string Dist { get; set; }
        public string LineNum { get; set; }
        public string VehNum { get; set; }
        public string Embarkation { get; set; }
    }


}
