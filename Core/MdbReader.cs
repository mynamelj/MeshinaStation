using System.Data.OleDb;
using System.Globalization;
using System.IO;

namespace MeshinaStandalone
{
    public sealed class MeshinaMeasurement
    {
        public Dictionary<string, decimal> Values { get; set; } = new Dictionary<string, decimal>();
        public List<string> EmptyFields { get; set; } = new List<string>();
        // 仅留存原始结果，当前不参与拦截或MES结果计算。
        public string Result { get; set; }
    }

    public interface IMdbReader
    {
        MeshinaMeasurement Read(string path);
    }

    public sealed class MdbReader : IMdbReader
    {
        public static readonly string[] NumericFields = { "Fi", "fii", "Fr" };
        private readonly string provider;
        private readonly HashSet<string> requiredFields;

        public MdbReader(string provider, IEnumerable<string> requiredFields = null)
        {
            this.provider = provider;
            this.requiredFields = new HashSet<string>(requiredFields ?? new[] { "Fi", "fii", "Fr" }, StringComparer.OrdinalIgnoreCase);
        }

        public MeshinaMeasurement Read(string path)
        {
            var builder = new OleDbConnectionStringBuilder
            {
                Provider = provider,
                DataSource = path
            };
            builder["Mode"] = "Read";
            builder["Persist Security Info"] = false;
            using var connection = new OleDbConnection(builder.ConnectionString);
            try { connection.Open(); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"MDB驱动不可用：当前程序为{(Environment.Is64BitProcess ? 64 : 32)}位，"
                    + $"请安装同位数的Access Runtime/数据库引擎（{provider}）。{ex.Message}", ex);
            }
            // 不依赖无序的第一条记录；单件单文件必须恰好一行。
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT TOP 2 " + string.Join(",", NumericFields.Select(f => "[" + f + "]"))
                + ",[Result] FROM [TJSHEET]";
            command.CommandTimeout = 5;
            using var reader = command.ExecuteReader();
            if (reader == null || !reader.Read()) throw new InvalidDataException("TJSHEET尚无检测记录，继续等待");
            var measurement = new MeshinaMeasurement();
            foreach (string field in NumericFields)
            {
                object value = reader[field];
                if (value == DBNull.Value)
                {
                    if (requiredFields.Contains(field)) throw new InvalidDataException($"必填检测字段{field}为空，继续等待完整数据");
                    measurement.EmptyFields.Add(field);
                    continue; // 设备未启用的检测项保留为空，不伪造为0或上传为0。
                }
                measurement.Values.Add(field, Convert.ToDecimal(value, CultureInfo.InvariantCulture));
            }
            measurement.Result = reader["Result"] == DBNull.Value ? "" : Convert.ToString(reader["Result"]);
            if (reader.Read()) throw new InvalidDataException("TJSHEET有多条记录，无法自动确定本件数据，请联系技术人员");
            if (measurement.Values.Count == 0) throw new InvalidDataException("TJSHEET没有有效数值数据");
            return measurement;
        }
    }
}
