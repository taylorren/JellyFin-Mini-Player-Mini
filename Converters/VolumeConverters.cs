using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RoundSoundMimic
{
    public class VolumeWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 &&
                values[0] is double volume &&
                values[1] is double trackWidth &&
                trackWidth > 0)
            {
                // volume is 0-100, we want to return width based on percentage
                var percentage = volume / 100.0;
                return trackWidth * percentage;
            }
            if (values.Length >= 1 && values[0] is double singleVolume)
            {
                // Fallback: assume track width of 100
                var percentage = singleVolume / 100.0;
                return 100.0 * percentage;
            }
            return 0.0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class VolumeToIconConverter : IMultiValueConverter
    {
        private static readonly Geometry HighIcon = Geometry.Parse("M3,9 L7,9 L12,4 L12,20 L7,15 L3,15 Z M14,8 C16,9 17,11 17,12 C17,13 16,15 14,16 M16,5 C19,7 21,10 21,12 C21,14 19,17 16,19");
        private static readonly Geometry LowIcon = Geometry.Parse("M3,9 L7,9 L12,4 L12,20 L7,15 L3,15 Z M14,8 C16,9 17,11 17,12 C17,13 16,15 14,16");
        private static readonly Geometry MuteIcon = Geometry.Parse("M3,9 L7,9 L12,4 L12,20 L7,15 L3,15 Z M14,9 L20,15 M14,15 L20,9");

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is int volume && values[1] is bool isMuted)
            {
                if (isMuted || volume == 0)
                {
                    return MuteIcon;
                }
                else if (volume < 50)
                {
                    return LowIcon;
                }
                else
                {
                    return HighIcon;
                }
            }
            // Fallback to volume only
            if (values.Length >= 1 && values[0] is int vol)
            {
                if (vol == 0)
                {
                    return MuteIcon;
                }
                else if (vol < 50)
                {
                    return LowIcon;
                }
                else
                {
                    return HighIcon;
                }
            }
            return HighIcon;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}