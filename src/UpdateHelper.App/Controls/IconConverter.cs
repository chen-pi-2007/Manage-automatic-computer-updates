using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UpdateHelper.Presentation;

namespace UpdateHelper.App.Controls;

/// <summary>
/// 把 IconSource（文件 + 第几个图标）变成界面上的图片。取不到时用 Windows 的通用程序图标。
/// 结果按"文件 + 序号"缓存；表格是虚拟化的，只有滚到的行才会取图标。
/// </summary>
public sealed class IconConverter : IValueConverter
{
    private const int Size = 32;
    private static readonly ConcurrentDictionary<IconSource, ImageSource?> Cache = new();
    private static readonly Lazy<ImageSource> Generic = new(() => ToImage(SystemIcons.Application));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is IconSource source ? Cache.GetOrAdd(source, Load) : null) ?? Generic.Value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static ImageSource? Load(IconSource source)
    {
        try
        {
            // 网络路径可能卡很久，不去取；文件不存在（软件被删了一半）也直接用通用图标
            if (source.Path.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(source.Path)) return null;
            using var icon = Icon.ExtractIcon(source.Path, source.Index, Size);
            return icon is null ? null : ToImage(icon);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static ImageSource ToImage(Icon icon)
    {
        var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(Size, Size));
        image.Freeze();
        return image;
    }
}
