namespace MeshinaStandalone
{
    public class BaseModel
    {
        public string Line { get; set; }
        public string StationID { get; set; }
        public string MachineID { get; set; }
        public string OPID { get; set; }
        public string SendTime { get; set; } = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
    }
    public class FeedingCheckModel : BaseModel
    {
        public string EventID { get; set; }
        public string Token { get; set; }
        public string FixSN { get; set; }
        public string SN { get; set; }
    }
    public class SNCheckoutModel : BaseModel
    {
        public string EventID { get; set; }
        public string Token { get; set; }
        public string Mold { get; set; }
        public string FixSN { get; set; }
        public SNInfo[] SNInfo { get; set; }
        public string CarrierID { get; set; } = "";
        public string Qty { get; set; } = "";
    }
    public class SNInfo
    {
        public string SN { get; set; }
        public string Result { get; set; }
        public DC_Info[] DC_Info { get; set; }
        public CompList[] CompList { get; set; }
    }
    public class DC_Info
    {
        public string Item { get; set; }
        public string Value { get; set; }
        public string Result { get; set; }
    }
    public class CompList { public string CompID { get; set; } public int Qty { get; set; } }
}
