using Microsoft.PowerToys.Settings.UI.Lib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Wox.Infrastructure;
using Wox.Infrastructure.Storage;
using Wox.Plugin;

namespace Microsoft.Plugin.Folder
{
    public class Main : IPlugin, ISettingProvider, IPluginI18n, ISavable, IContextMenu
    {
        public const string FolderImagePath = "Images\\folder.dark.png";
        public const string FileImagePath = "Images\\file.dark.png";
        public const string DeleteFileFolderImagePath = "Images\\delete.dark.png";
        public const string CopyImagePath = "Images\\copy.dark.png";

        private const string _fileExplorerProgramName = "explorer";
        private static List<string> _driverNames;
        private PluginInitContext _context;

        private readonly Settings _settings;
        private readonly PluginJsonStorage<Settings> _storage;
        private IContextMenu _contextMenuLoader;

        public Main()
        {
            _storage = new PluginJsonStorage<Settings>();
            _settings = _storage.Load();
        }

        public void Save()
        {
            _storage.Save();
        }

        public Control CreateSettingPanel()
        {
            return new FileSystemSettings(_context.API, _settings);
        }

        public void Init(PluginInitContext context)
        {
            _context = context;
            _contextMenuLoader = new ContextMenuLoader(context);
            InitialDriverList();
        }

        public List<Result> Query(Query query)
        {
            var results = GetUserFolderResults(query);

            string search = query.Search.ToLower();
            if (!IsDriveOrSharedFolder(search))
                return results;

            results.AddRange(QueryInternal_Directory_Exists(query));

            // todo why was this hack here?
            foreach (var result in results)
            {
                result.Score += 10;
            }

            return results;
        }

        private static bool IsDriveOrSharedFolder(string search)
        {
            if (search.StartsWith(@"\\"))
            { // share folder
                return true;
            }

            if (_driverNames != null && _driverNames.Any(search.StartsWith))
            { // normal drive letter
                return true;
            }

            if (_driverNames == null && search.Length > 2 && char.IsLetter(search[0]) && search[1] == ':')
            { // when we don't have the drive letters we can try...
                return true; // we don't know so let's give it the possibility
            }

            return false;
        }

        private Result CreateFolderResult(string title, string subtitle, string path, Query query)
        {
            return new Result
            {
                Title = title,
                IcoPath = path,
                SubTitle = "Folder: " + subtitle,
                QueryTextDisplay = path,
                TitleHighlightData = StringMatcher.FuzzySearch(query.Search, title).MatchData,
                ContextData = new SearchResult { Type = ResultType.Folder, FullPath = path },
                Action = c =>
                {
                    Process.Start(_fileExplorerProgramName, path);
                    return true;
                }
            };
        }

        private List<Result> GetUserFolderResults(Query query)
        {
            string search = query.Search.ToLower();
            var userFolderLinks = _settings.FolderLinks.Where(
                x => x.Nickname.StartsWith(search, StringComparison.OrdinalIgnoreCase));
            var results = userFolderLinks.Select(item =>
                CreateFolderResult(item.Nickname, item.Path, item.Path, query)).ToList();
            return results;
        }

        private void InitialDriverList()
        {
            if (_driverNames == null)
            {
                _driverNames = new List<string>();
                var allDrives = DriveInfo.GetDrives();
                foreach (DriveInfo driver in allDrives)
                {
                    _driverNames.Add(driver.Name.ToLower().TrimEnd('\\'));
                }
            }
        }

        private static readonly char[] _specialSearchChars = new char[]
        {
            '?', '*', '>'
        };

        private List<Result> QueryInternal_Directory_Exists(Query query)
        {
            var search = query.Search;
            var results = new List<Result>();
            var hasSpecial = search.IndexOfAny(_specialSearchChars) >= 0;
            string incompleteName = "";
            if (hasSpecial || !Directory.Exists(search + "\\"))
            {
                // if folder doesn't exist, we want to take the last part and use it afterwards to help the user 
                // find the right folder.
                int index = search.LastIndexOf('\\');
                if (index > 0 && index < (search.Length - 1))
                {
                    incompleteName = search.Substring(index + 1).ToLower();
                    search = search.Substring(0, index + 1);
                    if (!Directory.Exists(search))
                    {
                        return results;
                    }
                }
                else
                {
                    return results;
                }
            }
            else
            {
                // folder exist, add \ at the end of doesn't exist
                if (!search.EndsWith("\\"))
                {
                    search += "\\";
                }
            }

            results.Add(CreateOpenCurrentFolderResult(incompleteName, search));

            var searchOption = SearchOption.TopDirectoryOnly;
            incompleteName += "*";

            // give the ability to search all folder when starting with >
            if (incompleteName.StartsWith(">"))
            {
                searchOption = SearchOption.AllDirectories;

                // match everything before and after search term using supported wildcard '*', ie. *searchterm*
                incompleteName = "*" + incompleteName.Substring(1);
            }

            var folderList = new List<Result>();
            var fileList = new List<Result>();

            try
            {
                // search folder and add results
                var directoryInfo = new DirectoryInfo(search);
                var fileSystemInfos = directoryInfo.GetFileSystemInfos(incompleteName, searchOption);

                foreach (var fileSystemInfo in fileSystemInfos)
                {
                    if ((fileSystemInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) continue;

                    if(fileSystemInfo is DirectoryInfo)
                    {
                        var folderSubtitleString = fileSystemInfo.FullName;

                        folderList.Add(CreateFolderResult(fileSystemInfo.Name, folderSubtitleString, fileSystemInfo.FullName, query));
                    }
                    else
                    {
                        fileList.Add(CreateFileResult(fileSystemInfo.FullName, query));
                    }
                }
            }
            catch (Exception e)
            {
                if (e is UnauthorizedAccessException || e is ArgumentException)
                {
                    results.Add(new Result { Title = e.Message, Score = 501 });

                    return results;
                }

                throw;
            }

            // Initial ordering, this order can be updated later by UpdateResultView.MainViewModel based on history of user selection.
            return results.Concat(folderList.OrderBy(x => x.Title)).Concat(fileList.OrderBy(x => x.Title)).ToList();
        }

        private static Result CreateFileResult(string filePath, Query query)
        {
            var result = new Result
            {
                Title = Path.GetFileName(filePath),
                SubTitle = "Folder: " + filePath,
                IcoPath = filePath,
                TitleHighlightData = StringMatcher.FuzzySearch(query.Search, Path.GetFileName(filePath)).MatchData,
                Action = c =>
                {
                    try
                    {
                        Process.Start(_fileExplorerProgramName, filePath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Could not start " + filePath);
                    }

                    return true;
                },
                ContextData = new SearchResult { Type = ResultType.File, FullPath = filePath}
            };
            return result;
        }

        private static Result CreateOpenCurrentFolderResult(string incompleteName, string search)
        {
            var firstResult = "Open " + search;

            var folderName = search.TrimEnd('\\').Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.None).Last();
            var sanitizedPath = Regex.Replace(search, @"[\/\\]+", "\\");
            // A network path must start with \\
            if (sanitizedPath.StartsWith("\\"))
            {
                sanitizedPath = sanitizedPath.Insert(0, "\\");
            }

            return new Result
            {
                Title = firstResult,
                QueryTextDisplay = search,
                SubTitle = $"Folder: Use > to search within the directory. Use * to search for file extensions. Or use both >*.",
                IcoPath = search,
                Score = 500,
                Action = c =>
                {
                    Process.Start(_fileExplorerProgramName, sanitizedPath);
                    return true;
                }
            };
        }

        public string GetTranslatedPluginTitle()
        {
            return _context.API.GetTranslation("wox_plugin_folder_plugin_name");
        }

        public string GetTranslatedPluginDescription()
        {
            return _context.API.GetTranslation("wox_plugin_folder_plugin_description");
        }

        public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
        {
            return _contextMenuLoader.LoadContextMenus(selectedResult);
        }

        public void UpdateSettings(PowerLauncherSettings settings)
        {

        }
    }
}