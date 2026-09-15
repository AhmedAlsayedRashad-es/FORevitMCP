using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace FirstOption.RevitMcp.Shared
{
    public static class ActivityKinds
    {
        public const string Execute = "execute";
        public const string LibrarySave = "library_save";
        public const string GitHubPush = "github_push";
        public const string Undo = "undo";
        public const string Reset = "reset";
    }

    [DataContract]
    public sealed class ActivityEntry
    {
        [DataMember(Name = "id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember(Name = "time")] public string Time { get; set; } = DateTime.UtcNow.ToString("o");
        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "client")] public string Client { get; set; }
        [DataMember(Name = "port")] public int Port { get; set; }
        [DataMember(Name = "language")] public string Language { get; set; }
        [DataMember(Name = "commandName")] public string CommandName { get; set; }
        [DataMember(Name = "code")] public string Code { get; set; }
        [DataMember(Name = "output")] public string Output { get; set; }
        [DataMember(Name = "error")] public string Error { get; set; }
        [DataMember(Name = "ok")] public bool Ok { get; set; }
        [DataMember(Name = "durationMs")] public long DurationMs { get; set; }
        [DataMember(Name = "document")] public string Document { get; set; }
        [DataMember(Name = "message")] public string Message { get; set; }
        [DataMember(Name = "repo")] public string Repo { get; set; }
        [DataMember(Name = "commit")] public string Commit { get; set; }
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "files")] public int Files { get; set; }
        [DataMember(Name = "runId")] public string RunId { get; set; }
        [DataMember(Name = "undoName")] public string UndoName { get; set; }

        public DateTime LocalTime
        {
            get
            {
                DateTime t;
                return DateTime.TryParse(Time, null, System.Globalization.DateTimeStyles.RoundtripKind, out t) ? t.ToLocalTime() : DateTime.MinValue;
            }
        }
    }

    /// <summary>Append-only JSON-lines log shared by all MCP server processes and all Revit sessions.</summary>
    public static class ActivityLog
    {
        private const int MaxText = 20000;
        private const long MaxFileBytes = 10 * 1024 * 1024;

        private static DataContractJsonSerializer Serializer => new DataContractJsonSerializer(typeof(ActivityEntry));

        public static void Append(ActivityEntry entry)
        {
            entry.Code = Cut(entry.Code);
            entry.Output = Cut(entry.Output);
            entry.Error = Cut(entry.Error);

            byte[] line;
            using (var ms = new MemoryStream())
            {
                Serializer.WriteObject(ms, entry);
                ms.WriteByte((byte)'\n');
                line = ms.ToArray();
            }

            var path = AppPaths.ActivityFile;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    RotateIfLarge(path);
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        fs.Write(line, 0, line.Length);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(25);
                }
            }
        }

        /// <summary>Newest entries last. Bad lines are skipped.</summary>
        public static List<ActivityEntry> ReadTail(int max)
        {
            var result = new List<ActivityEntry>();
            var path = AppPaths.ActivityFile;
            if (!File.Exists(path)) return result;

            string text;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                    text = reader.ReadToEnd();
            }
            catch (IOException)
            {
                return result;
            }

            var lines = text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var start = Math.Max(0, lines.Length - max);
            var serializer = Serializer;
            for (var i = start; i < lines.Length; i++)
            {
                try
                {
                    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(lines[i])))
                        result.Add((ActivityEntry)serializer.ReadObject(ms));
                }
                catch
                {
                    // a line that another process is still writing, or a broken line
                }
            }
            return result;
        }

        private static void RotateIfLarge(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxFileBytes) return;
            var old = path + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
        }

        private static string Cut(string s) =>
            s == null || s.Length <= MaxText ? s : s.Substring(0, MaxText) + "\n... (cut, " + s.Length + " chars)";
    }
}
