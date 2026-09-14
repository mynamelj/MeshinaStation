using System.IO;
using System.IO.Ports;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace MeshinaStandalone
{
    public class Group<T> { public List<T> ListGroup { get; set; } = new List<T>(); }
    public class StationList { public List<Station> numberGroups { get; set; } = new List<Station>(); }
    public class Station
    {
        public int Number { get; set; }
        public string Name { get; set; }
        public override string ToString() => Number + " · " + Name;
    }
    public class SystemSettings
    {
        public int StationNumber { get; set; }
        public string Line { get; set; }
        public string StationID { get; set; }
        public string MachineID { get; set; }
        public string OPID { get; set; } = "";
        public string Token { get; set; } = "";
        public string FixSN { get; set; } = "";
        public string Mold { get; set; } = "";
        public string RegexRule { get; set; } = "";
        public int SNCodeLen { get; set; }
        public bool IsSimulate { get; set; }
    }
    public class ApiSettings
    {
        public int StationNumber { get; set; }
        public string BaseUrl { get; set; }
        public string FeedingCheck { get; set; }
        public string CheckOutApi { get; set; }
        public Uri Endpoint(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("MES接口路径不能为空。");
            if (!Uri.TryCreate(BaseUrl?.TrimEnd('/') + "/", UriKind.Absolute, out var root) ||
                (root.Scheme != "http" && root.Scheme != "https")) throw new InvalidDataException("BaseUrl必须是HTTP/HTTPS地址。");
            var uri = new Uri(root, path.TrimStart('/'));
            if (uri.Scheme != "http" && uri.Scheme != "https") throw new InvalidDataException("MES接口必须是HTTP/HTTPS地址。");
            return uri;
        }
    }
    public class ScannerSettings
    {
        public int Com { get; set; }
        public string BaudRate { get; set; } = "9600";
        public string Parity { get; set; } = "None";
        public string DataBits { get; set; } = "8";
        public string StopBits { get; set; } = "One";
        public string EndLine { get; set; } = "\\r\\n";
        public string Terminator => (EndLine ?? "").Replace("\\r", "\r").Replace("\\n", "\n");
        public SerialPort CreatePort()
        {
            if (Com < 1 || Terminator.Length == 0) throw new InvalidDataException("扫码串口号和结束符不能为空。");
            return new SerialPort("COM" + Com, int.Parse(BaudRate), (Parity)Enum.Parse(typeof(Parity), Parity), int.Parse(DataBits), (StopBits)Enum.Parse(typeof(StopBits), StopBits));
        }
    }
    public sealed class MeshinaSettings
    {
        public string DataDirectory { get; set; } = @"D:\齿轮双面啮合仪检测系统\统计";
        public string Provider { get; set; } = "Microsoft.Jet.OLEDB.4.0";
        public int PollIntervalMilliseconds { get; set; } = 1000;
        public int StablePollCount { get; set; } = 3;
        public int StationNumber { get; set; } = 1;
        public int ScannerIndex { get; set; }
        public Dictionary<string, string> ItemNames { get; set; } = new Dictionary<string, string>
        { ["Fi"] = "Outshaft_ToothFi1", ["fii"] = "Outshaft_ToothFi2", ["Fr"] = "Outshaft_ToothFi3" };
    }
    public sealed class Configuration
    {
        public static readonly string[] FileNames = { "sys.json", "stationNumber.json", "scan.json", "api.json", "meshina.json" };
        public List<Station> Stations { get; private set; }
        public List<SystemSettings> Systems { get; private set; }
        public List<ApiSettings> Apis { get; private set; }
        public List<ScannerSettings> Scanners { get; private set; }
        public MeshinaSettings Meshina { get; private set; }
        public static Configuration Load(string directory) => Parse(FileNames.ToDictionary(n => n, n => File.ReadAllText(Path.Combine(directory, n))));
        public static Configuration Parse(Dictionary<string, string> files)
        {
            T Read<T>(string n) where T : class => JsonConvert.DeserializeObject<T>(files[n]) ?? throw new InvalidDataException(n + "不能为null。");
            var config = new Configuration
            {
                Stations = Read<StationList>("stationNumber.json").numberGroups,
                Systems = Read<Group<SystemSettings>>("sys.json").ListGroup,
                Apis = Read<Group<ApiSettings>>("api.json").ListGroup,
                Scanners = Read<List<ScannerSettings>>("scan.json"),
                Meshina = Read<MeshinaSettings>("meshina.json")
            };
            if (config.Stations == null || config.Stations.Count == 0 || config.Stations.Any(s => s == null || s.Number < 1 || string.IsNullOrWhiteSpace(s.Name)) ||
                config.Stations.Select(s => s.Number).Distinct().Count() != config.Stations.Count)
                throw new InvalidDataException("stationNumber.json工位编号必须唯一且大于0，名称不能为空。");
            if (config.Systems == null || config.Apis == null || config.Systems.Any(s => s == null) || config.Apis.Any(a => a == null)) throw new InvalidDataException("sys/api的ListGroup不能为空或包含null。");
            var m = config.Meshina;
            if (m.PollIntervalMilliseconds < 200 || m.StablePollCount < 2 || string.IsNullOrWhiteSpace(m.DataDirectory) || !Path.IsPathRooted(m.DataDirectory) || string.IsNullOrWhiteSpace(m.Provider))
                throw new InvalidDataException("meshina.json需配置MDB绝对目录、驱动、至少200ms轮询和至少2次稳定检查。");
            if (m.ItemNames == null || MdbReader.NumericFields.Any(f => !m.ItemNames.ContainsKey(f) || string.IsNullOrWhiteSpace(m.ItemNames[f])) || m.ItemNames.Values.Distinct().Count() != m.ItemNames.Count)
                throw new InvalidDataException("Fi、fii、Fr必须配置不同的MES项目名。");
            config.Resolve(m.StationNumber);
            return config;
        }
        public (Station station, SystemSettings system, ApiSettings api) Resolve(int number)
        {
            var station = Stations.SingleOrDefault(s => s.Number == number) ?? throw new InvalidDataException("找不到工位编号：" + number);
            var sys = Systems.Where(s => s.StationNumber == number).ToList();
            var api = Apis.Where(a => a.StationNumber == number).ToList();
            if (sys.Count != 1 || api.Count != 1) throw new InvalidDataException("sys.json和api.json必须各有一条StationNumber=" + number + "的配置。");
            if (string.IsNullOrWhiteSpace(sys[0].Line) || string.IsNullOrWhiteSpace(sys[0].StationID) || string.IsNullOrWhiteSpace(sys[0].MachineID)) throw new InvalidDataException("Line、StationID、MachineID不能为空。");
            if (!string.IsNullOrEmpty(sys[0].RegexRule)) _ = new Regex(sys[0].RegexRule, RegexOptions.None, TimeSpan.FromSeconds(1));
            _ = api[0].Endpoint(api[0].FeedingCheck); _ = api[0].Endpoint(api[0].CheckOutApi);
            // 所有啮合工位统一使用串口扫码。
            {
                if (Meshina.ScannerIndex < 0 || Meshina.ScannerIndex >= Scanners.Count || Scanners[Meshina.ScannerIndex] == null) throw new InvalidDataException("ScannerIndex未对应scan.json中的扫码枪（从0开始）。");
                using var port = Scanners[Meshina.ScannerIndex].CreatePort();
            }
            return (station, sys[0], api[0]);
        }
        public static MeshinaJob CreateJob(SystemSettings sys, ApiSettings api, string opid) => new MeshinaJob
        {
            FeedingCheckRequest = new FeedingCheckModel { Line = sys.Line, StationID = sys.StationID, MachineID = sys.MachineID, OPID = opid,
                Token = sys.Token ?? "", FixSN = sys.FixSN ?? "", EventID = api.Endpoint(api.FeedingCheck).AbsolutePath.TrimEnd('/').Split('/').Last() },
            CheckOutRequest = new SNCheckoutModel { Line = sys.Line, StationID = sys.StationID, MachineID = sys.MachineID, OPID = opid,
                Token = sys.Token ?? "", FixSN = sys.FixSN ?? "", Mold = sys.Mold ?? "", EventID = api.Endpoint(api.CheckOutApi).AbsolutePath.TrimEnd('/').Split('/').Last() }
        };
    }
}
