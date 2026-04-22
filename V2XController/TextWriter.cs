using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;

/**********************************************************************************************************
 * V2X Controller - TextWriter.cs
 * Author: Michal Švrček
 * Version: 1.0.0
 * Description: Provides a custom TextWriter implementation for redirecting console output to a WPF TextBox.
 *              Handles thread-safe updates to the TextBox using the Dispatcher.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/

namespace V2XController
{
    public class TextBoxWriter : TextWriter
    {
        private readonly TextBox _textBox;
        private readonly Dispatcher _dispatcher;

        public TextBoxWriter(TextBox textBox)
        {
            _textBox = textBox;
            _dispatcher = textBox.Dispatcher;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            _dispatcher.Invoke(() => _textBox.AppendText(value.ToString()));
        }

        public override void Write(string value)
        {
            _dispatcher.Invoke(() => _textBox.AppendText(value));
        }

        public override void WriteLine(string value)
        {
            _dispatcher.Invoke(() =>
            {
                _textBox.AppendText(value + Environment.NewLine);
                _textBox.ScrollToEnd();
            });
        }
    }
}
