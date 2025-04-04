global using Serilog;
using CUE4Parse;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using Microsoft.WindowsAPICodePack.Dialogs;
using Newtonsoft.Json.Linq;
using System.Text;

using ProgressBar = iluvadev.ConsoleProgressBar.ProgressBar;

namespace SplitgateLegacyExport
{
    internal class Program
    {
        // dont want these things
        private readonly static List<string> IgnoredFiles = new List<string>()
        {
            ".bin",
            ".ini",
            ".txt",
            ".uplugin",
            ".upluginmanifest"
        };

        static async Task Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Console()
                .CreateLogger();

            string GamePakPath = ""; // valid game path
            string ProjectPath = ""; // valid content editor path

        FindPaks:
            if (GamePakPath == string.Empty)
            {
                using (var Finder = new CommonOpenFileDialog())
                {
                    Finder.IsFolderPicker = true;
                    Finder.Title = "Select Splitgate 1.9 Paks Folder";

                    if (Finder.ShowDialog() == CommonFileDialogResult.Ok)
                    {
                        GamePakPath = Finder.FileName;

                        if (!Directory.EnumerateFiles(GamePakPath).Any(File => File.EndsWith(".pak"))
                            || !GamePakPath.Contains("PortalWars\\Content\\Paks"))
                        {
                            Log.Error("Folder contains no Unreal Paks (Find PortalWars\\Content\\Paks).");

                            goto FindPaks;
                        }

                        Log.Information("Selected Game Path: {Path}", Finder.FileName);
                    }
                    else
                    {
                        Log.Error("No folder selected, please find /Content/Paks of the Splitgate 1.9 Install.");
                        goto FindPaks;
                    }
                }
            }

        FindContent:
            if (ProjectPath == string.Empty)
            {
                using (var Finder = new CommonOpenFileDialog())
                {
                    Finder.IsFolderPicker = true;
                    Finder.Title = "Select Project Content Folder";

                    if (Finder.ShowDialog() == CommonFileDialogResult.Ok)
                    {
                        ProjectPath = Finder.FileName;

                        if (!ProjectPath.EndsWith("Content"))
                        {
                            Log.Error("Folder is not content path.");

                            goto FindContent;
                        }

                        Log.Information("Selected Project Path: {Path}", Finder.FileName);
                    }
                    else
                    {
                        Log.Error("No folder selected, please find /Content/ of game project.");
                        goto FindContent;
                    }
                }
            }

            DefaultFileProvider Provider = new(GamePakPath, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE4_23));
            Globals.LogVfsMounts = false;
            Globals.WarnMissingImportPackage = false;

            Provider.Initialize();

            // 1.9.2 aes key
            await Provider.SubmitKeyAsync(
                new FGuid("00000000000000000000000000000000"),
                new FAesKey("0xD73A797940208F2FB29256BE81A7CBC7B74CBF899441BB277F357F7F4577DBBB"));

            if (Provider.TryGetGameFile("PortalWars/PortalWars.uproject", out var ProjectFile))
            {
                JObject ProjectData = JObject.Parse(Encoding.Default.GetString(await ProjectFile.ReadAsync()));
                if (ProjectData["EngineAssociation"]?.ToString() == "4.23"
                    && (Provider.Files.Where(x => x.Value.Name.Contains("BattlePass_S01")).Count() >= 1))
                {
                    Log.Information("Valid Splitgate 1.9!");
                }
                else
                {
                    goto FindPaks;
                }
            }
            else
            {
                goto FindPaks;
            }

            List<string> FinalList = new List<string>();
            using (var pb = new ProgressBar { Maximum = Provider.Files.Values.Count() })
            {
                foreach (var GameFile in Provider.Files)
                {
                    if (GameFile.Value.Path.StartsWith("Engine")) continue;
                    if (!GameFile.Value.IsUePackage) continue;
                    if (IgnoredFiles.Contains(GameFile.Value.Extension)) continue;

                    IPackage? Package = await Provider.LoadPackageAsync(GameFile.Value);
                    if (Package == null) continue;

                    // gather non blueprint generated classes

                    // wrap all to shush errors
                    try
                    {

                        bool FoundCompiledClass = false;
                        for (int i = 0; i != Package.GetExports().Count(); i++)
                        {
                            UObject? GameObject = Package.GetExport(i);
                            if (GameObject == null) continue;

                            if (GameObject.ExportType.Contains("BlueprintGeneratedClass") 
                                && GameObject.ExportType.Contains("AnimMontage")
                                && GameObject.ExportType.Contains("BlendSpace1D")
                                && GameFile.Value.Extension != "umap")
                            {
                                FoundCompiledClass = true;
                                break;
                            }
                        }
                        if (FoundCompiledClass)
                            continue;

                        if (Provider.TrySavePackage(GameFile.Value.Path, out var SavedAssets))
                        {
                            Parallel.ForEach(SavedAssets, kvp =>
                            {
                                var NewProjectPath = Path.Combine(ProjectPath, kvp.Key.Replace("PortalWars/Content/", "")).Replace('\\', '/');
                                string FixedPath = NewProjectPath.SubstringBeforeLast('/');

                                if (kvp.Key.Contains("PortalWars/Plugins/")) return;

                                if (!Directory.Exists(FixedPath))
                                    Directory.CreateDirectory(FixedPath);

                                if (File.Exists(NewProjectPath))
                                    File.Delete(NewProjectPath);

                                FinalList.Add($"\"{NewProjectPath}\"");
                                File.WriteAllBytes(NewProjectPath, kvp.Value);
                            });
                        }
                    }
                    catch (Exception Ex)
                    {
                        Log.Error("File '{File}' reported error: {Message}", 2, Ex.Message);
                    }

                    pb.PerformStep();
                }
            }

            File.WriteAllLines(ProjectPath.SubstringBeforeLast('/') + "CreatedList.txt", FinalList);
            Log.Information("Wrote all created content to CreatedList.txt in root of project.");
        }
    }
}
