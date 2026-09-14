using System.Globalization;
using System.IO;
using System.Threading;


namespace MeshinaStandalone
{
    /// <summary>单工位扫码进站、读取新增MDB、自动出站。</summary>
    public sealed class MeshinaStationService
    {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly MeshinaSettings settings;
        private readonly MdbPoller poller;
        private readonly IMdbReader reader;
        private readonly IMeshinaMesGateway gateway;
        private readonly Action<string> log;
        private volatile bool stopped;
        private string status = "等待扫码进站";
        public string Status => status;
        public MeshinaJob Current { get; private set; }
        public MeshinaJob LastFinished { get; private set; }
        public bool CanAbort => Current != null && !Current.AbortRequested;
        public bool CanRetry => Current != null && !Current.AbortRequested &&
            (Current.Stage == MeshinaStage.FeedingCheckRejected || Current.Stage == MeshinaStage.CheckOutRejected);

        public MeshinaStationService(MeshinaSettings settings, MdbPoller poller, IMdbReader reader,
            IMeshinaMesGateway gateway, Action<string> log)
        {
            this.settings = settings; this.poller = poller; this.reader = reader;
            this.gateway = gateway; this.log = log;
        }

        public async Task ScanAsync(string sn, Func<MeshinaJob> createRequest, CancellationToken token = default)
        {
            if (!await gate.WaitAsync(0, token)) { log("工位正在处理任务，本次扫码未接收"); return; }
            try
            {
                if (stopped) { log("啮合站未就绪，本次扫码未接收"); return; }
                if (Current != null) { log($"当前SN:{Current.SN}尚未完成，本次扫码{sn}未接收"); return; }
                if (string.IsNullOrWhiteSpace(sn)) { log("扫码为空，未进站"); return; }
                // 在请求MES前采集基线，进站接口响应期间生成的文件不会丢失。
                DateTime scanTime = DateTime.UtcNow;
                var baseline = poller.Snapshot();
                var job = createRequest();
                job.SN = sn.Trim(); job.ScanTimeUtc = scanTime;
                job.BaselineFiles = baseline.Select(f => f.Path).ToList();
                job.FeedingCheckRequest.SN = job.SN;
                job.Stage = MeshinaStage.FeedingCheckSending;
                Current = job;
                poller.Reset();
                await SendFeedingCheckAsync();
            }
            catch (Exception ex) { Report("扫码进站未完成：" + ex.Message); }
            finally { gate.Release(); }
        }

        public async Task RunAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && !stopped)
                {
                    await Task.Delay(settings.PollIntervalMilliseconds, token);
                    await TickAsync();
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Stop() { stopped = true; }

        public async Task AbortAsync()
        {
            var job = Current;
            if (job == null) return;
            // 先停止后续步骤，再等当前读文件/MES调用返回，避免旧回调影响下一件。
            job.AbortRequested = true;
            await gate.WaitAsync();
            try
            {
                if (Current != job) return;
                job.Stage = MeshinaStage.Cancelled;
                LastFinished = job;
                Current = null;
                poller.Reset();
                Report($"SN:{job.SN} 本次流程已终止，可以扫描新齿轮。请确保旧件不再保存检测文件。");
            }
            finally { gate.Release(); }
        }

        public async Task TickAsync()
        {
            if (!await gate.WaitAsync(0)) return;
            try
            {
                if (stopped || Current == null || Current.AbortRequested) return;
                if (Current.Stage == MeshinaStage.WaitingForMdb)
                {
                    var files = poller.Candidates(Current);
                    if (files.Count == 0) return;
                    var file = files[0];
                    if (!poller.IsStable(file, settings.StablePollCount)) return;
                    var measurement = reader.Read(file.Path);
                    if (Current.AbortRequested) return;
                    var after = poller.Candidates(Current);
                    if (after.Count == 0 || after[0].Path != file.Path || after[0].Stamp != file.Stamp)
                    { poller.Reset(); return; }
                    Current.MdbPath = file.Path;
                    Current.Measurement = measurement;
                    Current.CheckOutRequest.SendTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
                    Current.CheckOutRequest.SNInfo = new[] { new SNInfo
                    {
                        SN = Current.SN,
                        Result = "PASS", // 本期不使用MDB.Result，沿用数值采集的PASS规则。
                        CompList = Array.Empty<CompList>(),
                        DC_Info = MdbReader.NumericFields.Select(field => new KeyValuePair<string, decimal>(field, measurement.Values[field])).Select(pair => new DC_Info
                        {
                            Item = settings.ItemNames.TryGetValue(pair.Key, out string name) && !string.IsNullOrWhiteSpace(name) ? name : pair.Key,
                            Value = pair.Value.ToString(CultureInfo.InvariantCulture), Result = "PASS"
                        }).ToArray()
                    } };
                    Current.Stage = MeshinaStage.ReadyForCheckOut;
                    Report($"SN:{Current.SN} 已绑定MDB:{Path.GetFileName(file.Path)}，准备自动出站");
                }
                if (Current?.Stage == MeshinaStage.ReadyForCheckOut && !stopped && !Current.AbortRequested) await SendCheckOutAsync();
            }
            catch (Exception ex) { Report($"SN:{Current?.SN} 暂停处理：{ex.Message}"); }
            finally { gate.Release(); }
        }

        public async Task RetryAsync()
        {
            await gate.WaitAsync();
            try
            {
                if (stopped || !CanRetry) return;
                if (Current.Stage == MeshinaStage.FeedingCheckRejected) await SendFeedingCheckAsync();
                else await SendCheckOutAsync();
            }
            catch (Exception ex) { Report("重试未完成：" + ex.Message); }
            finally { gate.Release(); }
        }

        private async Task SendFeedingCheckAsync()
        {
            Current.Stage = MeshinaStage.FeedingCheckSending;
            Report($"SN:{Current.SN} 正在请求MES FeedingCheck，请等待进站OK后测量");
            MeshinaMesReply reply;
            try { reply = await gateway.FeedingCheckAsync(Current.FeedingCheckRequest); }
            catch (Exception ex) { reply = new MeshinaMesReply { Outcome = MeshinaMesOutcome.Unknown, Message = ex.Message }; }
            Current.FeedingReply = reply;
            if (Current.AbortRequested) return;
            Current.Stage = reply.Outcome == MeshinaMesOutcome.Accepted ? MeshinaStage.WaitingForMdb
                : MeshinaStage.FeedingCheckRejected;
            Current.Message = reply.Message;
            Report($"SN:{Current.SN} " + (reply.Outcome == MeshinaMesOutcome.Accepted ? "进站OK，可以开始啮合，等待MDB。"
                : reply.Outcome == MeshinaMesOutcome.Rejected ? "FeedingCheck失败，可重试原任务。" : "FeedingCheck结果未知，请核实MES记录后再重试。") + reply.Message);
        }

        private async Task SendCheckOutAsync()
        {
            Current.Stage = MeshinaStage.CheckOutSending;
            Report($"SN:{Current.SN} 正在自动出站");
            MeshinaMesReply reply;
            try { reply = await gateway.CheckOutAsync(Current.CheckOutRequest); }
            catch (Exception ex) { reply = new MeshinaMesReply { Outcome = MeshinaMesOutcome.Unknown, Message = ex.Message }; }
            Current.CheckoutReply = reply;
            if (Current.AbortRequested)
            {
                log($"SN:{Current.SN} 终止期间MES出站返回:{reply.Outcome}，{reply.Message}；已发送请求无法撤回。");
                return;
            }
            Current.Message = reply.Message;
            if (reply.Outcome == MeshinaMesOutcome.Accepted) { Finish(); return; }
            Current.Stage = MeshinaStage.CheckOutRejected;
            Report($"SN:{Current.SN} " + (reply.Outcome == MeshinaMesOutcome.Rejected
                ? "MES出站失败，数据已保留，可重试原任务。" : "出站结果未知，原数据已保留，请核实MES记录后再重试。") + reply.Message);
        }

        private void Finish()
        {
            Current.Stage = MeshinaStage.Completed;
            LastFinished = Current;
            Current = null;
            poller.Reset();
            Report($"SN:{LastFinished.SN} 出站OK，可以扫描下一件；MDB:{LastFinished.MdbPath}");
        }

        private void Report(string message)
        {
            if (status == message) return;
            status = message;
            log(message);
        }
    }
}
