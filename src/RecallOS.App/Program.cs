using System;
using Velopack;

namespace RecallOS.App;

/// <summary>
/// The real entry point.
/// </summary>
/// <remarks>
/// WPF would normally generate this from App.xaml, but the installer hooks have to run
/// before anything else in the process — earlier than <c>Application.OnStartup</c>, which
/// only fires once WPF has already begun starting up.
/// <para>
/// On install, update or uninstall the installer relaunches this executable with hook
/// arguments. Velopack performs that work and exits. Running it from the true entry point
/// means no window is ever created during a hook, and nothing touches the store while the
/// app is being removed.
/// </para>
/// <para>
/// App.xaml is therefore built as a Page rather than an ApplicationDefinition, and
/// <c>InitializeComponent</c> is called here by hand to load the resource dictionaries.
/// </para>
/// </remarks>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
