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
                }
            }
        }

        // Updated clamp to support Sub 0..6 for switch zones
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
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}