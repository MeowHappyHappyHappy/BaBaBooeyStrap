using System.Windows;
using BaBaBooeyStrap.App.ViewModels;

namespace BaBaBooeyStrap.App.Views.Dialogs.Bootstrapper;

public partial class ByfronDialog : Window, IBootstrapperDialog
{
    private bool _closingProgrammatically;

    public BootstrapperDialogViewModel ViewModel { get; }
    public string Message { get => ViewModel.Message; set => ViewModel.Message = value; }
    public bool CancelEnabled { get => ViewModel.CancelEnabled; set => ViewModel.CancelEnabled = value; }
    public event EventHandler? CancelRequested;

    public ByfronDialog(BootstrapperDialogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        InitializeComponent();

        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        ViewModel.CancelRequested += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Closing += (_, e) => BootstrapperDialogHelper.HandleClosing(_closingProgrammatically, ViewModel, e);
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
