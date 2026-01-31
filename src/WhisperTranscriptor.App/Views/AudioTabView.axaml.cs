using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.IO;
using System.Linq;
using WhisperTranscriptor.App.ViewModels;

namespace WhisperTranscriptor.App.Views;

public partial class AudioTabView : UserControl
{
    public AudioTabView()
    {
        InitializeComponent();
    }

    private async void SelectAudio_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = DataContext as AudioTabViewModel;
        if (vm is null)
            return;

        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите аудиофайл",
            AllowMultiple = false
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return;

        vm.SetAudioPath(file.Path.LocalPath);
    }

    private async void SelectOutput_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var vm = DataContext as AudioTabViewModel;
        if (vm is null)
            return;

        var suggestedName = "transcription.txt";
        if (!string.IsNullOrWhiteSpace(vm.AudioPath))
            suggestedName = Path.GetFileName(Path.ChangeExtension(vm.AudioPath, ".txt"));

        var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Куда сохранить txt",
            SuggestedFileName = suggestedName,
            DefaultExtension = "txt"
        });

        if (file is null)
            return;

        vm.SetOutputTextPath(file.Path.LocalPath);
    }
}

