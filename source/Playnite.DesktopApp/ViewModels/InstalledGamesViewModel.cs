﻿using Playnite;
using Playnite.Database;
using Playnite.SDK.Models;
using Playnite.SDK;
using Playnite.Commands;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Playnite.Common;
using System.Diagnostics;
using System.Drawing.Imaging;
using Playnite.Windows;
using System.Drawing;
using Playnite.Common.Media.Icons;
using System.Windows.Data;

namespace Playnite.DesktopApp.ViewModels
{
    public class InstalledGamesViewModel : ObservableObject
    {
        public enum ProgramType
        {
            Win32,
            UWP
        }

        public class ImportableProgram : SelectableItem<Program>
        {
            public static BitmapImage EmptyImage { get; set; }

            public ProgramType Type
            {
                get; set;
            }

            public string DisplayPath
            {
                get; set;
            }

            private ImageSource iconSource;
            public ImageSource IconSource
            {
                get
                {
                    if (string.IsNullOrEmpty(Item.Icon))
                    {
                        return null;
                    }

                    if (iconSource != null)
                    {
                        return iconSource;
                    }

                    if (Type == ProgramType.UWP)
                    {
                        iconSource = BitmapExtensions.CreateSourceFromURI(Item.Icon);
                    }
                    else
                    {
                        string path;
                        var match = Regex.Match(Item.Icon, @"(.*),(\d+)");
                        if (match.Success)
                        {
                            path = match.Groups[1].Value;
                            if (string.IsNullOrEmpty(path))
                            {
                                path = Item.Path;
                            }
                        }
                        else
                        {
                            path = Item.Icon;
                        }

                        var index = match.Groups[2].Value;
                        if (!File.Exists(path))
                        {
                            return null;
                        }

                        if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                        {
                            iconSource = BitmapExtensions.CreateSourceFromURI(path);
                        }
                        else
                        {
                            var icon = IconExtractor.ExtractMainIconFromFile(path);
                            if (icon != null)
                            {
                                try
                                {
                                    iconSource = icon.ToImageSource();
                                }
                                catch (Exception e)
                                {
                                    logger.Error(e, "Failed to convert icon.");
                                }
                                finally
                                {
                                    icon.Dispose();
                                }
                            }
                        }
                    }

                    if (iconSource == null)
                    {
                        iconSource = EmptyImage;
                    }

                    return iconSource;
                }
            }

            private bool import;
            public bool Import
            {
                get => import;
                set
                {
                    import = value;
                    OnPropertyChanged();
                }
            }

            public ImportableProgram(Program program, ProgramType type) : base(program)
            {
                Type = type;
                DisplayPath = type == ProgramType.Win32 ? program.Path : "Microsoft Store";
            }
        }

        public List<GameMetadata> SelectedGames
        {
            get;
            private set;
        } = new List<GameMetadata>();

        private ObservableCollection<ImportableProgram> programs = new ObservableCollection<ImportableProgram>();
        public ObservableCollection<ImportableProgram> Programs
        {
            get
            {
                return programs;
            }

            set
            {
                programs = value;
                OnPropertyChanged();
            }
        }

        private ImportableProgram selectedProgram;
        public ImportableProgram SelectedProgram
        {
            get
            {
                return selectedProgram;
            }

            set
            {
                selectedProgram = value;
                OnPropertyChanged();
            }
        }

        private ListCollectionView collectionView;
        public ListCollectionView CollectionView
        {
            get => collectionView;
            private set
            {
                collectionView = value;
                OnPropertyChanged();
            }
        }

        private bool hideImported = true;
        public bool HideImported
        {
            get => hideImported;
            set
            {
                hideImported = value;
                OnPropertyChanged();
                CollectionView.Refresh();
            }
        }

        private bool markImportAll;
        public bool MarkImportAll
        {
            get => markImportAll;
            set
            {
                markImportAll = value;
                OnPropertyChanged();
                Programs.ForEach(a => a.Import = markImportAll);
            }
        }

        private readonly object listSyncLock = new object();
        private readonly HashSet<string> importedExes;
        private static ILogger logger = LogManager.GetLogger();
        private IWindowFactory window;
        private IDialogsFactory dialogs;

        public RelayCommand<object> CloseCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                CloseView(false);
            });
        }

        public RelayCommand<object> ConfirmCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                ConfirmDialog();
            });
        }

        public RelayCommand<object> SelectExecutableCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                SelectExecutable();
            });
        }

        public RelayCommand<object> ScanFolderCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                ScanFolder();
            });
        }

        public RelayCommand<object> DetectInstalledCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                DetectInstalled();
            });
        }

        public InstalledGamesViewModel(IWindowFactory window, IDialogsFactory dialogs, IGameDatabaseMain database)
        {
            this.window = window;
            this.dialogs = dialogs;
            importedExes = database.GetImportedExeFiles();
            CollectionView = (ListCollectionView)CollectionViewSource.GetDefaultView(Programs);
            CollectionView.Filter = ListFilter;
            BindingOperations.EnableCollectionSynchronization(Programs, listSyncLock);
            ImportableProgram.EmptyImage = new BitmapImage(); // This is initialized here because the bitmap has to be created on main thread
        }

        public bool? OpenView()
        {
            return window.CreateAndOpenDialog(this);
        }

        public bool? OpenView(string directory)
        {
            if (!string.IsNullOrEmpty(directory))
            {
#pragma warning disable CS4014
                ScanFolder(directory);
#pragma warning restore CS4014
            }

            return window.CreateAndOpenDialog(this);
        }

        public bool? OpenViewOnWindowsApps()
        {
            DetectWindowsStoreApps();
            return window.CreateAndOpenDialog(this);
        }

        public void CloseView(bool? result)
        {
            window.Close(result);
        }

        public void ConfirmDialog()
        {
            SelectedGames = new List<GameMetadata>();
            foreach (var program in Programs)
            {
                if (!program.Import)
                {
                    continue;
                }

                var newGame = new GameMetadata()
                {
                    Name = program.Item.Name.RemoveTrademarks(),
                    GameId = program.Item.AppId,
                    InstallDirectory = program.Item.WorkDir,
                    Source = program.Type == ProgramType.UWP ? new MetadataNameProperty("Microsoft Store") : null,
                    IsInstalled = true,
                    Platforms = new HashSet<MetadataProperty> { new MetadataSpecProperty("pc_windows") }
                };

                var path = program.Item.Path;
                if (program.Type == ProgramType.Win32 && !string.IsNullOrEmpty(program.Item.WorkDir))
                {
                    path = program.Item.Path.Replace(program.Item.WorkDir.EndWithDirSeparator(), ExpandableVariables.InstallationDirectory.EndWithDirSeparator());
                }

                newGame.GameActions = new List<GameAction>
                {
                     new GameAction()
                    {
                        Path = path,
                        Arguments = program.Item.Arguments,
                        Type = GameActionType.File,
                        WorkingDir = program.Type == ProgramType.Win32 ? ExpandableVariables.InstallationDirectory : string.Empty,
                        Name = newGame.Name,
                        IsPlayAction = true
                    }
                };

                if (program.IconSource != null &&  program.IconSource != ImportableProgram.EmptyImage)
                {
                    var bitmap = (BitmapSource)program.IconSource;
                    newGame.Icon = new MetadataFile(Guid.NewGuid().ToString() + ".png", bitmap.ToPngArray());
                }

                SelectedGames.Add(newGame);
            }

            CloseView(true);
        }

        public void SelectExecutable()
        {
            var path = dialogs.SelectFile("Executable (.exe,.bat,lnk)|*.exe;*.bat;*.lnk");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var program = Common.Programs.GetProgramData(path);
            var import = new ImportableProgram(program, ProgramType.Win32)
            {
                Selected = true
            };

            // Use shortcut name as game name for .lnk shortcuts
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var shortcutName = Path.GetFileNameWithoutExtension(path);
                if (!shortcutName.IsNullOrEmpty())
                {
                    import.Item.Name = shortcutName;
                }
            }

            Programs.Add(import);
            SelectedProgram = import;
        }

        public void DetectInstalled()
        {
            dialogs.ActivateGlobalProgress(async (progArgs) =>
            {
                try
                {
                    var allApps = new List<ImportableProgram>();
                    var installed = await Playnite.Common.Programs.GetInstalledPrograms(progArgs.CancelToken);
                    if (installed != null)
                    {
                        allApps.AddRange(installed.Select(a => new ImportableProgram(a, ProgramType.Win32)));
                        if (Computer.WindowsVersion == WindowsVersion.Win10 || Computer.WindowsVersion == WindowsVersion.Win11)
                        {
                            allApps.AddRange(Playnite.Common.Programs.GetUWPApps().Select(a => new ImportableProgram(a, ProgramType.UWP)));
                        }

                        progArgs.MainContext.Send(_ =>
                        {
                            Programs.Clear();
                            Programs.AddRange(allApps.OrderBy(a => a.Item.Name));
                        }, null);
                    }
                }
                catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(exc, "Failed to load list of installed apps.");
                }
            }, new GlobalProgressOptions(LOC.EmuWizardScanning, true));
        }

        public void DetectWindowsStoreApps()
        {
            try
            {
                var winApps = Playnite.Common.Programs.GetUWPApps().Select(a => new ImportableProgram(a, ProgramType.UWP));
                Programs.Clear();
                Programs.AddRange(winApps.OrderBy(a => a.Item.Name));
            }
                catch (Exception e) when(!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to detect Windows Store apps.");
            }
        }

        public void ScanFolder()
        {
            var path = dialogs.SelectFolder();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            ScanFolder(path);
        }

        public void ScanFolder(string path)
        {
            dialogs.ActivateGlobalProgress(async (progArgs) =>
            {
                try
                {
                    var executables = await Playnite.Common.Programs.GetExecutablesFromFolder(path, SearchOption.AllDirectories, progArgs.CancelToken);
                    if (executables != null)
                    {
                        var apps = executables.Select(a => new ImportableProgram(a, ProgramType.Win32)).OrderBy(a => a.Item.Name);
                        progArgs.MainContext.Send(_ =>
                        {
                            Programs.Clear();
                            Programs.AddRange(apps);
                        }, null);
                    }
                }
                catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(exc, "Failed to scan folder for executables: " + path);
                }
            }, new GlobalProgressOptions(LOC.EmuWizardScanning, true));
        }

        public static List<Game> AddImportableGamesToDb(List<GameMetadata> games, IGameDatabaseMain database)
        {
            var statusSettings = database.GetCompletionStatusSettings();
            using (var buffer = database.BufferedUpdate())
            {
                var addedGames = new List<Game>();
                foreach (var game in games)
                {
                    var added = database.ImportGame(game);
                    if (statusSettings.DefaultStatus != Guid.Empty)
                    {
                        added.CompletionStatusId = statusSettings.DefaultStatus;
                        database.Games.Update(added);
                    }

                    addedGames.Add(added);
                }

                return addedGames;
            }
        }

        private bool ListFilter(object item)
        {
            var program = (ImportableProgram)item;
            if (HideImported)
            {
                return !importedExes.ContainsString(program.Item.Path, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }
    }
}
