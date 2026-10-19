using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.Views;

/// <summary>Vista del diálogo de mensaje. Pone el foco inicial (primario, o secundario si es peligroso).</summary>
public partial class MessageDialogView : UserControl
{
    public MessageDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyDialog();
        DataContextChanged += (_, _) => ApplyDialog();
    }

    private void ApplyDialog()
    {
        if (DataContext is not MessageDialogViewModel dialog || !IsLoaded) return;

        PrimaryButton.Style = (Style)FindResource(dialog.IsDanger ? "DangerButton" : "AccentButton");

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!ReferenceEquals(DataContext, dialog)) return;
            var target = dialog.IsDanger && dialog.HasSecondary ? (Button)SecondaryButton : PrimaryButton;
            target.Focus();
        }));
    }
}
