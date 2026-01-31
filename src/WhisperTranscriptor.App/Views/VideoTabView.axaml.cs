using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Linq;
using WhisperTranscriptor.App.ViewModels;

namespace WhisperTranscriptor.App.Views;

public partial class VideoTabView : UserControl
{
    private bool _isUserSeeking;

    public VideoTabView()
    {
        InitializeComponent();
    }

    private async void SelectVideo_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as VideoTabViewModel;
        if (vm is null)
            return;

        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите видеофайл",
            AllowMultiple = false
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        vm.SetVideoPath(file.Path.LocalPath);
    }

    private async void LoadSubs_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as VideoTabViewModel;
        if (vm is null)
            return;

        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Загрузить субтитры (.srt/.vtt)",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Subtitles") { Patterns = ["*.srt", "*.vtt"] }
            ]
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        await vm.LoadSubtitlesFromFileCommand.ExecuteAsync(file.Path.LocalPath);
    }

    private void Timeline_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_isUserSeeking)
            return;

        var vm = DataContext as VideoTabViewModel;
        if (vm is null)
            return;

        vm.SeekToSeconds(e.NewValue);
    }

    private void Timeline_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        _isUserSeeking = true;
        (DataContext as VideoTabViewModel)?.BeginSeek();
    }

    private void Timeline_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        _isUserSeeking = false;
        (DataContext as VideoTabViewModel)?.EndSeek();
    }

    private void SeekToSelected_Click(object? sender, RoutedEventArgs e)
    {
        var vm = DataContext as VideoTabViewModel;
        if (vm?.SelectedSegment is null)
            return;

        vm.SeekToSeconds(vm.SelectedSegment.Start.TotalSeconds);
    }
}

