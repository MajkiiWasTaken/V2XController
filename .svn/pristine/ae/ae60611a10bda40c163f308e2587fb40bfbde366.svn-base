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

        // OPRAVENO: Bez Math.Clamp - validace se dělá v UI layer (DataGrid CellEditEnding)
        public int MainZone
        {
            get => mainZone;
            set
            {
                if (mainZone != value)
                {
                    mainZone = value;
                    OnPropertyChanged();
                    UpdateName();
                }
            }
        }

        // OPRAVENO: Bez Math.Clamp - validace se dělá v UI layer (DataGrid CellEditEnding)
        public int SubZone
        {
            get => subZone;
            set
            {
                if (subZone != value)
                {
                    subZone = value;
                    OnPropertyChanged();
                    UpdateName();
                }
            }
        }

        public bool IsSwitchZone
        {
            get => isSwitchZone;
            set { isSwitchZone = value; OnPropertyChanged(); UpdateName(); }
        }

        // Add this method to ActivationZone class
        public void UpdateName()
        {
            if (isSwitchZone)
            {
                // RTV režim: Switches (35 zón celkem)
                // Struktura:
                // mainZone=0: P1-1 až P1-7 (Přibližovací 1)
                // mainZone=1: P2-1 až P2-7 (Přibližovací 2)
                // mainZone=2: B1 až B7 (Blokovací)
                // mainZone=3: V1-1 až V1-7 (Vzdalovací 1)
                // mainZone=4: V2-1 až V2-7 (Vzdalovací 2)

                switch (mainZone)
                {
                    case 0:
                        Name = $"P1-{subZone + 1}";
                        break;
                    case 1:
                        Name = $"P2-{subZone + 1}";
                        break;
                    case 2:
                        Name = $"B{subZone + 1}";
                        break;
                    case 3:
                        Name = $"V1-{subZone + 1}";
                        break;
                    case 4:
                        Name = $"V2-{subZone + 1}";
                        break;
                    default:
                        Name = $"Switch {mainZone + 1}-{subZone + 1}";
                        break;
                }
            }
            else
            {
                // WLC režim: Activation Zones (20 zón celkem)
                // Struktura:
                // mainZone=0: Z1-1 až Z1-5
                // mainZone=1: Z2-1 až Z2-5
                // mainZone=2: Z3-1 až Z3-5
                // mainZone=3: Z4-1 až Z4-5
                Name = $"Z{mainZone + 1}-{subZone + 1}";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}