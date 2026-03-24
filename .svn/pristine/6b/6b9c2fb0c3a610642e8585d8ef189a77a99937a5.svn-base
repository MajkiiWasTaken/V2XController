using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;


namespace V2XController
{
    internal class MapRectangle
    {
        public Rectangle Shape { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool ContainsSomething { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 20;
        public double Height { get; set; } = 20;
        public Brush Fill { get; set; } = Brushes.Red;
        public string Tooltip { get; set; } = "";
        public string LastTramId { get; set; } = "-";

        /////////////////////////////////////////////////
        public void AddToCanvas(Canvas canvas)
        {
            var rect = new Rectangle
            {
                Width = Width,
                Height = Height,
                Fill = Fill,
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                ToolTip = Tooltip
                
            };

            Canvas.SetLeft(rect, X);
            Canvas.SetTop(rect, Y);

          
            rect.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
            };

            canvas.Children.Add(rect);
        }

        public MapRectangle(Rectangle rectangle)
        {
            Shape = rectangle;
            Name = "New object";
            Description = "";
            ContainsSomething = false;
        }
    }
}
