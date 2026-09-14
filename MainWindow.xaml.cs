using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MeshinaStandalone
{
    public partial class MainWindow : Window
    {
        private readonly string configDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs");
        private readonly object logLock = new object();
        private Configuration config;
        private SystemSettings system;
        private ApiSettings api;
        private MeshinaStationService service;
        private MeshinaMesGateway gateway;
        private SerialScanner scanner;
        private CancellationTokenSource cancellation;
        private Task loop;
        private bool busy, closing, allowClose;
        public MainWindow() { InitializeComponent(); LoadConfiguration(); }
        private void LoadConfiguration()
        {
            try
            {
                config = Configuration.Load(configDirectory);
                StationBox.ItemsSource = config.Stations;
                StationBox.SelectedItem = config.Stations.Single(s => s.Number == config.Meshina.StationNumber);
                StatusText.Text = "请核对工位、线别及接口，点击启动工位";
            }
            catch (Exception ex) { config = null; StatusText.Text = "配置加载失败：" + ex.Message; Log(StatusText.Text); }
            Render();
        }
        private void StationChanged(object sender, SelectionChangedEventArgs e)
        {
            if (config == null || !(StationBox.SelectedItem is Station station)) return;
            try
            {
                var selected = config.Resolve(station.Number);
                ConfigText.Text = $"{selected.system.Line} · COM{config.Scanners[config.Meshina.ScannerIndex].Com}";
                ConfigText.ToolTip = $"StationID：{selected.system.StationID}\nMachineID：{selected.system.MachineID}\nMDB：{config.Meshina.DataDirectory}\nMES：{selected.api.BaseUrl}";
            }
            catch (Exception ex) { ConfigText.Text = ex.Message; }
        }
        private void StartClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (service != null || config == null || !(StationBox.SelectedItem is Station station)) return;
                var selected = config.Resolve(station.Number); system = selected.system; api = selected.api;
                if (system.IsSimulate) throw new InvalidOperationException("sys.json的IsSimulate为true；本程序只支持真实MES流程，请核对后设为false。");
                if (!Directory.Exists(config.Meshina.DataDirectory)) throw new DirectoryNotFoundException("MDB目录不存在：" + config.Meshina.DataDirectory);
                if (Type.GetTypeFromProgID(config.Meshina.Provider) == null) throw new InvalidOperationException("未安装32位数据库驱动：" + config.Meshina.Provider);
                _ = new MdbPoller(config.Meshina.DataDirectory).Snapshot();
                gateway = new MeshinaMesGateway(api, Log);
                service = new MeshinaStationService(config.Meshina, new MdbPoller(config.Meshina.DataDirectory), new MdbReader(config.Meshina.Provider), gateway, Log);
                // 三个啮合站均连接scan.json中指定的COM口。
                {
                    scanner = new SerialScanner(config.Scanners[config.Meshina.ScannerIndex], sn =>
                    {
                        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(async () => await SubmitAsync(sn)));
                    }, Log);
                    scanner.Open();
                }
                cancellation = new CancellationTokenSource();
                var runningService = service; var token = cancellation.Token;
                loop = Task.Run(() => runningService.RunAsync(token));
                Log("工位已启动：" + station + "，等待扫码。请勿同时用原MES啮合流程处理同一检测目录。");
                Render(); SnBox.Focus();
            }
            catch (Exception ex) { ReleaseResources(); StatusText.Text = "启动失败：" + ex.Message; Log(StatusText.Text); Render(); }
        }
        private async void StopClick(object sender, RoutedEventArgs e)
        {
            if (service?.Current != null || busy) return;
            busy = true; Render();
            service?.Stop(); cancellation?.Cancel();
            if (loop != null) await loop;
            ReleaseResources(); busy = false; StatusText.Text = "工位已停止"; Render();
        }
        private async void ScanClick(object sender, RoutedEventArgs e) => await SubmitAsync(SnBox.Text);
        private async void SnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SubmitAsync(SnBox.Text); }
        }
        private async Task SubmitAsync(string sn)
        {
            if (closing || busy || service == null) { Log("工位未就绪或正在处理，本次扫码未接收。"); return; }
            string opid = system.OPID ?? "";
            await Operate(async () =>
            {
                sn = (sn ?? "").Trim();
                if (sn.Length == 0) throw new InvalidOperationException("条码不能为空。");
                if (system.SNCodeLen > 0 && sn.Length != system.SNCodeLen) throw new InvalidOperationException("条码长度应为" + system.SNCodeLen);
                if (!string.IsNullOrEmpty(system.RegexRule) && !Regex.IsMatch(sn, system.RegexRule, RegexOptions.None, TimeSpan.FromSeconds(1))) throw new InvalidOperationException("条码不符合RegexRule。");
                await service.ScanAsync(sn, () => Configuration.CreateJob(system, api, opid));
            });
            SnBox.Clear(); SnBox.Focus();
        }
        private async Task Operate(Func<Task> action)
        {
            if (busy) return;
            busy = true; Render();
            try { await action(); }
            catch (Exception ex) { Log("操作失败：" + ex.Message); }
            finally { busy = false; Render(); }
        }
        private async void RetryClick(object sender, RoutedEventArgs e)
        {
            if (service?.CanRetry != true) return;
            if (MessageBox.Show("将重发当前任务的MES请求。若上次超时或结果未知，请先核实MES记录。确认重试？", "重试原任务", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                await Operate(() => service.RetryAsync());
        }
        private async void AbortClick(object sender, RoutedEventArgs e)
        {
            if (service?.CanAbort != true) return;
            if (MessageBox.Show("终止将清空队列中的全部任务，已发送MES请求无法撤回。请确保旧件不再检测或延迟保存MDB，再扫描新件。", "终止全部任务", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                await service.AbortAsync();
            Render();
        }
        private void SettingsClick(object sender, RoutedEventArgs e)
        {
            if (service != null) return;
            if (new SettingsWindow(configDirectory) { Owner = this }.ShowDialog() == true) LoadConfiguration();
        }
        private void Render()
        {
            bool running = service != null;
            StationBox.IsEnabled = !running && !busy; StartButton.IsEnabled = config != null && !running && !busy;
            SettingsButton.IsEnabled = !running && !busy; StopButton.IsEnabled = running && service.Current == null && !busy;
            SnBox.IsEnabled = running && !busy && service.CanScan; ScanButton.IsEnabled = SnBox.IsEnabled;
            RetryButton.IsEnabled = !busy && service?.CanRetry == true;
            AbortButton.IsEnabled = service?.CanAbort == true;
            if (running) StatusText.Text = service.Status;
            var queued = service?.Jobs ?? Array.Empty<MeshinaJob>();
            QueueText.Text = $"任务队列（{queued.Length}/2） · 结束结果保留至该位置接收新任务";
            var displayed = service?.DisplayJobs ?? new MeshinaJob[MeshinaStationService.Capacity];
            TaskCards.ItemsSource = displayed.Select((job, index) => new
            {
                Title = $"任务 {index + 1}",
                SN = "SN：" + (job?.SN ?? "—"),
                ScanTime = "扫码时间：" + (job == null ? "—" : job.ScanTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")),
                Stage = job == null ? "等待扫码" : StageName(job.Stage),
                Feeding = ResultText(job?.FeedingReply, job?.Stage == MeshinaStage.FeedingCheckSending, job?.Stage == MeshinaStage.Cancelled, "待扫码"),
                Checkout = ResultText(job?.CheckoutReply, job?.Stage == MeshinaStage.CheckOutSending, job?.Stage == MeshinaStage.Cancelled,
                    job?.FeedingReply?.Outcome == MeshinaMesOutcome.Accepted ? "待检测" : "未开始"),
                FeedingColor = ResultColor(job?.FeedingReply, job?.Stage == MeshinaStage.FeedingCheckSending),
                CheckoutColor = ResultColor(job?.CheckoutReply, job?.Stage == MeshinaStage.CheckOutSending),
                FeedingMessage = job?.FeedingReply?.Message,
                CheckoutMessage = job?.CheckoutReply?.Message,
                Mdb = "MDB：" + (job?.MdbPath == null ? "未绑定" : Path.GetFileName(job.MdbPath)),
                MdbPath = job?.MdbPath,
                Values = MdbReader.NumericFields.Select(field => new
                {
                    Field = field,
                    Value = job?.Measurement?.Values.TryGetValue(field, out var value) == true ? value.ToString(CultureInfo.InvariantCulture) : "—"
                }).ToArray()
            }).ToArray();
            StatusText.ToolTip = StatusText.Text;
        }
        private static string ResultText(MeshinaMesReply reply, bool sending, bool cancelled, string waiting) =>
            sending ? "处理中…" : reply != null ?
                (reply.Outcome == MeshinaMesOutcome.Accepted ? "OK" : reply.Outcome == MeshinaMesOutcome.Rejected ? "NG" : "结果未知") :
                cancelled ? "已终止" : waiting;
        private static System.Windows.Media.Brush ResultColor(MeshinaMesReply reply, bool sending) =>
            sending ? System.Windows.Media.Brushes.DodgerBlue : reply == null ? System.Windows.Media.Brushes.SlateGray :
                reply.Outcome == MeshinaMesOutcome.Accepted ? System.Windows.Media.Brushes.ForestGreen :
                reply.Outcome == MeshinaMesOutcome.Rejected ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DarkOrange;
        private static string StageName(MeshinaStage stage) => stage switch
        {
            MeshinaStage.FeedingCheckSending => "正在进站校验",
            MeshinaStage.FeedingCheckRejected => "进站未成功，请处理",
            MeshinaStage.WaitingForMdb => "等待检测并保存MDB",
            MeshinaStage.ReadyForCheckOut => "数据已读取，等待出站",
            MeshinaStage.CheckOutSending => "正在提交MES出站",
            MeshinaStage.CheckOutRejected => "出站未成功，请处理",
            MeshinaStage.Completed => "已完成",
            MeshinaStage.Cancelled => "已终止",
            _ => stage.ToString()
        };
        private void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message;
            try
            {
                lock (logLock)
                {
                    string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"); Directory.CreateDirectory(dir);
                    File.AppendAllText(Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd") + ".log"), line + Environment.NewLine);
                }
            }
            catch (Exception ex) { line += " [日志写入失败：" + ex.Message + "]"; }
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogBox.Text.Length > 100000) LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length - 50000);
                LogBox.AppendText(line + Environment.NewLine); LogBox.ScrollToEnd(); Render();
            }));
        }
        private void ReleaseResources()
        {
            // 单个资源释放失败也要继续释放其余资源；先清空引用，避免重复清理。
            var oldScanner = scanner; scanner = null;
            var oldService = service; service = null;
            var oldCancellation = cancellation; cancellation = null;
            var oldGateway = gateway; gateway = null; loop = null;
            void Clean(Action action)
            {
                try { action(); }
                catch (Exception ex) { Log("资源清理失败：" + ex.Message); }
            }
            Clean(() => oldService?.Stop());
            Clean(() => oldCancellation?.Cancel());
            Clean(() => oldScanner?.Dispose());
            Clean(() => oldCancellation?.Dispose());
            Clean(() => oldGateway?.Dispose());
        }
        private async void WindowClosing(object sender, CancelEventArgs e)
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            if (service?.Current != null && MessageBox.Show("当前任务尚未完成，关闭后不会自动恢复。已发送MES请求无法撤回，确认关闭？", "关闭程序", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            closing = true; IsEnabled = false;
            // 即使后面的Task均已完成，也必须先退出本次Closing事件再调用Close。
            await System.Windows.Threading.Dispatcher.Yield();
            try
            {
                service?.Stop(); cancellation?.Cancel();
                if (service != null) await service.AbortAsync();
                if (loop != null) await loop;
            }
            catch (Exception ex) { Log("关闭时等待后台任务失败：" + ex.Message); }
            finally
            {
                ReleaseResources();
                allowClose = true;
                Close();
            }
        }
    }
}
