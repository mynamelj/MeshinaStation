using System.IO.Ports;

namespace MeshinaStandalone
{
    // 保留跨次接收的半包，按配置结束符逐条提交，禁止把多件SN拼接成一件。
    public sealed class BarcodeFramer
    {
        private string buffer = "";
        private readonly string terminator;
        public BarcodeFramer(string terminator)
        {
            if (string.IsNullOrEmpty(terminator)) throw new ArgumentException("结束符不能为空");
            this.terminator = terminator;
        }
        public List<string> Push(string text)
        {
            buffer += text;
            if (buffer.Length > 8192) { buffer = ""; throw new InvalidOperationException("扫码数据超过8192字符，已丢弃，请检查结束符。"); }
            var values = new List<string>();
            int index;
            while ((index = buffer.IndexOf(terminator, StringComparison.Ordinal)) >= 0)
            {
                string sn = buffer.Substring(0, index).Trim(); buffer = buffer.Substring(index + terminator.Length);
                if (sn.Length > 0) values.Add(sn);
            }
            return values;
        }
    }
    public sealed class SerialScanner : IDisposable
    {
        private readonly SerialPort port;
        private readonly BarcodeFramer framer;
        private readonly Action<string> scan, log;
        public SerialScanner(ScannerSettings settings, Action<string> scan, Action<string> log)
        {
            this.scan = scan; this.log = log; framer = new BarcodeFramer(settings.Terminator);
            port = settings.CreatePort(); port.DataReceived += Received;
        }
        public void Open() { port.Open(); log("扫码枪已连接 " + port.PortName); }
        private void Received(object sender, SerialDataReceivedEventArgs e)
        {
            try { foreach (string sn in framer.Push(port.ReadExisting())) scan(sn); }
            catch (Exception ex) { log("扫码枪接收失败：" + ex.Message); }
        }
        public void Dispose() { port.DataReceived -= Received; port.Dispose(); }
    }
}
