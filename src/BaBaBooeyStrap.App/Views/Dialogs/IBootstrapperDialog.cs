using BaBaBooeyStrap.App.ViewModels;

namespace BaBaBooeyStrap.App.Views.Dialogs;

public interface IBootstrapperDialog
{
    BootstrapperDialogViewModel ViewModel { get; }
    string Message { get; set; }
    bool CancelEnabled { get; set; }
    event EventHandler? CancelRequested;

    void SetProgress(double value, double max, bool indeterminate);
    void ShowBootstrapper();
    void CloseBootstrapper();
}
