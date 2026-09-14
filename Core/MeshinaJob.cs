

namespace MeshinaStandalone
{
    public enum MeshinaStage { FeedingCheckSending, FeedingCheckRejected, WaitingForMdb,
        ReadyForCheckOut, CheckOutSending, CheckOutRejected, Completed, Cancelled, MdbTimedOut }

    public sealed class MeshinaJob
    {
        public string SN { get; set; }
        public DateTime ScanTimeUtc { get; set; }
        public List<string> BaselineFiles { get; set; }
        public MeshinaStage Stage { get; set; }
        public DateTime? FeedingAcceptedUtc { get; set; }
        public int MdbWaitTimeoutSeconds { get; set; }
        public DateTime? MdbBoundUtc { get; set; }
        public string MdbPath { get; set; }
        public MeshinaMeasurement Measurement { get; set; }
        public FeedingCheckModel FeedingCheckRequest { get; set; }
        public SNCheckoutModel CheckOutRequest { get; set; }
        public string Message { get; set; }
        public MeshinaMesReply FeedingReply { get; set; }
        public MeshinaMesReply CheckoutReply { get; set; }
        public volatile bool AbortRequested;
    }
}
