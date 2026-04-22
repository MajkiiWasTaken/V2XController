using System.Windows;

/**********************************************************************************************************
 * V2X Controller - MovementFrame.cs
 * Author: Michal Švrček
 * Version: 1.0.0
 * Description: Represents a movement frame in the V2X Controller application, containing timestamp and 
 *              position information.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/


namespace V2XController
{
    internal class MovementFrame
    {
        public TimeSpan Timestamp {  get; set; }
        public Point Position { get; set; }

    }
}
