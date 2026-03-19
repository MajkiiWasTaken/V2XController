using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Shapes;

namespace V2XController
{
    public class ActivationZone : INotifyPropertyChanged
    {
        private string name;
        private Rectangle rectangle;
        private double width;
        private double height;
        private int azimuth;
        private string lastTramId;
        private Rect bounds;
        private bool isActive;
        private string color = "#FF0000";
        private Point startPoint;
        private double latitude;
        private double longitude;
        private int mainZone = 0;
        private int subZone = 0;
        private bool isSwitchZone = false;

        public Point StartPoint
        {
            get => startPoint;
            set { startPoint = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => name;
            set { name = value; OnPropertyChanged(); }
        }

        public Rectangle Rectangle
        {
            get => rectangle;
            set { rectangle = value; OnPropertyChanged(); }
        }

        public double Width
        {
            get => width;
            set { width = Math.Round(value, 2); OnPropertyChanged(); }
        }

        public double Height
        {
            get => height;
            set { height = Math.Round(value, 2); OnPropertyChanged(); }
        }

        public string LastTramId
        {
            get => lastTramId;
            set { lastTramId = value; OnPropertyChanged(); }
        }

        public Rect Bounds
        {
            get => bounds;
            set { bounds = value; OnPropertyChanged(); }
        }

        public bool IsActive
        {
            get => isActive;
            set { isActive = value; OnPropertyChanged(); }
        }

        public string Color
        {
            get => color;
            set { color = value; OnPropertyChanged(); }
        }

        public double Latitude
        {
            get => latitude;
            set { latitude = value; OnPropertyChanged(); }
        }

        public double Longitude
        {
            get => longitude;
            set { longitude = value; OnPropertyChanged(); }
        }

        public int Azimuth
        {
            get => azimuth;
            set { azimuth = value; OnPropertyChanged(); }
        }

        // Updated clamp to support Main 0..4 for switch zones
        public int MainZone
        {
            get => mainZone;
            set
            {
                var v = Math.Clamp(value, 0, 4);
                if (mainZone != v)
                {
                    mainZone = v;
                    OnPropertyChanged();
                    UpdateName();
                }
            }
        }

        public int SubZone
        {
            get => subZone;
            set
            {
                var v = Math.Clamp(value, 0, 6);
                if (subZone != v)
                {
                    subZone = v;
                    OnPropertyChanged();
                    UpdateName();
                }
            }
        }

        public bool IsSwitchZone
        {
            get => isSwitchZone;
            set { isSwitchZone = value; OnPropertyChanged(); }
        }

        // Add this method to ActivationZone class
        public void UpdateName()
        {
            int linearIdx = (mainZone * 7) + subZone;

            // Zone structure (35 zones total):
            // 0-6:   P 1-1 to P 1-7 (mainZone=0, subZone=0-6)
            // 7-13:  P 2-1 to P 2-7 (mainZone=1, subZone=0-6)
            // 14-20: B 1 to B 7    (mainZone=2, subZone=0-6)
            // 21-27: V 1-1 to V 1-7 (mainZone=3, subZone=0-6)
            // 28-34: V 2-1 to V 2-7 (mainZone=4, subZone=0-6)

            if (linearIdx >= 0 && linearIdx <= 6)
                Name = $"P 1-{linearIdx + 1}";
            else if (linearIdx >= 7 && linearIdx <= 13)
                Name = $"P 2-{linearIdx - 6}";
            else if (linearIdx >= 14 && linearIdx <= 20)
                Name = $"B {linearIdx - 13}";
            else if (linearIdx >= 21 && linearIdx <= 27)
                Name = $"V 1-{linearIdx - 20}";
            else if (linearIdx >= 28 && linearIdx <= 34)
                Name = $"V 2-{linearIdx - 27}";
            else
                Name = $"Zone {mainZone + 1}-{subZone + 1}";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}