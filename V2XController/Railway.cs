using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Shapes;

/**********************************************************************************************************
 * V2X Controller - Railway.cs
 * Author: Michal Švrček
 * Version: 1.0.0
 * Description: Represents a railway segment in the V2X Controller application, containing start and end 
 *              coordinates, line properties, and visual attributes.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/


namespace V2XController
{
    public class Railway
    {
        public double Lat1 { get; set; }
        public double Lon1 { get; set; }
        public double Lat2 { get; set; }
        public double Lon2 { get; set; }
        public Line Line { get; set; }
        public string Color { get; set; } = "#000000";
        public double Thickness { get; set; } = 2.0;
    }
}