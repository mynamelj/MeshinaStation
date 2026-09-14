using MeshinaStandalone;
using System.IO;
using Newtonsoft.Json.Linq;

internal static class Program
{
    static int checks;
    static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
    static MeshinaJob Request() => new MeshinaJob { FeedingCheckRequest = new FeedingCheckModel(), CheckOutRequest = new SNCheckoutModel() };
    static async Task Main()
    {
        await PriorityAndTimeout();
        await ConfiguredFields();
        await QueueAndBinding();
        await SlowRequestsAndAbort();
        await UnknownAndReadFailure();
        await ImmediateReadAndLogFailure();
        await MissingBindingAndPendingAbort();
        Console.WriteLine($"PASS: {checks} assertions");
    }
    static async Task PriorityAndTimeout()
    {
        var f = new Fixture();
        f.Gateway.InOutcome = MeshinaMesOutcome.Rejected;
        await f.Service.ScanAsync("FAIL", Request);
        f.Gateway.InOutcome = MeshinaMesOutcome.Accepted;
        await f.Service.ScanAsync("A", Request);
        Check(f.Service.DisplayJobs[0].SN == "A" && f.Service.DisplayJobs[1] == null, "feeding NG next scan still uses slot one");
        var a = f.File("a"); await f.Service.TickAsync();
        await f.Service.ScanAsync("B", Request);
        f.Advance(1); await f.Service.TickAsync();
        Check(f.Service.TaskEnabled[0] && !f.Service.TaskEnabled[1], "finished slot one enabled while slot two occupied");
        await f.Service.ScanAsync("C", Request);
        Check(f.Service.DisplayJobs[0].SN == "C" && f.Service.DisplayJobs[1].SN == "B", "new task takes free slot one");
        var b = f.File("b"); await f.Service.TickAsync();
        Check(f.Service.DisplayJobs[1].MdbPath == b && f.Service.DisplayJobs[0].MdbPath == null, "older slot two binds before newer slot one");
        await f.Service.AbortAsync();
        Check(f.Service.TaskEnabled.All(e => e) && f.Service.DisplayJobs.All(j => j == null), "abort enables and clears both slots");

        var g = new Fixture();
        g.Gateway.InWait = new TaskCompletionSource<bool>();
        var scan = g.Service.ScanAsync("WAIT", Request);
        g.Advance(150); await g.Service.TickAsync();
        Check(!g.Service.TaskEnabled[0], "feeding request reserves slot and does not start MDB timer");
        g.Gateway.InWait.SetResult(true); await scan;
        g.Advance(99); await g.Service.TickAsync();
        Check(!g.Service.TaskEnabled[0], "not timed out at 99 seconds after accepted reply");
        g.Advance(1); await g.Service.TickAsync();
        Check(g.Service.TaskEnabled[0] && g.Service.DisplayJobs[0].Stage == MeshinaStage.MdbTimedOut, "default 100 seconds expires unbound task");
        await g.Service.ScanAsync("WAIT", Request);
        Check(g.Service.DisplayJobs[0].Stage == MeshinaStage.WaitingForMdb, "timeout permits same SN in slot one");

        var h = new Fixture(timeout: 5);
        await h.Service.ScanAsync("SHORT", Request);
        h.Advance(5); await h.Service.TickAsync();
        Check(h.Service.Current == null, "custom timeout is used");
        await h.Service.ScanAsync("BOUND", Request);
        var bound = h.File("bound"); h.Reader.FailPath = bound;
        await h.Service.TickAsync(); h.Advance(10); await h.Service.TickAsync();
        Check(!h.Service.TaskEnabled[0] && h.Service.Current.MdbPath == bound, "bound but unreadable MDB does not release slot");
        var both = new Fixture(timeout: 5);
        await both.Service.ScanAsync("ONE", Request); both.Advance(2);
        await both.Service.ScanAsync("TWO", Request); both.Advance(3); await both.Service.TickAsync();
        Check(both.Service.TaskEnabled[0] && !both.Service.TaskEnabled[1], "timers independent per task");
        both.Advance(2); await both.Service.TickAsync();
        Check(both.Service.TaskEnabled.All(e => e), "second timer expires independently");
    }
    static async Task ConfiguredFields()
    {
        var single = Newtonsoft.Json.JsonConvert.DeserializeObject<MeshinaSettings>("{\"ItemNames\":{\"CustomValue\":\"MES_Custom\"}}");
        single.ValidateItemNames();
        Check(single.ItemNames.Count == 1 && single.ItemNames.ContainsKey("CustomValue"), "explicit configuration replaces default three fields");
        foreach (var invalid in new[] {
            new Dictionary<string, string>(), new Dictionary<string, string> { ["Fi"] = "" },
            new Dictionary<string, string> { ["Fi"] = "same", ["Fr"] = "same" },
            new Dictionary<string, string> { ["Fi"] = "one", ["fi"] = "two" },
            new Dictionary<string, string> { ["bad]field"] = "one" } })
        {
            bool rejected = false;
            try { new MeshinaSettings { ItemNames = invalid }.ValidateItemNames(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "invalid field mapping rejected");
        }
        var names = new[] { "Fi", "fii", "Fr", "Fi1", "fii1", "Fr1" };
        var mappings = names.Zip(new[] { "Midshaft_UptoothFi1", "Midshaft_UptoothFi2", "Midshaft_UptoothFr3",
            "Midshaft_DowntoothFi1", "Midshaft_DowntoothFi2", "Midshaft_DowntoothFr3" }, (key, value) => new { key, value })
            .ToDictionary(p => p.key, p => p.value);
        var actualReader = new MdbReader("Microsoft.Jet.OLEDB.4.0", mappings.Keys);
        var f = new Fixture(mappings, actualReader);
        await f.Service.ScanAsync("SIX-FIELDS", Request);
        string path = Path.Combine(f.Dir, "six.mdb");
        string connectionString = "Provider=Microsoft.Jet.OLEDB.4.0;Data Source=" + path;
        dynamic catalog = Activator.CreateInstance(Type.GetTypeFromProgID("ADOX.Catalog", true));
        try { catalog.Create(connectionString); }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(catalog); }
        void Execute(string sql)
        {
            using var connection = new System.Data.OleDb.OleDbConnection(connectionString);
            connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
        Execute("CREATE TABLE TJSHEET ([Fi] DOUBLE,[fii] DOUBLE,[Fr] DOUBLE,[Fi1] DOUBLE,[fii1] DOUBLE,[Fr1] DOUBLE,[Extra7] DOUBLE,[Result] TEXT(20))");
        Execute("INSERT INTO TJSHEET VALUES (1.25,2.25,3.25,4.25,5.25,6.25,7.25,'FAIL')");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        await f.Service.TickAsync(); f.Advance(1); await f.Service.TickAsync();
        var data = f.Gateway.Out.Single().SNInfo.Single().DC_Info;
        Check(data.Select(d => d.Item).SequenceEqual(mappings.Values), "real MDB uploads all six MES mappings");
        Check(data.Select(d => d.Value).SequenceEqual(new[] { "1.25", "2.25", "3.25", "4.25", "5.25", "6.25" }), "real MDB preserves six distinct values");
        Check(f.Service.DisplayJobs[0].Measurement.Values.Count == 6, "display task retains six fields");
        var logged = JObject.Parse(File.ReadAllLines(Directory.GetFiles(f.Log, "*.txt").Single()).Single());
        Check(logged["Data"][0]["DC_Info"].Count() == 6, "txt includes all six uploaded fields");
        Check(new MdbReader("Microsoft.Jet.OLEDB.4.0", names.Concat(new[] { "Extra7" })).Read(path).Values.Count == 7, "reader supports more than six fields");
        Check(new MdbReader("Microsoft.Jet.OLEDB.4.0", new[] { "Extra7" }).Read(path).Values.Single().Value == 7.25m, "reader supports arbitrary configured subset");
        Execute("UPDATE TJSHEET SET [Fr1]=NULL");
        bool incomplete = false;
        try { actualReader.Read(path); } catch (InvalidDataException) { incomplete = true; }
        Check(incomplete, "null in sixth field cannot produce partial checkout");
        bool missing = false;
        try { new MdbReader("Microsoft.Jet.OLEDB.4.0", new[] { "MissingField" }).Read(path); }
        catch (System.Data.OleDb.OleDbException) { missing = true; }
        Check(missing, "missing configured column is not silently omitted");
    }
    static async Task QueueAndBinding()
    {
        var f = new Fixture();
        f.File("baseline");
        await f.Service.ScanAsync(" A ", Request);
        await f.Service.ScanAsync("A", Request);
        Check(f.Gateway.In.Count == 1, "duplicate rejected");
        var first = f.File("first");
        await f.Service.TickAsync();
        Check(f.Service.Current.MdbPath == first && f.Reader.Paths.Count == 0, "bind before read");
        await f.Service.ScanAsync("B", Request);
        await f.Service.ScanAsync("C", Request);
        Check(f.Service.Jobs.Length == 2 && f.Gateway.In.Count == 2, "capacity two");
        Check(f.Service.DisplayJobs[0].SN == "A" && f.Service.DisplayJobs[1].SN == "B", "two display slots retain both tasks");
        var second = f.File("second");
        await f.Service.TickAsync();
        Check(f.Service.Jobs[1].MdbPath == second && f.Service.Jobs[0].MdbPath == first, "exclusive binding");
        f.Advance(0.999); await f.Service.TickAsync();
        Check(f.Reader.Paths.Count == 0, "no read before one second");
        f.Gateway.Outcome = MeshinaMesOutcome.Rejected;
        f.Advance(0.001); await f.Service.TickAsync();
        Check(f.Service.Jobs.Length == 0 && f.Gateway.Out.Count == 2, "NG completes both");
        Check(f.Service.DisplayJobs.All(j => j.Stage == MeshinaStage.Completed && j.CheckoutReply.Outcome == MeshinaMesOutcome.Rejected), "both completed results stay visible");
        Check(f.Gateway.Out.All(r => r.SNInfo.Single().DC_Info.Length == 3), "three checkout fields");
        Check(f.Gateway.Out.Single(r => r.SNInfo[0].SN == "A").SNInfo[0].DC_Info.All(d => d.Value == "1.23") &&
            f.Gateway.Out.Single(r => r.SNInfo[0].SN == "B").SNInfo[0].DC_Info.All(d => d.Value == "2.34"), "different MDB values follow their own SN");
        var records = Directory.GetFiles(f.Log, "*.txt").SelectMany(File.ReadAllLines).Select(JObject.Parse).ToArray();
        Check(records.Length == 2 && records.Single(r => (string)r["SN"] == "A")["MDB"].ToString() == first &&
            records.Single(r => (string)r["SN"] == "B")["MDB"].ToString() == second, "txt records preserve bindings");
        Check(records.All(r => r.Properties().Count() == 3 && r["Data"][0]["DC_Info"].Count() == 3), "log only SN MDB data");
        await f.Service.ScanAsync("A", Request);
        await f.Service.TickAsync(); f.Advance(1); await f.Service.TickAsync();
        Check(f.Gateway.Out.Count == 2, "cannot reuse old MDB");
        var third = f.File("third"); await f.Service.TickAsync(); f.Advance(1); await f.Service.TickAsync();
        Check(f.Gateway.Out.Count == 3 && f.Service.LastFinished.MdbPath == third, "same SN new task new MDB");
    }
    static async Task SlowRequestsAndAbort()
    {
        var f = new Fixture();
        f.Gateway.InWait = new TaskCompletionSource<bool>();
        var scan = f.Service.ScanAsync("A", Request);
        var scan2 = f.Service.ScanAsync("B", Request);
        await f.Service.ScanAsync("A", Request);
        Check(f.Gateway.In.Count == 2, "concurrent feeding checks and duplicate guard");
        var a = f.File("a"); var b = f.File("b");
        await f.Service.TickAsync();
        Check(f.Service.Jobs.All(j => j.MdbPath == null), "no binding before feeding accepted");
        f.Gateway.InWait.SetResult(true); await Task.WhenAll(scan, scan2);
        await f.Service.TickAsync();
        Check(f.Service.Jobs[0].MdbPath == a && f.Service.Jobs[1].MdbPath == b, "two files arriving together assigned FIFO");
        await f.Service.AbortAsync();
        Check(f.Service.Jobs.Length == 0 && f.Service.LastFinished == null && f.Service.DisplayJobs.All(j => j == null), "abort all queued");
        await f.Service.ScanAsync("A", Request);
        var c = f.File("c"); await f.Service.TickAsync(); f.Advance(1);
        f.Gateway.OutWait = new TaskCompletionSource<bool>();
        var tick = f.Service.TickAsync();
        await f.Gateway.OutStarted.Task;
        await f.Service.ScanAsync("B", Request);
        Check(f.Service.Jobs.Length == 2, "scan while checkout in flight");
        var d = f.File("d"); await f.Service.TickAsync();
        Check(f.Service.Jobs[1].MdbPath == d, "bind while another MES call is pending");
        var abort = f.Service.AbortAsync();
        Check(!abort.IsCompleted && !f.Service.CanScan && !f.Service.CanRetry, "abort blocks until pending requests complete");
        await f.Service.ScanAsync("C", Request);
        f.Gateway.OutWait.SetResult(true); await tick; await abort;
        Check(f.Service.Jobs.Length == 0 && f.Service.LastFinished == null && f.Service.DisplayJobs.All(j => j == null), "late checkout cannot revive aborted tasks");
        f.Service.Stop(); await f.Service.ScanAsync("C", Request);
        Check(f.Service.Jobs.Length == 0, "stopped refuses scan");
    }
    static async Task UnknownAndReadFailure()
    {
        var f = new Fixture();
        await f.Service.ScanAsync("A", Request); var a = f.File("a");
        await f.Service.TickAsync();
        await f.Service.ScanAsync("B", Request); var b = f.File("b");
        f.Reader.FailPath = a;
        await f.Service.TickAsync(); f.Advance(1); await f.Service.TickAsync();
        Check(f.Service.Jobs.Length == 1 && f.Service.Current.MdbPath == a && f.Gateway.Out.Single().SNInfo[0].SN == "B", "bad first MDB does not block or steal second");
        var active = f.Service.Current;
        Check(f.Service.DisplayJobs[0] == active && f.Service.DisplayJobs[1].Stage == MeshinaStage.Completed, "active and finished results remain in their own slots");
        f.Reader.FailPath = null; f.Gateway.Outcome = MeshinaMesOutcome.Unknown;
        await f.Service.TickAsync();
        Check(!f.Service.CanRetry && f.Service.Current == null && f.Service.LastFinished.CheckoutReply.Outcome == MeshinaMesOutcome.Rejected, "unknown checkout ends as NG");
        int count = f.Gateway.Out.Count;
        await f.Service.TickAsync(); await f.Service.RetryAsync();
        Check(f.Gateway.Out.Count == count, "NG is not retried");
        f.Gateway.InOutcome = MeshinaMesOutcome.Rejected;
        await f.Service.ScanAsync("C", Request);
        Check(f.Service.Current == null && f.Service.TaskEnabled.All(e => e), "feeding NG immediately releases slot");
        f.Gateway.InOutcome = MeshinaMesOutcome.Unknown;
        await f.Service.ScanAsync("C", Request);
        Check(f.Service.DisplayJobs[0].FeedingReply.Outcome == MeshinaMesOutcome.Rejected && f.Service.Current == null, "unknown feeding ends as NG in slot one");
        f.Gateway.InOutcome = MeshinaMesOutcome.Accepted;
        await f.Service.ScanAsync("C", Request); f.File("c");
        await f.Service.TickAsync(); f.Advance(1); await f.Service.TickAsync();
        Check(f.Service.Current == null, "rescan after feeding NG completes normally");
    }
    static async Task ImmediateReadAndLogFailure()
    {
        var f = new Fixture(); await f.Service.ScanAsync("A", Request);
        string path = f.File("a"); await f.Service.TickAsync(); f.Advance(1);
        File.AppendAllText(path, "writing");
        f.Reader.AfterRead = p => File.AppendAllText(p, "changed during read");
        await f.Service.TickAsync();
        Check(f.Gateway.Out.Count == 1 && f.Service.Current == null, "readable data uploads at one second despite file changes");

        var g = new Fixture(); await g.Service.ScanAsync("B", Request);
        g.File("b"); await g.Service.TickAsync(); g.Advance(1);
        File.WriteAllText(g.Log, "blocks directory");
        await g.Service.TickAsync();
        Check(g.Gateway.Out.Count == 0 && g.Service.Current.Stage == MeshinaStage.ReadyForCheckOut, "log failure prevents unlogged checkout");
        File.Delete(g.Log); await g.Service.TickAsync();
        Check(g.Gateway.Out.Count == 1, "log recovery submits retained payload");
    }
    static async Task MissingBindingAndPendingAbort()
    {
        var f = new Fixture();
        f.Gateway.InWait = new TaskCompletionSource<bool>();
        var first = f.Service.ScanAsync("A", Request);
        var second = f.Service.ScanAsync("B", Request);
        var abort = f.Service.AbortAsync();
        Check(!abort.IsCompleted && !f.Service.CanAbort, "abort waits for both feeding requests");
        await f.Service.ScanAsync("C", Request);
        Check(f.Gateway.In.Count == 2, "abort refuses new feeding");
        f.Gateway.InWait.SetResult(true);
        await Task.WhenAll(first, second, abort);
        Check(f.Service.Jobs.Length == 0, "late feeding replies cannot resume either task");
        await f.Service.ScanAsync("A", Request);
        string a = f.File("a"); await f.Service.TickAsync();
        File.Delete(a);
        string extra = f.File("extra"); f.Advance(1); await f.Service.TickAsync();
        Check(f.Service.Current.MdbPath == a && f.Gateway.Out.Count == 0, "missing bound file cannot be replaced");
        await f.Service.AbortAsync();
        Check(f.Service.CanScan, "abort releases duplicate SN reservation");
    }
    sealed class Fixture
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "meshina-queue-tests", Guid.NewGuid().ToString("N"));
        public string Log => Path.Combine(Dir, "checkout");
        public readonly Gateway Gateway = new Gateway();
        public readonly Reader Reader = new Reader();
        public readonly MeshinaStationService Service;
        DateTime now = DateTime.UtcNow;
        public Fixture(Dictionary<string, string> items = null, IMdbReader mdbReader = null, int timeout = 100)
        {
            Directory.CreateDirectory(Dir);
            var settings = new MeshinaSettings { DataDirectory = Dir, CheckoutLogDirectory = Log, MdbWaitTimeoutSeconds = timeout };
            if (items != null) settings.ItemNames = items;
            Reader.Fields = settings.ItemNames.Keys.ToArray();
            Service = new MeshinaStationService(settings,
                new MdbPoller(Dir), mdbReader ?? Reader, Gateway, _ => { }, () => now);
        }
        public void Advance(double seconds) { now = now.AddSeconds(seconds); }
        public string File(string name)
        {
            var p = Path.Combine(Dir, name + ".mdb");
            System.IO.File.WriteAllText(p, "test");
            System.IO.File.SetCreationTimeUtc(p, now.AddMilliseconds(1)); return p;
        }
    }
    sealed class Reader : IMdbReader
    {
        public readonly List<string> Paths = new List<string>();
        public string[] Fields;
        public string FailPath;
        public Action<string> AfterRead;
        public MeshinaMeasurement Read(string path)
        {
            lock (Paths) Paths.Add(path);
            if (path == FailPath) throw new IOException("busy");
            AfterRead?.Invoke(path);
            return new MeshinaMeasurement { Values = Fields.ToDictionary(f => f, _ => Path.GetFileName(path) == "second.mdb" ? 2.34m : 1.23m), Result = "FAIL" };
        }
    }
    sealed class Gateway : IMeshinaMesGateway
    {
        public readonly List<FeedingCheckModel> In = new List<FeedingCheckModel>();
        public readonly List<SNCheckoutModel> Out = new List<SNCheckoutModel>();
        public MeshinaMesOutcome Outcome = MeshinaMesOutcome.Accepted, InOutcome = MeshinaMesOutcome.Accepted;
        public TaskCompletionSource<bool> InWait, OutWait;
        public readonly TaskCompletionSource<bool> OutStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<MeshinaMesReply> FeedingCheckAsync(FeedingCheckModel request)
        { lock (In) In.Add(request); if (InWait != null) await InWait.Task; return new MeshinaMesReply { Outcome = InOutcome }; }
        public async Task<MeshinaMesReply> CheckOutAsync(SNCheckoutModel request)
        { lock (Out) Out.Add(request); OutStarted.TrySetResult(true); if (OutWait != null) await OutWait.Task; return new MeshinaMesReply { Outcome = Outcome }; }
    }
}
