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

        /// <summary>%LOCALAPPDATA%\FirstOption\RevitMCP, or the FO_REVIT_MCP_HOME environment variable (used by tests).</summary>
        public static string DataDir
        {
            get
            {
                var over = Environment.GetEnvironmentVariable("FO_REVIT_MCP_HOME");
                var dir = !string.IsNullOrWhiteSpace(over)
                    ? over
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FirstOption", "RevitMCP");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string SettingsFile => Path.Combine(DataDir, "settings.json");

        /// <summary>One JSON object per line. The MCP server writes it; the Revit panel reads it.</summary>
        public static string ActivityFile => Path.Combine(DataDir, "activity.jsonl");

        public static string DefaultLibraryPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FirstOption", "RevitCommandLibrary");
    }
}
