using System.Windows;
using BaBaBooeyStrap.App.ViewModels;
using BaBaBooeyStrap.Core.Models.Enums;

namespace BaBaBooeyStrap.App.Views.Dialogs.Bootstrapper;

public partial class LegacyProgressDialog : Window, IBootstrapperDialog
{
    private bool _closingProgrammatically;

    public BootstrapperDialogViewModel ViewModel { get; }
    public string Message { get => ViewModel.Message; set => ViewModel.Message = value; }
    public bool CancelEnabled { get => ViewModel.CancelEnabled; set => ViewModel.CancelEnabled = value; }
    public event EventHandler? CancelRequested;

    public LegacyProgressDialog(BootstrapperDialogViewModel viewModel, BootstrapperStyle style = BootstrapperStyle.ProgressDialog)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        InitializeComponent();

        if (style == BootstrapperStyle.VistaDialog)
        {
            Background = System.Windows.Media.Brushes.White;
        }

        ViewModel.CancelRequested += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Closing += (_, _) =>
        {
            if (!_closingProgrammatically && ViewModel.CancelEnabled)
                ViewModel.RequestCancel();
        };
    }

    public void SetProgress(double value, double max, bool indeterminate) =>
        ViewModel.UpdateProgress(value, max, indeterminate);

    public void ShowBootstrapper() => Show();

    public void CloseBootstrapper()
    {
        _closingProgrammatically = true;
        Close();
    }
}
