using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RoundSoundMimic
{
    public class FormatToBrushConverter : IValueConverter
    {
        private static readonly HashSet<string> LosslessFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FLAC", "WAV", "ALAC", "APE", "WV", "TAK", "TTA", "AIFF", "DSD", "DSF", "DFF"
        };

        private static readonly SolidColorBrush LosslessBrush = CreateFrozen(Color.FromRgb(255, 215, 0));
        private static readonly SolidColorBrush LossyBrush = CreateFrozen(Color.FromRgb(122, 127, 138));

        private static SolidColorBrush CreateFrozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string format && !string.IsNullOrEmpty(format))
            {
                if (LosslessFormats.Contains(format) || format.Contains("PCM", StringComparison.OrdinalIgnoreCase))
                {
                    return LosslessBrush;
                }
            }

            return LossyBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
