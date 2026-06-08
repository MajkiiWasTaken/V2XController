using System.Windows;
using System.Windows.Controls;

namespace V2XController // prvni cislo v seznamu je vpravo druhe vlevo
{
    public partial class TramSignalControlLeft : UserControl
    {
        public static readonly DependencyProperty DirectionLeftProperty =
            DependencyProperty.Register(
                nameof(Left),
                typeof(TramSignalDirection),
                typeof(TramSignalControlLeft),
                new PropertyMetadata(TramSignalDirection.None, OnDirectionChangedLeft));

        public TramSignalDirection Left
        {
            get => (TramSignalDirection)GetValue(DirectionLeftProperty);
            set => SetValue(DirectionLeftProperty, value);
        }

        private static void OnDirectionChangedLeft(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TramSignalControlLeft ctrl)
                ctrl.UpdateArrowsLeft();
        }

        public TramSignalControlLeft()
        {
            InitializeComponent();
            UpdateArrowsLeft();
        }

        private void UpdateArrowsLeft()
        {
            const double active = 1.00;
            const double inactive = 0.30;

            switch (Left)
            {
                case TramSignalDirection.Stop:
                    LOrangeRect.Opacity = inactive;
                    LeftIndicator.Opacity = inactive;
                    LStraightIndicator.Opacity = inactive;
                    break;

                case TramSignalDirection.Left:
                    LOrangeRect.Opacity = active;
                    LeftIndicator.Opacity = active;
                    LStraightIndicator.Opacity = inactive;
                    break;

                case TramSignalDirection.Straight:
                    LOrangeRect.Opacity = active;
                    LeftIndicator.Opacity = inactive;
                    LStraightIndicator.Opacity = active;
                    break;

                default: // None
                    LOrangeRect.Opacity = inactive;
                    LeftIndicator.Opacity = inactive;
                    LStraightIndicator.Opacity = inactive;
                    break;
            }
        }
    }
}