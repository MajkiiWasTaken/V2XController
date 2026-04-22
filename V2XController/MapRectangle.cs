using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

/**********************************************************************************************************
 * V2X Controller - MapRectangle.cs
 * Author: Michal Švrček
 * Version: 1.0.7
 * Description: Represents a map rectangle in the V2X Controller application, containing shape, position, 
 *              and visual properties.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/

namespace V2XController
{
    internal class MapRectangle
    {
        public Shape Shape { get; set; }
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

        public MapRectangle(Shape shape)
        {
            Shape = shape;
            Name = "New object";
            Description = "";
            ContainsSomething = false;
        }
    }
}
