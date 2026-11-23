using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BaBaBooeyStrap.App.ViewModels;
using Wpf.Ui.Controls;

namespace BaBaBooeyStrap.App.Views.Dialogs.Bootstrapper;

public partial class FluentDialog : FluentWindow, IBootstrapperDialog
{
    private bool _closingProgrammatically;

    public BootstrapperDialogViewModel ViewModel { get; }

    public string Message
    {
        get => ViewModel.Message;
        set => ViewModel.Message = value;
    }

    public bool CancelEnabled
    {
        get => ViewModel.CancelEnabled;
        set => ViewModel.CancelEnabled = value;
    }

    public event EventHandler? CancelRequested;

    public FluentDialog(BootstrapperDialogViewModel viewModel, bool useAeroBackdrop = false)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = ViewModel;
        InitializeComponent();

        WindowBackdropType = useAeroBackdrop ? WindowBackdropType.Acrylic : WindowBackdropType.Mica;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && HasButtonAncestor(source))
            {
                return;
            }

            try
            {
                DragMove();
            }
            catch
            {
            }
        };

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

    private static bool HasButtonAncestor(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }
}
