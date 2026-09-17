using System.Windows.Controls;

namespace RecallOS.App.Views;

/// <summary>
/// The settings panel. Purely declarative: every command and value lives on
/// <see cref="ViewModels.SettingsViewModel"/>.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
