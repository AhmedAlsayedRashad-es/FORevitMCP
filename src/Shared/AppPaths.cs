using System;
using System.IO;

namespace FirstOption.RevitMcp.Shared
{
    /// <summary>Folders and names that the MCP server, the Revit add-in and the pyRevit extension agree on.</summary>
    public static class AppPaths
    {
        public const int DefaultPortStart = 48884;
        public const int DefaultPortCount = 10;
        public const string DefaultRoutesHost = "127.0.0.1";

        /// <summary>The pyRevit Routes API name. URLs are http://host:port/fo-mcp/&lt;route&gt;/</summary>
        public const string RoutesApiName = "fo-mcp";

        /// <summary>The command library folder is a pyRevit extension: &lt;library&gt;\FirstOptionLibrary.extension\FO Library.tab\Commands.panel\&lt;name&gt;.pushbutton</summary>
        public const string LibraryExtensionFolder = "FirstOptionLibrary.extension";
        public const string LibraryTabFolder = "FO Library.tab";
        public const string LibraryPanelFolder = "Commands.panel";

        public static string LibraryPanelDir(string libraryRoot) =>
            Path.Combine(libraryRoot, LibraryExtensionFolder, LibraryTabFolder, LibraryPanelFolder);

        /// <summary>%LOCALAPPDATA%\First Option\RevitMCP, or the FO_REVIT_MCP_HOME environment variable (used by tests).
        /// All files of the MCP live under this folder; only the Revit .addin manifest and the agent skills live elsewhere.</summary>
        public static string DataDir
        {
            get
            {
                var over = Environment.GetEnvironmentVariable("FO_REVIT_MCP_HOME");
                var dir = !string.IsNullOrWhiteSpace(over)
                    ? over
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "First Option", "RevitMCP");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public const string ServerFolder = "Server";
        public const string AddinFolder = "Revit Add-in";
        public const string BridgeFolder = "pyRevit Bridge";
        public const string LibraryFolder = "Command Library";
        public const string ActivityFolder = "Activity Log";
        public const string SettingsFolder = "Settings";

        public static string SettingsFile => InDataDir(SettingsFolder, "settings.json");

        /// <summary>One JSON object per line. The MCP server writes it; the Revit panel reads it.</summary>
        public static string ActivityFile => InDataDir(ActivityFolder, "activity.jsonl");

        public static string DefaultLibraryPath => Path.Combine(DataDir, LibraryFolder);

        private static string InDataDir(string folder, string file)
        {
            var dir = Path.Combine(DataDir, folder);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, file);
        }
    }
}
