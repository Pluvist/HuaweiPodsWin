using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HuaweiPods;

// WinUI port of the icon slot on Yuandl.ThemeUI's PageViewItem (MIT).
public sealed class SidebarMenuItem : ListViewItem
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(object), typeof(SidebarMenuItem), new PropertyMetadata(null));

    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
