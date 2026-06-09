using System.Windows;
using System.Windows.Controls;

namespace V2XController // prvni cislo v seznamu je vpravo druhe vlevo
{
    public partial class TramSignalControlRight: UserControl
    {
        public static readonly DependencyProperty DirectionRightProperty =
            DependencyProperty.Register(
                nameof(Right),
                typeof(TramSignalDirection),
                typeof(TramSignalControlRight),
                new PropertyMetadata(TramSignalDirection.None, OnDirectionChangedRight));

        public TramSignalDirection Right
        {
            get => (TramSignalDirection)GetValue(DirectionRightProperty);
            set => SetValue(DirectionRightProperty, value);
        }

        private static void OnDirectionChangedRight(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TramSignalControlRight ctrl)
                ctrl.UpdateArrowsRight();
        }

        public TramSignalControlRight()
        {
            InitializeComponent();
            UpdateArrowsRight();
        }

        private void UpdateArrowsRight()
        {
            const double active = 1.00;
            const double inactive = 0.30;

            switch (Right)
            {
                case TramSignalDirection.Stop:
                    ROrangeRect.Opacity = inactive;
                    RightIndicator.Opacity = inactive;
                    RStraightIndicator.Opacity = inactive;
                    break;

                case TramSignalDirection.Right:
                    ROrangeRect.Opacity = active;
                    RightIndicator.Opacity = active;
                    RStraightIndicator.Opacity = inactive;
                    break;

                case TramSignalDirection.Straight:
                    ROrangeRect.Opacity = active;
                    RightIndicator.Opacity = inactive;
                    RStraightIndicator.Opacity = active;
                    break;

                default: // None
                    ROrangeRect.Opacity = inactive;
                    RightIndicator.Opacity = inactive;
                    RStraightIndicator.Opacity = inactive;
                    break;
            }
        }
    }
}