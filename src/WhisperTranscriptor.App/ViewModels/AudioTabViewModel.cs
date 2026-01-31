using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Whisper.net;
using Whisper.net.Ggml;

namespace WhisperTranscriptor.App.ViewModels;

public sealed partial class AudioTabViewModel : ViewModelBase, IDisposable
{
    private const QuantizationType DefaultQuantization = QuantizationType.Q5_0;

    private CancellationTokenSource? _runCts;
    private WhisperFactory? _factory;
    private string? _factoryModelPath;

    public AudioTabViewModel()
    {
        Models = Enum.GetValues<GgmlType>()
            .OrderBy(x => x.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(x => new ModelOption(x))
            .ToList();

        if (Models.Any(m => m.Type == GgmlType.Tiny))
            SelectedModel = Models.First(m => m.Type == GgmlType.Tiny);
        else
            SelectedModel = Models[0];

        Language = "en";
        RemoveSilence = true;
        Status = "Готово.";
        Log = "";
    }

    public IReadOnlyList<ModelOption> Models { get; }

    [ObservableProperty]
    private ModelOption selectedModel;

    [ObservableProperty]
    private string? audioPath;

    [ObservableProperty]
    private string? outputTextPath;

    [ObservableProperty]
    private string language;

    [ObservableProperty]
    private string status;

    [ObservableProperty]
    private string log;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? lastResultPreview;

    [ObservableProperty]
    private bool removeSilence;

    public string ModelsDirectory => Path.Combine(GetAppDataDirectory(), "models");

    public string SelectedModelPath =>
        Path.Combine(ModelsDirectory, $"ggml-{SelectedModel.Type.ToString().ToLowerInvariant()}-{DefaultQuantization.ToString().ToLowerInvariant()}.bin");

    public void SetAudioPath(string? path)
    {
        AudioPath = string.IsNullOrWhiteSpace(path) ? null : path;

        if (!string.IsNullOrWhiteSpace(AudioPath) && string.IsNullOrWhiteSpace(OutputTextPath))
        {
            OutputTextPath = Path.ChangeExtension(AudioPath, ".txt");
        }

        TranscribeCommand.NotifyCanExecuteChanged();
    }

    public void SetOutputTextPath(string? path)
    {
        OutputTextPath = string.IsNullOrWhiteSpace(path) ? null : path;
    }

    partial void OnSelectedModelChanged(ModelOption value)
    {
        // При смене модели сбрасываем factory, чтобы пересоздать под новый файл.
        if (_factory is not null)
        {
            _factory.Dispose();
            _factory = null;
            _factoryModelPath = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartWork))]
    private async Task DownloadModelAsync()
    {
        await RunExclusiveAsync(ct => DownloadModelInternalAsync(ct, reportStatus: true));
    }

    [RelayCommand(CanExecute = nameof(CanTranscribe))]
    private async Task TranscribeAsync()
    {
        await RunExclusiveAsync(async ct =>
        {
            if (string.IsNullOrWhiteSpace(AudioPath))
                throw new InvalidOperationException("Не выбран аудиофайл.");

            var outputPath = string.IsNullOrWhiteSpace(OutputTextPath)
                ? Path.ChangeExtension(AudioPath, ".txt")
                : OutputTextPath!;

            Status = "Подготовка модели...";
            await EnsureModelAsync(ct);

            Status = "Конвертация аудио (ffmpeg)...";
            var wavPath = await ConvertToWav16kMonoAsync(AudioPath, ct);

            try
            {
                Status = "Транскрибация...";
                var text = await TranscribeWavAsync(wavPath, ct);

                Status = "Сохранение результата...";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
                await File.WriteAllTextAsync(outputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);

                LastResultPreview = text.Length <= 4000 ? text : text[..4000] + Environment.NewLine + "...";
                AppendLog($"[OK] Сохранено: {outputPath}");
                Status = "Готово.";
            }
            finally
            {
                try
                {
                    if (File.Exists(wavPath))
                        File.Delete(wavPath);
                }
                catch
                {
                    // ignore
                }
            }
        });
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _runCts?.Cancel();
    }

    private bool CanStartWork() => !IsBusy;

    private bool CanTranscribe() => !IsBusy && !string.IsNullOrWhiteSpace(AudioPath);

    private async Task RunExclusiveAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        DownloadModelCommand.NotifyCanExecuteChanged();
        TranscribeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();

        try
        {
            await action(_runCts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendLog("[CANCEL] Операция отменена.");
            Status = "Отменено.";
        }
        catch (Exception ex)
        {
            AppendLog("[ERROR] " + ex);
            Status = "Ошибка: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            DownloadModelCommand.NotifyCanExecuteChanged();
            TranscribeCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task EnsureModelAsync(CancellationToken ct)
    {
        if (File.Exists(SelectedModelPath))
            return;

        await DownloadModelInternalAsync(ct, reportStatus: false);
    }

    private async Task DownloadModelInternalAsync(CancellationToken ct, bool reportStatus)
    {
        var modelPath = SelectedModelPath;
        AppendLog($"[MODEL] Проверяем модель: {modelPath}");

        if (File.Exists(modelPath))
        {
            AppendLog("[MODEL] Уже скачана.");
            if (reportStatus)
                Status = "Модель уже скачана.";
            return;
        }

        Directory.CreateDirectory(ModelsDirectory);

        AppendLog($"[MODEL] Скачиваем: {SelectedModel.Type} ({DefaultQuantization})...");
        using var http = new HttpClient();
        var downloader = new WhisperGgmlDownloader(http);

        await using var modelStream = await downloader.GetGgmlModelAsync(SelectedModel.Type, DefaultQuantization, ct);
        await using var fs = File.OpenWrite(modelPath);
        await modelStream.CopyToAsync(fs, ct);

        AppendLog("[MODEL] Готово.");
        if (reportStatus)
            Status = "Модель скачана.";
    }

    private async Task<string> TranscribeWavAsync(string wavPath, CancellationToken ct)
    {
        var lang = string.IsNullOrWhiteSpace(Language) ? "auto" : Language.Trim();

        if (_factory is null || !string.Equals(_factoryModelPath, SelectedModelPath, StringComparison.OrdinalIgnoreCase))
        {
            _factory?.Dispose();
            _factory = WhisperFactory.FromPath(SelectedModelPath);
            _factoryModelPath = SelectedModelPath;
        }

        var builder = _factory.CreateBuilder();
        builder = builder.WithLanguage(lang);

        using var processor = builder.Build();
        await using var fs = File.OpenRead(wavPath);

        var sb = new StringBuilder();
        string? lastNorm = null;
        var sameInRow = 0;
        await foreach (var segment in processor.ProcessAsync(fs, ct))
        {
            var text = segment.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var norm = NormalizeForLoopGuard(text);
            if (lastNorm is not null && string.Equals(norm, lastNorm, StringComparison.Ordinal))
                sameInRow++;
            else
                sameInRow = 0;

            lastNorm = norm;

            if (sameInRow >= 8 && norm.Length >= 8)
            {
                AppendLog("[WHISPER] Похоже на зацикливание (повтор сегмента). Остановлено.");
                break;
            }

            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(text);
        }

        return sb.ToString().Trim();
    }

    private async Task<string> ConvertToWav16kMonoAsync(string inputPath, CancellationToken ct)
    {
        var tempWav = Path.Combine(Path.GetTempPath(), $"whisper_transcriptor_{Guid.NewGuid():N}.wav");

        var args = BuildFfmpegArgs(inputPath, tempWav, removeSilence: RemoveSilence);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        AppendLog("[FFMPEG] " + psi.FileName + " " + psi.Arguments);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new Exception("Не удалось запустить ffmpeg (проверь PATH).");

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        await process.WaitForExitAsync(ct);

        var stderr = await stderrTask;
        var stdout = await stdoutTask;

        if (!string.IsNullOrWhiteSpace(stdout))
            AppendLog("[FFMPEG][stdout]\n" + stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            AppendLog("[FFMPEG][stderr]\n" + stderr.Trim());

        if (process.ExitCode != 0)
        {
            if (RemoveSilence)
            {
                AppendLog("[FFMPEG] Конвертация с удалением тишины не удалась. Повторяем без фильтра...");
                return await ConvertToWav16kMonoWithoutSilenceAsync(inputPath, ct);
            }

            throw new Exception($"ffmpeg завершился с кодом {process.ExitCode}. См. лог выше.");
        }

        if (!File.Exists(tempWav))
            throw new Exception("ffmpeg не создал выходной WAV-файл.");

        var fileInfo = new FileInfo(tempWav);
        if (fileInfo.Length < 1024)
            throw new Exception("Сконвертированный WAV слишком маленький (возможно, нет аудиодорожки или получилась тишина).");

        return tempWav;
    }

    private async Task<string> ConvertToWav16kMonoWithoutSilenceAsync(string inputPath, CancellationToken ct)
    {
        var tempWav = Path.Combine(Path.GetTempPath(), $"whisper_transcriptor_{Guid.NewGuid():N}.wav");
        var args = BuildFfmpegArgs(inputPath, tempWav, removeSilence: false);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        AppendLog("[FFMPEG] " + psi.FileName + " " + psi.Arguments);

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new Exception("Не удалось запустить ffmpeg (проверь PATH).");

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();

        await process.WaitForExitAsync(ct);

        var stderr = await stderrTask;
        var stdout = await stdoutTask;

        if (!string.IsNullOrWhiteSpace(stdout))
            AppendLog("[FFMPEG][stdout]\n" + stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr))
            AppendLog("[FFMPEG][stderr]\n" + stderr.Trim());

        if (process.ExitCode != 0)
            throw new Exception($"ffmpeg завершился с кодом {process.ExitCode}. См. лог выше.");

        if (!File.Exists(tempWav))
            throw new Exception("ffmpeg не создал выходной WAV-файл.");

        return tempWav;
    }

    private static string BuildFfmpegArgs(string inputPath, string outputWavPath, bool removeSilence)
    {
        var baseArgs = $"-y -i \"{inputPath}\" -ac 1 -ar 16000 -c:a pcm_s16le";
        if (!removeSilence)
            return $"{baseArgs} \"{outputWavPath}\"";

        var filter =
            "silenceremove=start_periods=1:start_duration=0.25:start_threshold=-50dB:" +
            "stop_periods=-1:stop_duration=0.50:stop_threshold=-50dB";

        return $"{baseArgs} -af \"{filter}\" \"{outputWavPath}\"";
    }

    private static string NormalizeForLoopGuard(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        var sb = new StringBuilder(t.Length);
        var prevWs = false;
        foreach (var ch in t)
        {
            var isWs = char.IsWhiteSpace(ch);
            if (isWs)
            {
                if (!prevWs)
                    sb.Append(' ');
                prevWs = true;
                continue;
            }

            prevWs = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string GetAppDataDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = AppContext.BaseDirectory;

        return Path.Combine(baseDir, "WhisperTranscriptor");
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Log = string.IsNullOrWhiteSpace(Log) ? line : Log + Environment.NewLine + line;
    }

    public void Dispose()
    {
        _runCts?.Dispose();
        _factory?.Dispose();
    }
}

public readonly record struct ModelOption(GgmlType Type)
{
    public override string ToString() => Type.ToString();
}

