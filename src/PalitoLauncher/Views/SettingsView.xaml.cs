using System.Windows.Controls;
using System.Windows.Input;
using PalitoLauncher.ViewModels;

namespace PalitoLauncher.Views;

/// <summary>Página de Ajustes (guardado inmediato).</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    // Los argumentos se guardan también al perder el foco (además del retardo de 500 ms).
    private void LaunchArgumentsBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings) settings.CommitLaunchArguments();
    }

    // El apodo de las opiniones se guarda al perder el foco.
    private void FeedbackNicknameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is SettingsViewModel settings) settings.CommitFeedbackNickname();
    }
}
