using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using FirstOption.RevitMcp.Addin.UI;

namespace FirstOption.RevitMcp.Addin
{
    public class App : IExternalApplication
    {
        public const string TabName = "First Option";
        public const string PanelName = "AI Bridge";

        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("6C1B2F0E-5B7A-4E0C-9D1E-3F6A2B8C4D10"));

        internal static McpPanel Panel { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            RegisterAssemblyResolve();

            try { application.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* another First Option add-in made the tab */ }

            var panel = application.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == PanelName)
                        ?? application.CreateRibbonPanel(TabName, PanelName);

            AddButton(panel, "FoMcpPanel", "MCP\nPanel", typeof(TogglePanelCommand), "AI",
                "Show or hide the FirstOption MCP panel: pyRevit Routes status, port, executed commands, GitHub uploads.");
            AddButton(panel, "FoMcpLibrary", "Command\nLibrary", typeof(OpenLibraryCommand), "{ }",
                "Open the folder of saved agent commands (a pyRevit extension and a git repository).");
            AddButton(panel, "FoMcpGitHub", "GitHub\nSettings", typeof(GitHubSettingsCommand), "GH",
                "Set the GitHub repository and token that the MCP uses to upload the command library.");

            Panel = new McpPanel();
            application.RegisterDockablePane(PaneId, "FirstOption MCP", Panel);
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            Panel?.Stop();
            return Result.Succeeded;
        }

        private static void AddButton(RibbonPanel panel, string name, string text, Type command, string glyph, string tooltip)
        {
            var data = new PushButtonData(name, text, Assembly.GetExecutingAssembly().Location, command.FullName)
            {
                ToolTip = tooltip,
                LargeImage = Icons.Make(glyph, 32),
                Image = Icons.Make(glyph, 16),
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
            };
            panel.AddItem(data);
        }

        // Roslyn and its dependencies ship next to this dll. When Revit (or another add-in) asks for a
        // different version of one of them, give it ours instead of failing.
        private static void RegisterAssemblyResolve()
        {
            var dir = Path.GetDirectoryName(typeof(App).Assembly.Location);
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                var file = Path.Combine(dir, name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
        }
    }
}
