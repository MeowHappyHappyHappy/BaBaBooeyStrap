using System.ComponentModel;
using System.Windows;
using BaBaBooeyStrap.App.ViewModels;

namespace BaBaBooeyStrap.App.Views.Dialogs.Bootstrapper;

internal static class BootstrapperDialogHelper
{
    public static void HandleClosing(
        bool closingProgrammatically,
        BootstrapperDialogViewModel viewModel,
        CancelEventArgs e)
    {
        if (closingProgrammatically)
        {
            return;
        }

        if (!viewModel.CancelEnabled)
        {
            e.Cancel = true;
            return;
        }

        viewModel.RequestCancel();
    }

    public static void DispatchIfAlive(Window window, Action action)
    {
        var dispatcher = window.Dispatcher;
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }
}
