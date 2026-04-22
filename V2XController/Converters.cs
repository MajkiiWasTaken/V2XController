using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

/**********************************************************************************************************
 * V2X Controller - Converters.cs
 * Author: Michal Švrček
 * Version: 2.1.2
 * Description: Contains value converters for the V2X Controller application, used for data binding in the UI.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/

namespace V2XController
{
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}