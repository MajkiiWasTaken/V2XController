using System.ComponentModel;
using System.Runtime.CompilerServices;

/**********************************************************************************************************
 * V2X Controller - TramInfo.cs
 * Author: Michal Švrček
 * Version: 1.0.0
 * Description: Represents tram information within the V2X Controller application. Handles properties such as
 *              VehicleID, Speed, LastCamTime, TimeSinceLastMessage, LastMessageTimestamp, and SecondsSinceLastCam.
 *              Provides a data structure for storing and processing tram information in real-time.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/


namespace V2XController
{
    public class TramInfo : INotifyPropertyChanged
    {
        private double speed;

        private string lastCamTime;

        private int timeSinceLastMessage;

        private int secondsSinceLastCam;

        public event PropertyChangedEventHandler PropertyChanged;

        ///////////////////////////////////////////////////////////

        public string VehicleId { get; set; }

        public double Speed
        {
            get => speed;
            set { speed = value; OnPropertyChanged(); }
        }

        public string LastCamTime
        {
            get => lastCamTime;
            set { lastCamTime = value; OnPropertyChanged(); }
        }

        public int TimeSinceLastMessage
        {
            get => timeSinceLastMessage;
            set { timeSinceLastMessage = value; OnPropertyChanged(); }
        }

        public DateTime? LastMessageTimestamp { get; set; }

        
        protected void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }

        public int SecondsSinceLastCam
        {
            get => secondsSinceLastCam;
            set { secondsSinceLastCam = value; OnPropertyChanged(); }
        }

    }
}
