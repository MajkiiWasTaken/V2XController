using System.ComponentModel;
using System.Runtime.CompilerServices;

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
