using System.IO;

namespace MeshinaStandalone
{
    public sealed class MdbFile
    {
        public string Path { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime WrittenUtc { get; set; }
        public long Length { get; set; }
        public string Stamp => Length + ":" + WrittenUtc.Ticks;
    }

    public sealed class MdbPoller
    {
        private readonly string directory;
        private readonly Dictionary<string, (string stamp, int count)> stability = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);

        public MdbPoller(string directory) { this.directory = directory; }

        public List<MdbFile> Snapshot()
        {
            // 目录不可访问时必须报错，不能把失败当成空目录建立基线。
            return new DirectoryInfo(directory).GetFiles("*.mdb", SearchOption.TopDirectoryOnly)
                .Select(f => new MdbFile { Path = f.FullName, CreatedUtc = f.CreationTimeUtc,
                    WrittenUtc = f.LastWriteTimeUtc, Length = f.Length }).ToList();
        }

        public List<MdbFile> Candidates(MeshinaJob job)
        {
            var excluded = new HashSet<string>(job.BaselineFiles, StringComparer.OrdinalIgnoreCase);
            return Snapshot().Where(f => !excluded.Contains(f.Path) && f.CreatedUtc > job.ScanTimeUtc)
                .OrderBy(f => f.CreatedUtc).ThenBy(f => f.Path).ToList();
        }

        public bool IsStable(MdbFile file, int requiredCount)
        {
            int count = stability.TryGetValue(file.Path, out var previous) && previous.stamp == file.Stamp ? previous.count + 1 : 1;
            stability[file.Path] = (file.Stamp, count);
            return file.Length > 0 && count >= requiredCount;
        }

        public void Reset(string path) { stability.Remove(path); }
        public void Reset() { stability.Clear(); }
    }
}
