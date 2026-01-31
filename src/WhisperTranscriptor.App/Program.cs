using Avalonia;
using System;
using LibVLCSharp.Shared;

namespace WhisperTranscriptor.App;

public sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Инициализация LibVLC для VideoView (Windows/macOS — через NuGet VideoLAN.LibVLC.*,
        // Linux — обычно через системный libvlc).
        Core.Initialize();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
