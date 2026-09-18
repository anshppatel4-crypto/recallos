using System.Windows.Controls;

namespace RecallOS.App.Views;

/// <summary>
/// The landing pane: what the recorder is doing, what still needs setting up, and the
/// quickest ways back into a past moment. Purely declarative — everything it shows and
/// every command it invokes lives on <see cref="ViewModels.MainViewModel"/>.
/// </summary>
public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();
}
