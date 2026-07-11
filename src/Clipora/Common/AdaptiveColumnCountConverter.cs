using System.Globalization;
using System.Windows.Data;

namespace Clipora.Common;

/// <summary>
/// 根据可用宽度在 1–3 列间切换，供窄面板中的等宽自适应选项使用。
/// </summary>
public sealed class AdaptiveColumnCountConverter : IValueConverter
{
    public double TwoColumnMinimumWidth { get; set; } = 230;

    public double ThreeColumnMinimumWidth { get; set; } = 350;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double width = value is double actualWidth ? actualWidth : 0;
        if (width >= ThreeColumnMinimumWidth)
            return 3;
        return width >= TwoColumnMinimumWidth ? 2 : 1;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
