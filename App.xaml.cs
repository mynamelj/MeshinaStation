using System.Threading;
using System.Windows;
namespace MeshinaStandalone
{
    public partial class App : Application
    {
        private Mutex mutex;
        protected override void OnStartup(StartupEventArgs e)
        {
            mutex = new Mutex(true, @"Local\LuxshareMeshinaStandalone", out bool created);
            if (!created) { MessageBox.Show("独立啮合程序已经运行。"); Shutdown(); return; }
            base.OnStartup(e);
        }
        protected override void OnExit(ExitEventArgs e) { mutex?.Dispose(); base.OnExit(e); }
    }
}
