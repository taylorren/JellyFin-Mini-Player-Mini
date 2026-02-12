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

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string format && !string.IsNullOrEmpty(format))
            {
                if (LosslessFormats.Contains(format) || format.Contains("PCM", StringComparison.OrdinalIgnoreCase))
                {
                    // Shiny color for lossless
                    return new SolidColorBrush(Color.FromRgb(255, 215, 0)); // Gold color
                }
            }
            // Default muted color
            return new SolidColorBrush(Color.FromRgb(122, 127, 138)); // MutedTextBrush color
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}