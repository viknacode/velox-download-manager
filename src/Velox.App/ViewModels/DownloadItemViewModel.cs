using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using Velox.Core.Models;
using Velox.Core.Services;
using Velox.Core.Utils;

namespace Velox.App.ViewModels;

public sealed class DownloadItemViewModel : ObservableObject
{
    private readonly MainViewModel _parent;

    public DownloadItem Model { get; }
    public Guid Id => Model.Id;
    public DateTime CreatedAt => Model.CreatedAt;

    public DownloadItemViewModel(DownloadItem model, MainViewModel parent)
    {
        Model = model;
        _parent = parent;

        ToggleCommand = new RelayCommand(() => _parent.Toggle(this));
        OpenFolderCommand = new RelayCommand(OpenFolder);
        OpenFileCommand = new RelayCommand(OpenFile, () => IsCompleted);
        RemoveCommand = new RelayCommand(() => _parent.Remove(this, false));
        RemoveWithFileCommand = new RelayCommand(() => _parent.Remove(this, true));
        RestartCommand = new RelayCommand(() => _parent.Restart(this));
        CopyUrlCommand = new RelayCommand(() => _parent.CopyUrl(this));

        Refresh(true);
    }

    // ------------------------------------------------------------ comandos
    public ICommand ToggleCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand RemoveWithFileCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand CopyUrlCommand { get; }

    // ------------------------------------------------------------ propriedades
    private string _fileName = "";
    public string FileName { get => _fileName; private set => Set(ref _fileName, value); }

    private string _url = "";
    public string Url { get => _url; private set => Set(ref _url, value); }

    private string _fullPath = "";
    public string FullPath { get => _fullPath; private set => Set(ref _fullPath, value); }

    private string _category = "";
    public string Category { get => _category; private set => Set(ref _category, value); }

    private string _iconGlyph = "";
    public string IconGlyph { get => _iconGlyph; private set => Set(ref _iconGlyph, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string _statusKind = "queued";
    public string StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    private string _percentText = "";
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }

    private string _detailText = "";
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

    private string _speedText = "";
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }

    private string _etaText = "";
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }

    private double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    private long _totalSize = -1;
    public long TotalSize { get => _totalSize; private set => Set(ref _totalSize, value); }

    private bool _isIndeterminate;
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }

    private SegmentSnapshot[] _segments = Array.Empty<SegmentSnapshot>();
    public SegmentSnapshot[] Segments { get => _segments; private set => Set(ref _segments, value); }

    private double[] _speedHistory = Array.Empty<double>();
    public double[] SpeedHistory { get => _speedHistory; private set => Set(ref _speedHistory, value); }

    private bool _isActive;
    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }

    private bool _isCompleted;
    public bool IsCompleted { get => _isCompleted; private set => Set(ref _isCompleted, value); }

    private bool _isFailed;
    public bool IsFailed { get => _isFailed; private set => Set(ref _isFailed, value); }

    private bool _canToggle = true;
    public bool CanToggle { get => _canToggle; private set => Set(ref _canToggle, value); }

    private string _toggleGlyph = "";
    public string ToggleGlyph { get => _toggleGlyph; private set => Set(ref _toggleGlyph, value); }

    private string _toggleTooltip = "Iniciar";
    public string ToggleTooltip { get => _toggleTooltip; private set => Set(ref _toggleTooltip, value); }

    private string _toggleLabel = "Iniciar";
    public string ToggleLabel { get => _toggleLabel; private set => Set(ref _toggleLabel, value); }

    // detalhes
    private string _sizeText = "";
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }

    private string _downloadedText = "";
    public string DownloadedText { get => _downloadedText; private set => Set(ref _downloadedText, value); }

    private string _averageSpeedText = "";
    public string AverageSpeedText { get => _averageSpeedText; private set => Set(ref _averageSpeedText, value); }

    private string _connectionsText = "";
    public string ConnectionsText { get => _connectionsText; private set => Set(ref _connectionsText, value); }

    private string _resumeText = "";
    public string ResumeText { get => _resumeText; private set => Set(ref _resumeText, value); }

    private string _elapsedText = "";
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    private string _retriesText = "";
    public string RetriesText { get => _retriesText; private set => Set(ref _retriesText, value); }

    private string _hashText = "";
    public string HashText { get => _hashText; private set => Set(ref _hashText, value); }

    private string? _errorText;
    public string? ErrorText { get => _errorText; private set => Set(ref _errorText, value); }

    private string _segmentsText = "";
    public string SegmentsText { get => _segmentsText; private set => Set(ref _segmentsText, value); }

    private string _remainingText = "";
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    private string _createdText = "";
    public string CreatedText { get => _createdText; private set => Set(ref _createdText, value); }

    private bool _isStream;
    public bool IsStream { get => _isStream; private set => Set(ref _isStream, value); }

    /// <summary>Qualidade escolhida do stream (ex.: "1080p · 6,2 Mbps"), vazio para arquivos comuns.</summary>
    private string _variantLabel = "";
    public string VariantLabel { get => _variantLabel; private set => Set(ref _variantLabel, value); }

    // ------------------------------------------------------------ refresh
    public void Refresh(bool full)
    {
        var m = Model;

        FileName = m.FileName;
        Url = m.Url;
        FullPath = m.FullPath;
        Category = m.Category;
        IconGlyph = GlyphFor(m.Category);
        TotalSize = m.TotalSize;
        Progress = m.Progress;

        IsActive = m.IsActive;
        IsCompleted = m.Status == DownloadStatus.Completed;
        IsFailed = m.Status == DownloadStatus.Failed;
        IsIndeterminate = m.Status == DownloadStatus.Connecting || m.Status == DownloadStatus.Verifying || m.Status == DownloadStatus.Merging ||
                          (m.Status == DownloadStatus.Downloading && m.TotalSize <= 0 && !(m.IsStream && m.StreamSegmentsTotal > 0));

        (StatusText, StatusKind) = m.Status switch
        {
            DownloadStatus.Queued => ("Na fila", "queued"),
            DownloadStatus.Connecting => ("Conectando", "active"),
            DownloadStatus.Downloading when m.ActiveConnections == 0 && m.RetryingConnections > 0 => ("Reconectando", "active"),
            DownloadStatus.Downloading => ("Baixando", "active"),
            DownloadStatus.Paused => ("Pausado", "paused"),
            DownloadStatus.Completed => ("Concluído", "done"),
            DownloadStatus.Failed => ("Falhou", "error"),
            DownloadStatus.Verifying => ("Verificando", "active"),
            DownloadStatus.Merging => ("Juntando", "active"),
            _ => (m.Status.ToString(), "queued")
        };

        IsStream = m.IsStream;
        VariantLabel = m.IsStream
            ? (m.Kind == StreamKind.Youtube ? "YouTube" + (m.VariantLabel != null ? " · " + m.VariantLabel : "") : m.VariantLabel ?? (m.Kind == StreamKind.Dash ? "DASH" : "HLS"))
            : "";
        string approx = m.TotalIsEstimate ? "≈" : "";
        PercentText = m.TotalSize > 0 || IsCompleted || (m.IsStream && m.StreamSegmentsTotal > 0) ? FormatHelper.Percent(m.Progress) : "";
        ProgressText = m.Status == DownloadStatus.Completed
            ? FormatHelper.Bytes(m.TotalSize > 0 ? m.TotalSize : m.DownloadedBytes)
            : m.IsStream && m.StreamSegmentsTotal > 0
                ? $"{m.StreamSegmentsDone} de {m.StreamSegmentsTotal} segmentos  ·  {FormatHelper.Bytes(m.DownloadedBytes)}"
            : m.TotalSize > 0
                ? $"{FormatHelper.Bytes(m.DownloadedBytes)} de {approx}{FormatHelper.Bytes(m.TotalSize)}"
                : FormatHelper.Bytes(m.DownloadedBytes);

        SpeedText = m.IsActive && m.Status == DownloadStatus.Downloading ? FormatHelper.Speed(m.Speed) : "";
        EtaText = m.Status switch
        {
            DownloadStatus.Downloading when m.Eta != null => FormatHelper.Eta(m.Eta) + " restantes",
            DownloadStatus.Downloading when m.ActiveConnections == 0 && m.RetryingConnections > 0 => $"tentativa {m.RetryCount}",
            DownloadStatus.Downloading => "calculando…",
            DownloadStatus.Completed when m.CompletedAt != null => m.CompletedAt.Value.ToString("dd/MM HH:mm"),
            _ => ""
        };

        DetailText = m.Status switch
        {
            DownloadStatus.Merging => m.IsStream && m.Kind == StreamKind.Dash ? "juntando vídeo e áudio…" : "montando o arquivo…",
            DownloadStatus.Completed when m.IsStream && !string.IsNullOrEmpty(m.Note) => m.Note,
            DownloadStatus.Downloading when m.ActiveConnections > 0 =>
                $"{m.ActiveConnections} {(m.ActiveConnections == 1 ? "conexão" : "conexões")}",
            DownloadStatus.Downloading when m.RetryingConnections > 0 => "aguardando servidor…",
            DownloadStatus.Failed => m.ErrorMessage ?? "Erro desconhecido",
            DownloadStatus.Completed when !string.IsNullOrEmpty(m.ComputedHash) => "integridade verificada",
            DownloadStatus.Paused when m.DownloadedBytes == 0 => "não iniciado",
            DownloadStatus.Paused when m.SupportsResume => "retomável",
            DownloadStatus.Paused => "sem suporte a retomada — reinicia do zero",
            _ => ""
        };

        CanToggle = m.Status != DownloadStatus.Completed && m.Status != DownloadStatus.Verifying && m.Status != DownloadStatus.Merging;
        if (m.IsActive || m.Status == DownloadStatus.Queued)
        {
            ToggleGlyph = "";
            ToggleTooltip = "Pausar";
            ToggleLabel = "Pausar";
        }
        else if (m.Status == DownloadStatus.Failed)
        {
            ToggleGlyph = "";
            ToggleTooltip = "Tentar novamente";
            ToggleLabel = "Tentar novamente";
        }
        else
        {
            ToggleGlyph = "";
            ToggleTooltip = m.DownloadedBytes > 0 ? "Retomar" : "Iniciar";
            ToggleLabel = ToggleTooltip;
        }

        Segments = m.SnapshotSegments();

        if (full)
        {
            SizeText = (m.TotalIsEstimate && !IsCompleted ? "≈ " : "") + FormatHelper.Bytes(m.TotalSize);
            DownloadedText = FormatHelper.Bytes(m.DownloadedBytes);
            AverageSpeedText = m.AverageSpeed > 0 ? FormatHelper.Speed(m.AverageSpeed) : "—";
            ConnectionsText = m.IsActive ? $"{m.ActiveConnections} / {m.MaxConnections}" : $"até {m.MaxConnections}";
            ResumeText = m.SupportsResume ? "Sim" : (m.TotalSize < 0 && m.Status == DownloadStatus.Queued ? "—" : "Não");
            ElapsedText = m.ElapsedSeconds > 0 ? FormatHelper.Duration(m.ElapsedSeconds) : "—";
            RetriesText = m.RetryCount.ToString();
            HashText = !string.IsNullOrEmpty(m.ComputedHash) ? m.ComputedHash : "";
            ErrorText = m.Status == DownloadStatus.Failed ? m.ErrorMessage : null;
            SegmentsText = m.IsStream
                ? $"{m.StreamSegmentsDone} / {m.StreamSegmentsTotal} segmentos de mídia"
                : $"{Segments.Count(s => s.IsCompleted)} / {Segments.Length} segmentos";
            CreatedText = m.CreatedAt.ToString("dd/MM HH:mm");
            RemainingText = m.Status == DownloadStatus.Downloading ? FormatHelper.Eta(m.Eta) : (m.Status == DownloadStatus.Completed ? "concluído" : "—");
            SpeedHistory = m.GetSpeedHistory();
        }
    }

    public static string GlyphFor(string category) => category switch
    {
        CategoryDetector.Video => "",
        CategoryDetector.Music => "",
        CategoryDetector.Documents => "",
        CategoryDetector.Compressed => "",
        CategoryDetector.Programs => "",
        CategoryDetector.Images => "",
        _ => ""
    };

    private void OpenFolder()
    {
        try
        {
            if (File.Exists(FullPath))
                Process.Start("explorer.exe", $"/select,\"{FullPath}\"");
            else if (Directory.Exists(Model.Directory))
                Process.Start("explorer.exe", $"\"{Model.Directory}\"");
        }
        catch { }
    }

    private void OpenFile()
    {
        try
        {
            if (File.Exists(FullPath))
                Process.Start(new ProcessStartInfo(FullPath) { UseShellExecute = true });
        }
        catch { }
    }
}
