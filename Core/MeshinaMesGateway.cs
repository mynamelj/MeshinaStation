using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MeshinaStandalone
{
    public enum MeshinaMesOutcome { Accepted, Rejected, Unknown }
    public sealed class MeshinaMesReply
    {
        public MeshinaMesOutcome Outcome { get; set; }
        public string Message { get; set; }
    }
    public interface IMeshinaMesGateway
    {
        Task<MeshinaMesReply> FeedingCheckAsync(FeedingCheckModel request);
        Task<MeshinaMesReply> CheckOutAsync(SNCheckoutModel request);
    }
    public sealed class MeshinaMesGateway : IMeshinaMesGateway, IDisposable
    {
        private readonly HttpClient client;
        private readonly ApiSettings api;
        private readonly Action<string> log;
        public MeshinaMesGateway(ApiSettings api, Action<string> log, HttpMessageHandler handler = null)
        {
            this.api = api; this.log = log;
            client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(30);
        }
        public Task<MeshinaMesReply> FeedingCheckAsync(FeedingCheckModel request) => Send(api.FeedingCheck, request);
        public Task<MeshinaMesReply> CheckOutAsync(SNCheckoutModel request) => Send(api.CheckOutApi, request);
        private async Task<MeshinaMesReply> Send(string path, object request)
        {
            try
            {
                string json = JsonConvert.SerializeObject(request);
                var safe = JObject.Parse(json); safe["Token"] = "***";
                log("MES请求 " + path + " " + safe.ToString(Formatting.None));
                using var body = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(api.Endpoint(path), body).ConfigureAwait(false);
                string raw = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                log("MES响应 HTTP " + (int)response.StatusCode + " " + raw);
                if (!response.IsSuccessStatusCode) return new MeshinaMesReply { Outcome = MeshinaMesOutcome.Unknown, Message = "HTTP " + (int)response.StatusCode + " " + raw };
                var obj = JObject.Parse(raw);
                string result = obj.GetValue("Result", StringComparison.OrdinalIgnoreCase)?.ToString();
                return new MeshinaMesReply
                {
                    Outcome = string.Equals(result, "PASS", StringComparison.OrdinalIgnoreCase) ? MeshinaMesOutcome.Accepted :
                        string.Equals(result, "FAIL", StringComparison.OrdinalIgnoreCase) ? MeshinaMesOutcome.Rejected : MeshinaMesOutcome.Unknown,
                    Message = obj.GetValue("Msg", StringComparison.OrdinalIgnoreCase)?.ToString() ?? raw
                };
            }
            catch (Exception ex) { log("MES通讯异常 " + ex.Message); return new MeshinaMesReply { Outcome = MeshinaMesOutcome.Unknown, Message = ex.Message }; }
        }
        public void Dispose() => client.Dispose();
    }
}
