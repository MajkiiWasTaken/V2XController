using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace V2XController
{
    public partial class TerminalWindow : Window
    {
        private const int MaxBlocks = 2000;

        public TerminalWindow()
        {
            InitializeComponent();
        }

        public void Append(string text, Brush color)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => Append(text, color));
                return;
            }

            var p = new Paragraph(new Run(text)) { Foreground = color, Margin = new Thickness(0) };
            TerminalBox.Document.Blocks.Add(p);

            // trim oldest
            while (TerminalBox.Document.Blocks.Count > MaxBlocks)
                TerminalBox.Document.Blocks.Remove(TerminalBox.Document.Blocks.FirstBlock);

            TerminalBox.ScrollToEnd();
        }
    }
}