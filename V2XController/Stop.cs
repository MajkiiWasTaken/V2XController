using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/**********************************************************************************************************
 * V2X Controller - Stop.cs
 * Author: Michal Švrček
 * Version: 1.0.0
 * Description: Represents a stop in the V2X Controller application. Contains properties for the stop's name,
 *              latitude, and longitude.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/


namespace V2XController
{
    public class Stop
    {
        public string StopName { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }

    }
}
