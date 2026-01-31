using System;

namespace WhisperTranscriptor.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IDisposable
{
    public MainWindowViewModel()
    {
        Audio = new AudioTabViewModel();
        Video = new VideoTabViewModel();
    }

    public AudioTabViewModel Audio { get; }
    public VideoTabViewModel Video { get; }

    public void Dispose()
    {
        Audio.Dispose();
        Video.Dispose();
    }
}
