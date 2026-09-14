using System.Globalization;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace MeshinaStandalone
{
    /// <summary>两件任务独立出站；MDB按扫码顺序预绑定，绑定后不再换文件。</summary>
    public sealed class MeshinaStationService
    {
        public const int Capacity = 2;
        private readonly object sync = new object();
        private readonly MeshinaJob[] displayJobs = new MeshinaJob[Capacity];
        private readonly List<MeshinaJob> jobs = new List<MeshinaJob>();
        private readonly Dictionary<string, string> bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<MeshinaJob, TaskCompletionSource<bool>> operations = new Dictionary<MeshinaJob, TaskCompletionSource<bool>>();
        private readonly MeshinaSettings settings;
        private readonly KeyValuePair<string, string>[] itemNames;
        private readonly MdbPoller poller;
        private readonly IMdbReader reader;
        private readonly IMeshinaMesGateway gateway;
        private readonly Action<string> log;
        private readonly Func<DateTime> utcNow;
        private bool stopped, aborting;
        private string status = "等待扫码进站";
        public string Status { get { lock (sync) return status; } }
        public MeshinaJob[] DisplayJobs { get { lock (sync) return (MeshinaJob[])displayJobs.Clone(); } }
        public MeshinaJob[] Jobs { get { lock (sync) return jobs.ToArray(); } }
        public MeshinaJob Current { get { lock (sync) return jobs.FirstOrDefault(); } }
        public MeshinaJob LastFinished { get; private set; }
        public bool CanScan { get { lock (sync) return !stopped && !aborting && jobs.Count < Capacity; } }
        public bool CanAbort { get { lock (sync) return !aborting && displayJobs.Any(j => j != null); } }
        public bool[] TaskEnabled { get { lock (sync) return displayJobs.Select(j => !stopped && !aborting && (j == null || !jobs.Contains(j))).ToArray(); } }
        public bool CanRetry => false;

        public MeshinaStationService(MeshinaSettings settings, MdbPoller poller, IMdbReader reader,
            IMeshinaMesGateway gateway, Action<string> log, Func<DateTime> utcNow = null)
        {
            settings.ValidateItemNames();
            if (settings.MdbWaitTimeoutSeconds < 1) throw new ArgumentOutOfRangeException(nameof(settings.MdbWaitTimeoutSeconds));
            itemNames = settings.ItemNames.ToArray();
            this.settings = settings; this.poller = poller; this.reader = reader;
            this.gateway = gateway; this.log = log; this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public Task ScanAsync(string sn, Func<MeshinaJob> createRequest, CancellationToken token = default)
        {
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                sn = sn?.Trim();
                if (string.IsNullOrEmpty(sn)) { Report("扫码为空，未进站"); return Task.CompletedTask; }
                if (jobs.Any(j => string.Equals(j.SN, sn, StringComparison.OrdinalIgnoreCase)))
                { Report($"SN:{sn} 已有未结束任务，拒绝重复扫码"); return Task.CompletedTask; }
                if (!CanScan) { Report("工位未就绪、正在终止或队列已满（2/2），本次扫码未接收"); return Task.CompletedTask; }
                try
                {
                    // 先为旧任务认领文件，避免第二次扫码的基线影响旧任务。
                    var scanTime = utcNow();
                    var files = poller.Snapshot();
                    BindFiles(files);
                    var job = createRequest();
                    job.SN = sn; job.ScanTimeUtc = scanTime;
                    job.BaselineFiles = files.Select(f => f.Path).ToList();
                    job.FeedingCheckRequest.SN = sn;
                    job.Stage = MeshinaStage.FeedingCheckSending;
                    job.MdbWaitTimeoutSeconds = settings.MdbWaitTimeoutSeconds;
                    // 槽位1始终优先；jobs仍保持扫码先后顺序，用于MDB认领。
                    int slot = Array.FindIndex(displayJobs, j => j == null || !jobs.Contains(j));
                    displayJobs[slot] = job;
                    jobs.Add(job);
                    return StartOperation(job, () => SendFeedingCheckAsync(job));
                }
                catch (Exception ex) { Report("扫码进站未完成：" + ex.Message); return Task.CompletedTask; }
            }
        }

        // 注册后再启动，终止能够等待所有已开始的读文件和MES请求。
        private Task StartOperation(MeshinaJob job, Func<Task> action)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            operations.Add(job, completion);
            _ = Execute();
            return completion.Task;
            async Task Execute()
            {
                try { await action().ConfigureAwait(false); }
                catch (Exception ex) { lock (sync) Report($"SN:{job.SN} 暂停处理：{ex.Message}"); }
                finally
                {
                    lock (sync) { operations.Remove(job); completion.TrySetResult(true); }
                }
            }
        }

        public async Task RunAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(settings.PollIntervalMilliseconds, token).ConfigureAwait(false);
                    lock (sync) if (stopped) return;
                    // 不等待慢MES请求，后续轮询仍能绑定另一件的新文件。
                    _ = TickAsync();
                }
            }
            catch (OperationCanceledException) { }
        }
        public void Stop() { lock (sync) stopped = true; }

        public async Task AbortAsync()
        {
            MeshinaJob[] cancelled;
            Task[] pending;
            lock (sync)
            {
                aborting = true;
                cancelled = jobs.ToArray();
                foreach (var job in cancelled) job.AbortRequested = true;
                pending = operations.Values.Select(c => c.Task).ToArray();
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
            lock (sync)
            {
                foreach (var job in cancelled)
                {
                    job.Stage = MeshinaStage.Cancelled;
                    LastFinished = job;
                    jobs.Remove(job);
                }
                Array.Clear(displayJobs, 0, displayJobs.Length);
                LastFinished = null;
                poller.Reset();
                aborting = false;
                Report("全部任务已终止。请确保旧件不再延迟保存MDB，再扫描新件。");
            }
        }

        private void BindFiles(List<MdbFile> files)
        {
            foreach (var job in jobs)
            {
                if (job.AbortRequested || job.MdbPath != null) continue;
                // 进站尚未确认时不能让后面的任务越过它抢占文件。
                if (job.Stage != MeshinaStage.WaitingForMdb) break;
                var excluded = new HashSet<string>(job.BaselineFiles, StringComparer.OrdinalIgnoreCase);
                var file = files.Where(f => !excluded.Contains(f.Path) && !bindings.ContainsKey(f.Path) && f.CreatedUtc > job.ScanTimeUtc)
                    .OrderBy(f => f.CreatedUtc).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (file == null) break;
                bindings.Add(file.Path, job.SN);
                job.MdbPath = file.Path;
                job.MdbBoundUtc = utcNow();
                Report($"SN:{job.SN} 已绑定MDB:{Path.GetFileName(file.Path)}，等待1秒后尝试读取");
            }
        }

        public Task TickAsync()
        {
            lock (sync)
            {
                if (stopped || aborting || jobs.Count == 0) return Task.CompletedTask;
                try
                {
                    var files = poller.Snapshot();
                    BindFiles(files); // 所有文件先认领，再开始任何读取或网络请求。
                    ExpireWaitingJobs();
                    var pending = new List<Task>();
                    foreach (var job in jobs.ToArray())
                    {
                        if (operations.ContainsKey(job)) continue;
                        if (job.Stage == MeshinaStage.WaitingForMdb && job.MdbPath != null)
                        {
                            var file = files.FirstOrDefault(f => string.Equals(f.Path, job.MdbPath, StringComparison.OrdinalIgnoreCase));
                            if (file == null || utcNow() - job.MdbBoundUtc.Value < TimeSpan.FromSeconds(1)) continue;
                            pending.Add(StartOperation(job, () => Task.Run(() => ReadAndCheckOutAsync(job, file))));
                        }
                        else if (job.Stage == MeshinaStage.ReadyForCheckOut)
                            pending.Add(StartOperation(job, () => SendCheckOutAsync(job)));
                    }
                    return Task.WhenAll(pending);
                }
                catch (Exception ex) { Report("MDB轮询失败：" + ex.Message); ExpireWaitingJobs(); return Task.CompletedTask; }
            }
        }

        private async Task ReadAndCheckOutAsync(MeshinaJob job, MdbFile file)
        {
            var measurement = reader.Read(file.Path);
            lock (sync)
            {
                if (stopped || job.AbortRequested) return;
                job.Measurement = measurement;
                job.CheckOutRequest.SendTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
                job.CheckOutRequest.SNInfo = new[] { new SNInfo
                {
                    SN = job.SN, Result = "PASS", CompList = Array.Empty<CompList>(),
                    DC_Info = itemNames.Select(field => new DC_Info
                    {
                        Item = field.Value,
                        Value = measurement.Values[field.Key].ToString(CultureInfo.InvariantCulture), Result = "PASS"
                    }).ToArray()
                } };
                job.Stage = MeshinaStage.ReadyForCheckOut;
            }
            await SendCheckOutAsync(job).ConfigureAwait(false);
        }

        public Task RetryAsync() => Task.CompletedTask;

        private void ExpireWaitingJobs()
        {
            foreach (var job in jobs.Where(j => j.Stage == MeshinaStage.WaitingForMdb && j.MdbPath == null &&
                j.FeedingAcceptedUtc.HasValue && utcNow() - j.FeedingAcceptedUtc.Value >= TimeSpan.FromSeconds(j.MdbWaitTimeoutSeconds)).ToArray())
            {
                job.Stage = MeshinaStage.MdbTimedOut;
                job.Message = $"超过{job.MdbWaitTimeoutSeconds}秒未检测到MDB，超时重扫";
                jobs.Remove(job); LastFinished = job;
                Report($"SN:{job.SN} {job.Message}");
            }
        }

        private static MeshinaMesReply TreatUnknownAsNg(MeshinaMesReply reply) => reply?.Outcome == MeshinaMesOutcome.Unknown || reply == null
            ? new MeshinaMesReply { Outcome = MeshinaMesOutcome.Rejected, Message = "结果未知，按NG处理，请重新啮合。" + reply?.Message }
            : reply;

        private async Task SendFeedingCheckAsync(MeshinaJob job)
        {
            lock (sync)
            {
                if (stopped || job.AbortRequested) return;
                job.Stage = MeshinaStage.FeedingCheckSending;
                Report($"SN:{job.SN} 正在请求MES FeedingCheck，请等待进站OK后测量");
            }
            MeshinaMesReply reply;
            try { reply = await gateway.FeedingCheckAsync(job.FeedingCheckRequest).ConfigureAwait(false); }
            catch (Exception ex) { reply = new MeshinaMesReply { Outcome = MeshinaMesOutcome.Unknown, Message = ex.Message }; }
            lock (sync)
            {
                reply = TreatUnknownAsNg(reply);
                job.FeedingReply = reply;
                if (job.AbortRequested) return;
                job.Stage = reply.Outcome == MeshinaMesOutcome.Accepted ? MeshinaStage.WaitingForMdb : MeshinaStage.FeedingCheckRejected;
                job.Message = reply.Message;
                if (reply.Outcome == MeshinaMesOutcome.Accepted) job.FeedingAcceptedUtc = utcNow();
                else { jobs.Remove(job); LastFinished = job; }
                Report($"SN:{job.SN} " + (reply.Outcome == MeshinaMesOutcome.Accepted ? "进站OK，可以开始啮合，等待MDB。" : "进站NG，本次任务已释放，请重新扫码。") + reply.Message);
            }
        }

        private async Task SendCheckOutAsync(MeshinaJob job)
        {
            lock (sync)
            {
                if (stopped || job.AbortRequested) return;
                // 先落盘；写入失败时停留ReadyForCheckOut，不发送未记录的数据。
                string dir = settings.CheckoutLogDirectory;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd") + ".txt"),
                    JsonConvert.SerializeObject(new { SN = job.SN, MDB = job.MdbPath, Data = job.CheckOutRequest.SNInfo }) + Environment.NewLine);
                job.Stage = MeshinaStage.CheckOutSending;
                Report($"SN:{job.SN} 正在自动出站");
            }
            MeshinaMesReply reply;
            try { reply = await gateway.CheckOutAsync(job.CheckOutRequest).ConfigureAwait(false); }
            catch (Exception ex) { reply = new MeshinaMesReply { Outcome = MeshinaMesOutcome.Unknown, Message = ex.Message }; }
            lock (sync)
            {
                reply = TreatUnknownAsNg(reply);
                job.CheckoutReply = reply;
                if (job.AbortRequested) return;
                job.Message = reply.Message;
                job.Stage = MeshinaStage.Completed;
                jobs.Remove(job); LastFinished = job;
                poller.Reset(job.MdbPath);
                Report($"SN:{job.SN} 出站{(reply.Outcome == MeshinaMesOutcome.Accepted ? "OK" : "NG")}，本次任务已结束。{reply.Message}");
            }
        }
        private void Report(string message) { status = message; log(message); }
    }
}
