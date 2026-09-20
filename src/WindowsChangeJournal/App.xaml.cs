using System.Security.Cryptography;
using System.Text;
using System.Windows;
using WindowsChangeJournal.Services;
namespace WindowsChangeJournal;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        var customDataDirectory = Environment.GetEnvironmentVariable("WINDOWS_CHANGE_JOURNAL_DATA_DIR");
        var instanceName = @"Local\WindowsChangeJournal.SingleInstance";
        if (!string.IsNullOrWhiteSpace(customDataDirectory))
        {
            var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDirectory)))[..12];
            instanceName += "." + scope;
        }
        _singleInstance = new Mutex(true, instanceName, out var created);
        if (!created)
        {
            MessageBox.Show("事件记录仪已经在运行，请从任务栏托盘打开。", "事件记录仪", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
    }

    private void Application_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "事件记录仪", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
