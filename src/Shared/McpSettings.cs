using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace FirstOption.RevitMcp.Shared
{
    /// <summary>
    /// settings.json. The Revit add-in edits it (GitHub Settings window); the MCP server reads it on every call.
    /// DataContractJsonSerializer works on .NET Framework 4.8 and .NET 8, so both sides use the same code.
    /// </summary>
    [DataContract]
    public sealed class McpSettings
    {
        [DataMember(Name = "githubOwner")] public string GitHubOwner { get; set; }
        [DataMember(Name = "githubRepo")] public string GitHubRepo { get; set; }
        [DataMember(Name = "branch")] public string Branch { get; set; }
        [DataMember(Name = "tokenProtected")] public string TokenProtected { get; set; }
        [DataMember(Name = "libraryPath")] public string LibraryPath { get; set; }
        [DataMember(Name = "authorName")] public string AuthorName { get; set; }
        [DataMember(Name = "authorEmail")] public string AuthorEmail { get; set; }
        [DataMember(Name = "autoPush")] public bool AutoPush { get; set; }
        [DataMember(Name = "notifyOnPush")] public bool NotifyOnPush { get; set; }
        [DataMember(Name = "routesHost")] public string RoutesHost { get; set; }
        [DataMember(Name = "portStart")] public int PortStart { get; set; }
        [DataMember(Name = "portCount")] public int PortCount { get; set; }

        /// <summary>Optional git remote that replaces https://github.com/owner/repo.git (GitHub Enterprise, or tests).</summary>
        [DataMember(Name = "remoteUrl")] public string RemoteUrl { get; set; }

        public McpSettings() { SetDefaults(); }

        [OnDeserializing]
        private void OnDeserializing(StreamingContext context) { SetDefaults(); }

        private void SetDefaults()
        {
            GitHubOwner = "";
            GitHubRepo = "";
            Branch = "main";
            TokenProtected = "";
            LibraryPath = "";
            AuthorName = "";
            AuthorEmail = "";
            AutoPush = false;
            NotifyOnPush = true;
            RoutesHost = AppPaths.DefaultRoutesHost;
            PortStart = AppPaths.DefaultPortStart;
            PortCount = AppPaths.DefaultPortCount;
        }

        public string EffectiveLibraryPath =>
            string.IsNullOrWhiteSpace(LibraryPath) ? AppPaths.DefaultLibraryPath : Environment.ExpandEnvironmentVariables(LibraryPath.Trim());

        public string EffectiveBranch => string.IsNullOrWhiteSpace(Branch) ? "main" : Branch.Trim();

        public string EffectiveHost => string.IsNullOrWhiteSpace(RoutesHost) ? AppPaths.DefaultRoutesHost : RoutesHost.Trim();

        public bool GitHubConfigured => !string.IsNullOrWhiteSpace(GitHubOwner) && !string.IsNullOrWhiteSpace(GitHubRepo);

        public bool HasToken => !string.IsNullOrEmpty(TokenProtected);

        public string GetToken() => SecretProtector.Unprotect(TokenProtected);

        public void SetToken(string token) => TokenProtected = SecretProtector.Protect(token);

        private static DataContractJsonSerializer Serializer =>
            new DataContractJsonSerializer(typeof(McpSettings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        public static McpSettings Load()
        {
            try
            {
                var path = AppPaths.SettingsFile;
                if (!File.Exists(path)) return new McpSettings();
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var s = (McpSettings)Serializer.ReadObject(fs);
                    if (s.PortStart <= 0) s.PortStart = AppPaths.DefaultPortStart;
                    if (s.PortCount <= 0) s.PortCount = AppPaths.DefaultPortCount;
                    return s;
                }
            }
            catch
            {
                return new McpSettings();
            }
        }

        public void Save()
        {
            var path = AppPaths.SettingsFile;
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true))
            {
                Serializer.WriteObject(writer, this);
                writer.Flush();
            }
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }
    }
}
