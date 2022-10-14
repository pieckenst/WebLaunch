﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using XIVLauncher.Common.PlatformAbstractions;

namespace CoreLibLaunchSupport
{
    public class DalamudOverlayInfoProxy : IDalamudLoadingOverlay
    {
        public bool IsVisible { get; private set; }

        public IDalamudLoadingOverlay.DalamudUpdateStep Step { get; private set; }

        public void SetStep(IDalamudLoadingOverlay.DalamudUpdateStep step)
        {
            this.Step = step;
        }

        public void SetVisible()
        {
            this.IsVisible = true;
        }

        public void SetInvisible()
        {
            this.IsVisible = true;
        }

        public void ReportProgress(long? size, long downloaded, double? progress)
        {
            // TODO
        }
    }
    public class Storage
    {
        public DirectoryInfo Root { get; }

        public Storage(string appName, string? overridePath = null)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                this.Root = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), appName));
            }
            else
            {
                this.Root = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $".{appName}"));
            }

            if (!string.IsNullOrEmpty(overridePath))
            {
                this.Root = new DirectoryInfo(overridePath);
            }

            if (!this.Root.Exists)
                this.Root.Create();
        }

        public FileInfo GetFile(string fileName)
        {
            return new FileInfo(Path.Combine(this.Root.FullName, fileName));
        }

        /// <summary>
        /// Gets a folder and makes sure that it exists.
        /// </summary>
        /// <param name="folderName"></param>
        /// <returns></returns>
        public DirectoryInfo GetFolder(string folderName)
        {
            var folder = new DirectoryInfo(Path.Combine(this.Root.FullName, folderName));

            if (!folder.Exists)
                folder.Create();

            return folder;
        }
    }
    public class CommonUniqueIdCache : IUniqueIdCache
    {
        private const int DAYS_TO_TIMEOUT = 1;

        private List<UniqueIdCacheEntry> _cache;

        public CommonUniqueIdCache(FileInfo saveFile)
        {
            this.configFile = saveFile;

            Load();
        }

        #region SaveLoad

        private readonly FileInfo configFile;

        public void Save()
        {
            File.WriteAllText(configFile.FullName, JsonConvert.SerializeObject(_cache, Formatting.Indented));
        }

        public void Load()
        {
            if (!File.Exists(configFile.FullName))
            {
                _cache = new List<UniqueIdCacheEntry>();
                return;
            }

            _cache = JsonConvert.DeserializeObject<List<UniqueIdCacheEntry>>(File.ReadAllText(configFile.FullName)) ?? new List<UniqueIdCacheEntry>();
        }

        public void Reset()
        {
            _cache.Clear();
            Save();
        }

        #endregion

        private void DeleteOldCaches()
        {
            _cache.RemoveAll(entry => (DateTime.Now - entry.CreationDate).TotalDays > DAYS_TO_TIMEOUT);
        }

        public bool HasValidCache(string userName)
        {
            return _cache.Any(entry => IsValidCache(entry, userName));
        }

        public void Add(string userName, string uid, int region, int expansionLevel)
        {
            _cache.Add(new UniqueIdCacheEntry
            {
                CreationDate = DateTime.Now,
                UserName = userName,
                UniqueId = uid,
                Region = region,
                ExpansionLevel = expansionLevel
            });

            Save();
        }

        public bool TryGet(string userName, out IUniqueIdCache.CachedUid cached)
        {
            DeleteOldCaches();

            var cache = _cache.FirstOrDefault(entry => IsValidCache(entry, userName));

            if (cache == null)
            {
                cached = default;
                return false;
            }

            cached = new IUniqueIdCache.CachedUid
            {
                UniqueId = cache.UniqueId,
                Region = cache.Region,
                MaxExpansion = cache.ExpansionLevel,
            };
            return true;
        }

        private bool IsValidCache(UniqueIdCacheEntry entry, string name) => entry.UserName == name &&
                                                                            (DateTime.Now - entry.CreationDate).TotalDays <=
                                                                            DAYS_TO_TIMEOUT;

        public class UniqueIdCacheEntry
        {
            public string UserName { get; set; }
            public string UniqueId { get; set; }
            public int Region { get; set; }
            public int ExpansionLevel { get; set; }

            public DateTime CreationDate { get; set; }
        }
    }
}
