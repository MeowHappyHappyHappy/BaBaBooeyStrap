using System.Windows;
using BaBaBooeyStrap.App.ViewModels;

namespace BaBaBooeyStrap.App.Views.Dialogs.Bootstrapper;

public partial class CustomDialog : Window, IBootstrapperDialog
{
    private bool _closingProgrammatically;

    public BootstrapperDialogViewModel ViewModel { get; }
    public string Message { get => ViewModel.Message; set => ViewModel.Message = value; }
    public bool CancelEnabled { get => ViewModel.CancelEnabled; set => ViewModel.CancelEnabled = value; }
    public event EventHandler? CancelRequested;

    public CustomDialog(BootstrapperDialogViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        InitializeComponent();

        ViewModel.CancelRequested += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Loaded += (_, _) => ClampWindowDimensions();
        Closing += (_, e) => BootstrapperDialogHelper.HandleClosing(_closingProgrammatically, ViewModel, e);
    }

    private void ClampWindowDimensions()
    {
        double w = double.IsFinite(Width) ? Width : 500;
        double h = double.IsFinite(Height) ? Height : 320;
        Width = Math.Clamp(w, 320, 900);
        Height = Math.Clamp(h, 180, 600);
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
