using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BaBaBooeyStrap.App.Models;
using BaBaBooeyStrap.App.Services;
using BaBaBooeyStrap.Bootstrapper.Deployment.Models;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.App.ViewModels;

public sealed class BootstrapperDialogViewModel : ViewModelBase, IProgress<PackageDownloadProgress>
{
    private readonly CancellationTokenSource _cts;
    private readonly Dispatcher? _dispatcher;
    private readonly Stopwatch _throttleTimer = Stopwatch.StartNew();
    private readonly TimeSpan _throttleInterval = TimeSpan.FromMilliseconds(33);
    private readonly object _progressLock = new();

    private string _title = "BaBaBooeyStrap";
    private string _message = LocalizationService.Instance.GetString("Str_Bootstrapper_Preparing");
    private string _packageProgressText = string.Empty;
    private double _progressValue;
    private double _progressMaximum = 100;
    private bool _isIndeterminate = true;
    private bool _cancelEnabled = true;
    private ThemeDefinition _themeColors = XmlThemeParser.GetDefaultFluent();
    private ImageSource? _iconSource;
    private BootstrapperStyle _style = BootstrapperStyle.FluentDialog;
    private long _lastDispatchMs;

    public event EventHandler? CancelRequested;

    public BootstrapperDialogViewModel(
        CancellationTokenSource? cts = null,
        Dispatcher? dispatcher = null)
    {
        _cts = cts ?? new CancellationTokenSource();
        _dispatcher = dispatcher;
        CancelCommand = new RelayCommand(ExecuteCancel, () => CancelEnabled);
    }

    public CancellationToken CancellationToken => _cts.Token;
    public ICommand CancelCommand { get; }
    public ObservableCollection<string> LogLines { get; } = new();

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value) && !string.IsNullOrWhiteSpace(value))
            {
                AppendLogLine($"[INFO] {value}");
            }
        }
    }

    public string PackageProgressText
    {
        get => _packageProgressText;
        set => SetProperty(ref _packageProgressText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public double ProgressMaximum
    {
        get => _progressMaximum;
        set => SetProperty(ref _progressMaximum, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => SetProperty(ref _isIndeterminate, value);
    }

    public bool CancelEnabled
    {
        get => _cancelEnabled;
        set => SetProperty(ref _cancelEnabled, value);
    }

    public ThemeDefinition ThemeColors
    {
        get => _themeColors;
        set => SetProperty(ref _themeColors, value);
    }

    public ImageSource? IconSource
    {
        get => _iconSource;
        set => SetProperty(ref _iconSource, value);
    }

    public BootstrapperStyle Style
    {
        get => _style;
        set => SetProperty(ref _style, value);
    }

    public void Report(PackageDownloadProgress value)
    {
        bool isFinal = value.TotalPackages > 0 && value.CompletedPackages >= value.TotalPackages;

        lock (_progressLock)
        {
            long nowMs = _throttleTimer.ElapsedMilliseconds;
            if (!isFinal && (nowMs - _lastDispatchMs) < _throttleInterval.TotalMilliseconds)
            {
                return;
            }
            _lastDispatchMs = nowMs;
        }

        void ApplyUpdate()
        {
            IsIndeterminate = false;
            ProgressMaximum = 100;
            ProgressValue = Math.Clamp(value.ProgressPercentage, 0, 100);
            PackageProgressText = LocalizationService.Instance.Format(
                "Str_Bootstrapper_PackageProgressFormat",
                value.CompletedPackages,
                value.TotalPackages,
                value.ProgressPercentage);
            _message = value.IsExtracting
                ? (string.IsNullOrWhiteSpace(value.CurrentPackage)
                    ? LocalizationService.Instance.GetString("Str_Bootstrapper_StatusExtracting")
                    : LocalizationService.Instance.Format("Str_Bootstrapper_ExtractingNamedFormat", value.CurrentPackage))
                : (string.IsNullOrWhiteSpace(value.CurrentPackage)
                    ? LocalizationService.Instance.GetString("Str_Bootstrapper_DownloadingPackages")
                    : LocalizationService.Instance.Format("Str_Bootstrapper_DownloadingNamedFormat", value.CurrentPackage));
            OnPropertyChanged(nameof(Message));
        }

        if (_dispatcher != null && !_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(ApplyUpdate, DispatcherPriority.Background);
        }
        else
        {
            ApplyUpdate();
        }
    }

    public void UpdateProgress(double value, double maximum, bool indeterminate)
    {
        void Apply()
        {
            IsIndeterminate = indeterminate;
            ProgressMaximum = maximum > 0 ? maximum : 100;
            ProgressValue = Math.Clamp(value, 0, ProgressMaximum);
        }

        if (_dispatcher != null && !_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(Apply, DispatcherPriority.Background);
        }
        else
        {
            Apply();
        }
    }

    public void RequestCancel() => ExecuteCancel();

    private void ExecuteCancel()
    {
        if (!CancelEnabled) return;
        CancelEnabled = false;
        Message = LocalizationService.Instance.GetString("Str_Bootstrapper_Cancelling");
        try { _cts.Cancel(); } catch { }
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AppendLogLine(string line)
    {
        void Add()
        {
            if (LogLines.Count > 200)
                LogLines.RemoveAt(0);
            LogLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
        }

        if (_dispatcher != null && !_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(Add, DispatcherPriority.Background);
        }
        else
        {
            Add();
        }
    }
}
