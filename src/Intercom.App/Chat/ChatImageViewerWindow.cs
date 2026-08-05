using Intercom.Chat;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Intercom.App.Chat;

public sealed class ChatImageViewerWindow : Window
{
    readonly RectInt32 _workArea;

    public ChatImageViewerWindow(OptimizedChatImage image, RectInt32 workArea)
    {
        _workArea = workArea;
        Title = "Shared image";
        var control = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => { args.Handled = true; Close(); };
        control.KeyboardAccelerators.Add(escape);
        Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Padding = new Thickness(12), Children = { control } };
        Activated += async (_, _) =>
        {
            if (control.Source is not null) return;
            control.Source = await CreateBitmapAsync(image.Bytes);
            SizeToDisplay(image.Width, image.Height);
        };
    }

    void SizeToDisplay(int imageWidth, int imageHeight)
    {
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
        var work = _workArea;
        const int chrome = 48;
        var scale = Math.Min(1d, Math.Min((work.Width - 32d) / imageWidth, (work.Height - chrome - 32d) / imageHeight));
        var width = Math.Max(240, (int)Math.Ceiling(imageWidth * scale) + 24);
        var height = Math.Max(180, (int)Math.Ceiling(imageHeight * scale) + chrome);
        appWindow.Resize(new SizeInt32(width, height));
        appWindow.Move(new PointInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2));
    }

    static async Task<BitmapImage> CreateBitmapAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream)) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        bitmap.SetSource(stream);
        return bitmap;
    }
}
