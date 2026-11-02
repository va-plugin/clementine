using System.Windows;
using Clementine.App.Startup;

namespace Clementine.App;

public partial class App : Application
{
    /// <summary>Set by <see cref="Program.Main"/> when it has already made this process the primary instance.</summary>
    internal SingleInstance? PrimaryInstance { get; init; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShellBootstrap.Run(this, e.Args, PrimaryInstance);
    }
}
