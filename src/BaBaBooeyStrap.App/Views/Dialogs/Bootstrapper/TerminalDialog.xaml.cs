using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using BaBaBooeyStrap.App.ViewModels;

namespace BaBaBooeyStrap.App.Views.Dialogs.Bootstrapper;

public partial class TerminalDialog : Window, IBootstrapperDialog
{
    private bool _closingProgrammatically;

    public BootstrapperDialogViewModel ViewModel { get; }
    public string Message { get => ViewModel.Message; set => ViewModel.Message = value; }
    public bool CancelEnabled { get => ViewModel.CancelEnabled; set => ViewModel.CancelEnabled = value; }
    public event EventHandler? CancelRequested;

    public TerminalDialog(BootstrapperDialogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        InitializeComponent();

        ViewModel.CancelRequested += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        ((INotifyCollectionChanged)ViewModel.LogLines).CollectionChanged += OnLogLinesCollectionChanged;
        Closing += (_, e) => BootstrapperDialogHelper.HandleClosing(_closingProgrammatically, ViewModel, e);
        Closed += (_, _) => ((INotifyCollectionChanged)ViewModel.LogLines).CollectionChanged -= OnLogLinesCollectionChanged;
    }

    private void OnLogLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.LogLines.Count == 0)
        {
            return;
        }

        BootstrapperDialogHelper.DispatchIfAlive(this, () =>
        {
            if (LogListBox.Items.Count > 0)
            {
                LogListBox.ScrollIntoView(LogListBox.Items[^1]);
            }
        });
    }

    private void LogListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            var lines = LogListBox.SelectedItems.Count > 0
                ? LogListBox.SelectedItems.Cast<object>().Select(x => x?.ToString() ?? string.Empty)
                : ViewModel.LogLines;

            string text = string.Join(Environment.NewLine, lines);
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch
                {
                }
            }
        }
    }

    public void SetProgress(double value, double max, bool indeterminate) =>
        ViewModel.UpdateProgress(value, max, indeterminate);

    public void ShowBootstrapper() =>
        BootstrapperDialogHelper.DispatchIfAlive(this, Show);

    public void CloseBootstrapper() =>
        BootstrapperDialogHelper.DispatchIfAlive(this, () =>
        {
            _closingProgrammatically = true;
            Close();
        });
}
