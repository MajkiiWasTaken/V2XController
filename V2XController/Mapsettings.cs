using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/**********************************************************************************************************
* V2X Controller - Mapsettings.cs
* Author: Michal Švrček
* Version: 1.0.0
* Description: Represents map settings in the V2X Controller application, containing latitude and longitude 
*              information.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/


namespace V2XController
{
    internal class Mapsettings
    {
        private static double latitude = 49.842432;
        private static double longitude = 18.276736;

        public static double Latitude
        {
            get => latitude;
            set => latitude = value;
        }

        public static double Longitude
        {
            get => longitude;
            set => longitude = value;
        }
    }
}
