using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Whisper.net;
using Whisper.net.Ggml;

namespace WhisperTranscriptor.App.ViewModels;

public sealed partial class VideoTabViewModel : ViewModelBase, IDisposable
{
    private const QuantizationType DefaultQuantization = QuantizationType.Q5_0;

    private readonly LibVLC _libVlc;
    private Media? _currentMedia;
    private bool _isUserSeeking;

    private CancellationTokenSource? _runCts;
    private WhisperFactory? _factory;
    private string? _factoryModelPath;

    public VideoTabViewModel()
    {
        // Video player
        _libVlc = new LibVLC();
        Player = new MediaPlayer(_libVlc);
        Player.LengthChanged += (_, e) =>
        {
            // Событие приходит не с UI-потока
            Dispatcher.UIThread.Post(() =>
            {
                if (e.Length > 0)
                    DurationSeconds = e.Length / 1000.0;
            });
        };
        Player.TimeChanged += (_, e) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isUserSeeking)
                    return;

                if (e.Time >= 0)
                    PositionSeconds = e.Time / 1000.0;

                UpdateActiveSubtitle();
            });
        };

        // Whisper model picker
        Models = Enum.GetValues<GgmlType>()
            .OrderBy(x => x.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(x => new ModelOption(x))
            .ToList();

        // Дефолт: medium.en (если есть), иначе medium, иначе base.
        SelectedModel = PickDefaultModel(Models);
        Language = "en";

        // ВАЖНО: для видео тайминги должны совпадать, поэтому по умолчанию тишину НЕ вырезаем.
        RemoveSilence = false;

        Status = "Готово.";
        Log = "";
        Segments = new ObservableCollection<SubtitleSegmentVm>();
    }

    public IReadOnlyList<ModelOption> Models { get; }

    [ObservableProperty]
    private ModelOption selectedModel;

    [ObservableProperty]
    private string language;

    [ObservableProperty]
    private bool removeSilence;

    [ObservableProperty]
    private string? videoPath;

    [ObservableProperty]
    private string status;

    [ObservableProperty]
    private string log;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private double durationSeconds;

    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private string? activeSubtitleText;

    [ObservableProperty]
    private SubtitleSegmentVm? selectedSegment;

    public MediaPlayer Player { get; }

    public ObservableCollection<SubtitleSegmentVm> Segments { get; }

    public string ModelsDirectory => Path.Combine(GetAppDataDirectory(), "models");

    public string SelectedModelPath =>
        Path.Combine(ModelsDirectory, $"ggml-{SelectedModel.Type.ToString().ToLowerInvariant()}-{DefaultQuantization.ToString().ToLowerInvariant()}.bin");

    public string SubsDirectory => Path.Combine(AppContext.BaseDirectory, "subs");

    public void SetVideoPath(string? path)
    {
        VideoPath = string.IsNullOrWhiteSpace(path) ? null : path;

        if (string.IsNullOrWhiteSpace(VideoPath))
            return;

        try
        {
            _currentMedia?.Dispose();
            _currentMedia = new Media(_libVlc, new Uri(VideoPath));
            Player.Media = _currentMedia;
            // Не автоплей: пользователю нужны управление и предсказуемость.
            // Чтобы появилась длительность/seek, запускаем и сразу паузим.
            Player.Play();
            Player.Pause();
            Status = "Видео загружено.";
            AppendLog($"[VIDEO] {VideoPath}");

            GenerateSubtitlesCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            AppendLog("[VIDEO][ERROR] " + ex);
            Status = "Ошибка загрузки видео: " + ex.Message;
        }
    }

    public void BeginSeek()
    {
        _isUserSeeking = true;
    }

    public void EndSeek()
    {
        _isUserSeeking = false;
        SeekToSeconds(PositionSeconds);
    }

    public void SeekToSeconds(double seconds)
    {
        if (seconds < 0) seconds = 0;
        Player.Time = (long)(seconds * 1000);
        PositionSeconds = seconds;
        UpdateActiveSubtitle();
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (Player.IsPlaying)
            Player.Pause();
        else
            Player.Play();
    }

    [RelayCommand(CanExecute = nameof(CanStartWork))]
    private async Task DownloadModelAsync()
    {
        await RunExclusiveAsync(ct => DownloadModelInternalAsync(ct, reportStatus: true));
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateSubtitlesAsync()
    {
        await RunExclusiveAsync(async ct =>
        {
            if (string.IsNullOrWhiteSpace(VideoPath))
                throw new InvalidOperationException("Не выбран видеофайл.");

            Status = "Подготовка модели...";
            await EnsureModelAsync(ct);

            Status = "Извлечение аудио (ffmpeg)...";
            var wavPath = await ExtractWavFromVideoAsync(VideoPath, ct);

            try
            {
                Status = "Whisper → сегменты...";
                var segments = await TranscribeToSegmentsAsync(wavPath, ct);

                Segments.Clear();
                foreach (var s in segments)
                    Segments.Add(s);

                UpdateActiveSubtitle();

                Status = "Сохранение субтитров...";
                Directory.CreateDirectory(SubsDirectory);
                var baseName = Path.GetFileNameWithoutExtension(VideoPath);
                var srtPath = Path.Combine(SubsDirectory, baseName + ".srt");
                var vttPath = Path.Combine(SubsDirectory, baseName + ".vtt");

                await File.WriteAllTextAsync(srtPath, WriteSrt(Segments), new UTF8Encoding(false), ct);
                await File.WriteAllTextAsync(vttPath, WriteVtt(Segments), new UTF8Encoding(false), ct);

                AppendLog($"[SUBS] Сохранено: {srtPath}");
                AppendLog($"[SUBS] Сохранено: {vttPath}");
                Status = "Готово.";
            }
            finally
            {
                try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanLoadSubs))]
    private async Task LoadSubtitlesFromFileAsync(string path)
    {
        await RunExclusiveAsync(async ct =>
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Пустой путь к субтитрам.");

            Status = "Загрузка субтитров...";
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var text = await File.ReadAllTextAsync(path, ct);

            List<SubtitleSegmentVm> parsed = ext switch
            {
                ".srt" => ParseSrt(text),
                ".vtt" => ParseVtt(text),
                _ => throw new Exception("Поддерживаются только .srt и .vtt")
            };

            Segments.Clear();
            foreach (var s in parsed)
                Segments.Add(s);

            AppendLog($"[SUBS] Загружено: {path}");
            Status = "Готово.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanSaveSubs))]
    private async Task SaveSubtitlesAsync()
    {
        await RunExclusiveAsync(async ct =>
        {
            Directory.CreateDirectory(SubsDirectory);

            var baseName = !string.IsNullOrWhiteSpace(VideoPath)
                ? Path.GetFileNameWithoutExtension(VideoPath)
                : "subtitles";

            var srtPath = Path.Combine(SubsDirectory, baseName + ".srt");
            var vttPath = Path.Combine(SubsDirectory, baseName + ".vtt");

            await File.WriteAllTextAsync(srtPath, WriteSrt(Segments), new UTF8Encoding(false), ct);
            await File.WriteAllTextAsync(vttPath, WriteVtt(Segments), new UTF8Encoding(false), ct);

            AppendLog($"[SUBS] Сохранено: {srtPath}");
            AppendLog($"[SUBS] Сохранено: {vttPath}");
            Status = "Готово.";
        });
    }

    [RelayCommand(CanExecute = nameof(CanNudgeSelected))]
    private void NudgeSelectedMinus100ms() => NudgeSelected(TimeSpan.FromMilliseconds(-100));

    [RelayCommand(CanExecute = nameof(CanNudgeSelected))]
    private void NudgeSelectedPlus100ms() => NudgeSelected(TimeSpan.FromMilliseconds(100));

    [RelayCommand(CanExecute = nameof(CanNudgeSelected))]
    private void NudgeSelectedMinus500ms() => NudgeSelected(TimeSpan.FromMilliseconds(-500));

    [RelayCommand(CanExecute = nameof(CanNudgeSelected))]
    private void NudgeSelectedPlus500ms() => NudgeSelected(TimeSpan.FromMilliseconds(500));

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        _runCts?.Cancel();
    }

    private void NudgeSelected(TimeSpan delta)
    {
        if (SelectedSegment is null)
            return;

        SelectedSegment.Start = Clamp(SelectedSegment.Start + delta);
        SelectedSegment.End = Clamp(SelectedSegment.End + delta);
        SelectedSegment.SyncTextFieldsFromTime();
        UpdateActiveSubtitle();
    }

    private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    private bool CanStartWork() => !IsBusy;
    private bool CanGenerate() => !IsBusy && !string.IsNullOrWhiteSpace(VideoPath);
    private bool CanLoadSubs() => !IsBusy;
    private bool CanSaveSubs() => !IsBusy && Segments.Count > 0;
    private bool CanNudgeSelected() => !IsBusy && SelectedSegment is not null;

    private async Task RunExclusiveAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        GenerateSubtitlesCommand.NotifyCanExecuteChanged();
        DownloadModelCommand.NotifyCanExecuteChanged();
        SaveSubtitlesCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        NudgeSelectedMinus100msCommand.NotifyCanExecuteChanged();
        NudgeSelectedPlus100msCommand.NotifyCanExecuteChanged();
        NudgeSelectedMinus500msCommand.NotifyCanExecuteChanged();
        NudgeSelectedPlus500msCommand.NotifyCanExecuteChanged();

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
            GenerateSubtitlesCommand.NotifyCanExecuteChanged();
            DownloadModelCommand.NotifyCanExecuteChanged();
            SaveSubtitlesCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            NudgeSelectedMinus100msCommand.NotifyCanExecuteChanged();
            NudgeSelectedPlus100msCommand.NotifyCanExecuteChanged();
            NudgeSelectedMinus500msCommand.NotifyCanExecuteChanged();
            NudgeSelectedPlus500msCommand.NotifyCanExecuteChanged();
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

    private async Task<string> ExtractWavFromVideoAsync(string videoPath, CancellationToken ct)
    {
        var tempWav = Path.Combine(Path.GetTempPath(), $"whisper_transcriptor_video_{Guid.NewGuid():N}.wav");

        // ВАЖНО: -vn, потому что вход — видео. Тайминги должны совпадать с видео, поэтому тишину не режем по умолчанию.
        var baseArgs = $"-y -i \"{videoPath}\" -vn -ac 1 -ar 16000 -c:a pcm_s16le";
        var args = RemoveSilence
            ? $"{baseArgs} -af \"silenceremove=start_periods=1:start_duration=0.25:start_threshold=-50dB:stop_periods=-1:stop_duration=0.50:stop_threshold=-50dB\" \"{tempWav}\""
            : $"{baseArgs} \"{tempWav}\"";

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
                AppendLog("[FFMPEG] Извлечение с удалением тишины не удалось. Повторяем без фильтра...");
                RemoveSilence = false;
                return await ExtractWavFromVideoAsync(videoPath, ct);
            }

            throw new Exception($"ffmpeg завершился с кодом {process.ExitCode}. См. лог выше.");
        }

        if (!File.Exists(tempWav))
            throw new Exception("ffmpeg не создал выходной WAV-файл.");

        return tempWav;
    }

    private async Task<List<SubtitleSegmentVm>> TranscribeToSegmentsAsync(string wavPath, CancellationToken ct)
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

        var list = new List<SubtitleSegmentVm>();

        // Loop guard на случай мантры
        string? lastNorm = null;
        var sameInRow = 0;

        await foreach (var seg in processor.ProcessAsync(fs, ct))
        {
            var text = seg.Text?.Trim();
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

            list.Add(new SubtitleSegmentVm
            {
                Start = seg.Start,
                End = seg.End,
                Text = text
            });
        }

        // Нормализуем и сортируем
        list.Sort((a, b) => a.Start.CompareTo(b.Start));
        foreach (var s in list)
            s.SyncTextFieldsFromTime();

        return list;
    }

    private void UpdateActiveSubtitle()
    {
        if (Segments.Count == 0)
        {
            ActiveSubtitleText = null;
            return;
        }

        var t = TimeSpan.FromSeconds(PositionSeconds);

        // Линейный поиск ок для MVP; можно оптимизировать позже бинарным.
        SubtitleSegmentVm? active = null;
        foreach (var s in Segments)
        {
            if (t >= s.Start && t <= s.End)
            {
                active = s;
                break;
            }
        }

        ActiveSubtitleText = active?.Text;
    }

    private static ModelOption PickDefaultModel(IReadOnlyList<ModelOption> models)
    {
        // Пытаемся найти medium.en (обычно MediumEn), иначе Medium, иначе Base, иначе первый.
        var byName = models.FirstOrDefault(m => string.Equals(m.Type.ToString(), "MediumEn", StringComparison.OrdinalIgnoreCase));
        if (models.Any(m => string.Equals(m.Type.ToString(), "MediumEn", StringComparison.OrdinalIgnoreCase)))
            return byName;

        var medium = models.FirstOrDefault(m => string.Equals(m.Type.ToString(), "Medium", StringComparison.OrdinalIgnoreCase));
        if (models.Any(m => string.Equals(m.Type.ToString(), "Medium", StringComparison.OrdinalIgnoreCase)))
            return medium;

        var baseM = models.FirstOrDefault(m => string.Equals(m.Type.ToString(), "Base", StringComparison.OrdinalIgnoreCase));
        if (models.Any(m => string.Equals(m.Type.ToString(), "Base", StringComparison.OrdinalIgnoreCase)))
            return baseM;

        return models.Count > 0 ? models[0] : new ModelOption(GgmlType.Base);
    }

    private static string WriteSrt(IEnumerable<SubtitleSegmentVm> segments)
    {
        var sb = new StringBuilder();
        var i = 1;
        foreach (var s in segments)
        {
            sb.AppendLine(i.ToString(CultureInfo.InvariantCulture));
            sb.Append(FormatSrtTime(s.Start));
            sb.Append(" --> ");
            sb.AppendLine(FormatSrtTime(s.End));
            sb.AppendLine((s.Text ?? string.Empty).Trim());
            sb.AppendLine();
            i++;
        }
        return sb.ToString();
    }

    private static string WriteVtt(IEnumerable<SubtitleSegmentVm> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();
        foreach (var s in segments)
        {
            sb.Append(FormatVttTime(s.Start));
            sb.Append(" --> ");
            sb.AppendLine(FormatVttTime(s.End));
            sb.AppendLine((s.Text ?? string.Empty).Trim());
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FormatSrtTime(TimeSpan t)
        => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";

    private static string FormatVttTime(TimeSpan t)
        => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

    private static List<SubtitleSegmentVm> ParseSrt(string srt)
    {
        var result = new List<SubtitleSegmentVm>();
        var blocks = srt.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            var lines = block.Split('\n').Select(l => l.TrimEnd()).ToList();
            if (lines.Count < 2)
                continue;

            // lines[0] = index (optional)
            var timeLine = lines[1].Trim();
            if (!TryParseTimeRange(timeLine, "-->", ',', out var start, out var end))
                continue;

            var text = string.Join("\n", lines.Skip(2)).Trim();
            var seg = new SubtitleSegmentVm { Start = start, End = end, Text = text };
            seg.SyncTextFieldsFromTime();
            result.Add(seg);
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private static List<SubtitleSegmentVm> ParseVtt(string vtt)
    {
        var result = new List<SubtitleSegmentVm>();
        var lines = vtt.Replace("\r\n", "\n").Split('\n');
        int i = 0;

        // skip WEBVTT header
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
        if (i < lines.Length && lines[i].StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) i++;

        while (i < lines.Length)
        {
            // skip blank
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
            if (i >= lines.Length) break;

            // time line
            var timeLine = lines[i].Trim();
            if (!timeLine.Contains("-->"))
            {
                // cue id line, next is time
                i++;
                if (i >= lines.Length) break;
                timeLine = lines[i].Trim();
            }

            if (!TryParseTimeRange(timeLine, "-->", '.', out var start, out var end))
            {
                i++;
                continue;
            }

            i++;
            var textLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                textLines.Add(lines[i]);
                i++;
            }

            var text = string.Join("\n", textLines).Trim();
            var seg = new SubtitleSegmentVm { Start = start, End = end, Text = text };
            seg.SyncTextFieldsFromTime();
            result.Add(seg);
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    private static bool TryParseTimeRange(string line, string arrow, char msSep, out TimeSpan start, out TimeSpan end)
    {
        start = default;
        end = default;

        var parts = line.Split(arrow, StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        var a = parts[0].Trim();
        var b = parts[1].Trim();

        // remove VTT settings after end time (e.g. "00:00:01.000 --> 00:00:02.000 align:start")
        var bTime = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? b;

        return TryParseTimestamp(a, msSep, out start) && TryParseTimestamp(bTime, msSep, out end);
    }

    private static bool TryParseTimestamp(string s, char msSep, out TimeSpan ts)
    {
        ts = default;
        // Accept both comma and dot for convenience
        s = s.Trim().Replace(',', msSep).Replace('.', msSep);
        // HH:MM:SS{sep}mmm
        var parts = s.Split(':');
        if (parts.Length < 3)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm)) return false;

        var secParts = parts[2].Split(msSep);
        if (!int.TryParse(secParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ss)) return false;
        var ms = 0;
        if (secParts.Length > 1)
            int.TryParse(secParts[1].PadRight(3, '0')[..3], NumberStyles.Integer, CultureInfo.InvariantCulture, out ms);

        ts = new TimeSpan(0, hh, mm, ss, ms);
        return true;
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
        _currentMedia?.Dispose();
        Player.Dispose();
        _libVlc.Dispose();
    }
}

public sealed partial class SubtitleSegmentVm : ObservableObject
{
    [ObservableProperty] private TimeSpan start;
    [ObservableProperty] private TimeSpan end;
    [ObservableProperty] private string text = "";

    [ObservableProperty] private string startText = "00:00:00.000";
    [ObservableProperty] private string endText = "00:00:00.000";

    partial void OnStartTextChanged(string value)
    {
        if (TryParse(value, out var ts))
            Start = ts;
    }

    partial void OnEndTextChanged(string value)
    {
        if (TryParse(value, out var ts))
            End = ts;
    }

    partial void OnStartChanged(TimeSpan value) => StartText = Format(value);
    partial void OnEndChanged(TimeSpan value) => EndText = Format(value);

    public void SyncTextFieldsFromTime()
    {
        StartText = Format(Start);
        EndText = Format(End);
    }

    private static string Format(TimeSpan t)
        => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";

    private static bool TryParse(string s, out TimeSpan ts)
    {
        ts = default;
        s = s.Trim();
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Replace(',', '.');
        if (!TimeSpan.TryParseExact(s, @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out ts))
        {
            // fallback for longer than 24h etc
            var parts = s.Split(':');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out var hh)) return false;
            if (!int.TryParse(parts[1], out var mm)) return false;
            var secParts = parts[2].Split('.');
            if (!int.TryParse(secParts[0], out var ss)) return false;
            var ms = 0;
            if (secParts.Length > 1) int.TryParse(secParts[1].PadRight(3, '0')[..3], out ms);
            ts = new TimeSpan(0, hh, mm, ss, ms);
        }
        return true;
    }
}

