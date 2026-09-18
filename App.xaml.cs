using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace SubtitleMaster;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    public static Window? CurrentWindow { get; private set; }
    
    public App()
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "app_trace.log"), $"[{DateTime.Now:HH:mm:ss.fff}] App..ctor entered\n"); } catch { }
        UnhandledException += (s, e) =>
        {
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "xaml_crash.txt"), 
                    $"Message: {e.Message}\nException: {e.Exception}\nStackTrace: {e.Exception?.StackTrace}");
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "appdomain_crash.txt"), 
                    $"Object: {e.ExceptionObject}");
            }
            catch { }
        };

        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "init_crash.txt"), ex.ToString());
            throw;
        }
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        try
        {
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "app_trace.log"), 
                $"[{DateTime.Now:HH:mm:ss.fff}] App.OnLaunched entered\n");
            CurrentWindow = new MainWindow();
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "app_trace.log"), 
                $"[{DateTime.Now:HH:mm:ss.fff}] MainWindow created, activating...\n");
            CurrentWindow.Activate();
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "app_trace.log"), 
                $"[{DateTime.Now:HH:mm:ss.fff}] CurrentWindow.Activate completed!\n");

            try
            {
                Services.NotificationService.Instance.Initialize();
            }
            catch { }
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "onlaunched_crash.txt"), ex.ToString());
            throw;
        }
    }
}
