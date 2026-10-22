using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AppForge.Shared
{
    /// <summary>
    /// 应用库：内存索引 + 文件落盘（index.json 原子写、每日快照、app.json 冗余重建）。
    /// 单写者模型（宿主进程），所有写入串行化。
    /// </summary>
    public sealed class AppRepository
    {
        private readonly Workspace _ws;
        private readonly object _gate = new object();
        private IndexFile _index;
        private bool _loaded;

        public AppRepository(Workspace workspace)
        {
            _ws = workspace;
            _index = new IndexFile();
        }

        public int Count
        {
            get { lock (_gate) return _index.Apps.Count; }
        }

        public void Load()
        {
            lock (_gate)
            {
                var idx = ReadIndex();
                if (idx == null)
                {
                    idx = Rebuild();
                    _index = idx;
                    SaveUnlocked();
                }
                else
                {
                    _index = idx;
                    if (_index.Apps.Count == 0)
                    {
                        var rebuilt = Rebuild();
                        if (rebuilt.Apps.Count > 0)
                        {
                            _index = rebuilt;
                            SaveUnlocked();
                        }
                    }
                }
                _loaded = true;
            }
        }

        private IndexFile ReadIndex()
        {
            try
            {
                var json = JsonStore.ReadOrNull(_ws.IndexPath);
                if (string.IsNullOrWhiteSpace(json)) return null;
                var idx = JsonLite.Deserialize<IndexFile>(json);
                if (idx == null || idx.Apps == null) return null;
                idx.Apps = idx.Apps.Where(a => a != null && AppId.IsValid(a.Id)).ToList();
                return idx;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>索引损坏/丢失时，从 apps/&lt;id&gt;/app.json 重建（自愈）。</summary>
        public IndexFile Rebuild()
        {
            var idx = new IndexFile();
            if (!Directory.Exists(_ws.AppsDir)) return idx;

            foreach (var dir in Directory.GetDirectories(_ws.AppsDir))
            {
                var appJson = Path.Combine(dir, "app.json");
                try
                {
                    var json = JsonStore.ReadOrNull(appJson);
                    if (string.IsNullOrWhiteSpace(json)) continue;
                    var meta = JsonLite.Deserialize<AppMeta>(json);
                    if (meta == null || !AppId.IsValid(meta.Id)) continue;
                    if (meta.CurrentVersion <= 0) continue;
                    idx.Apps.Add(meta);
                }
                catch
                {
                    // 单个应用损坏不影响整体
                }
            }
            return idx;
        }

        public void Save()
        {
            lock (_gate)
            {
                if (!_loaded) return;
                SaveUnlocked();
            }
        }

        private void SaveUnlocked()
        {
            JsonStore.WriteAtomic(_ws.IndexPath, JsonLite.Serialize(_index));

            // 每日快照（保留 7 份）
            try
            {
                Directory.CreateDirectory(_ws.BackupsDir);
                var snapshot = Path.Combine(_ws.BackupsDir, "index-" + TimeUtil.FileStamp() + ".json");
                if (!File.Exists(snapshot)) File.Copy(_ws.IndexPath, snapshot, true);

                var old = Directory.GetFiles(_ws.BackupsDir, "index-*.json")
                    .OrderByDescending(f => f, StringComparer.Ordinal)
                    .Skip(7)
                    .ToList();
                foreach (var f in old) JsonStore.TryDelete(f);
            }
            catch
            {
                // 快照失败不影响主流程
            }
        }

        public IList<AppMeta> List(string search)
        {
            lock (_gate)
            {
                IEnumerable<AppMeta> items = _index.Apps;

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var q = search.Trim();
                    items = items.Where(a =>
                        Contains(a.Name, q) || Contains(a.Description, q) || Contains(a.Id, q));
                }

                return items
                    .OrderByDescending(a => a.Pinned)
                    .ThenByDescending(a => a.LastOpenedAt)
                    .ThenByDescending(a => a.UpdatedAt)
                    .ToList();
            }
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public AppMeta Get(string id)
        {
            if (!AppId.IsValid(id)) return null;
            lock (_gate) return _index.Apps.FirstOrDefault(a => a.Id == id);
        }

        /// <summary>写入/更新一个应用（同时写 app.json 与索引）。</summary>
        public void Upsert(AppMeta meta)
        {
            if (meta == null || !AppId.IsValid(meta.Id))
                throw new ArgumentException("非法的应用元数据");

            lock (_gate)
            {
                meta.UpdatedAt = TimeUtil.Now();
                var existing = _index.Apps.FindIndex(a => a.Id == meta.Id);
                if (existing >= 0) _index.Apps[existing] = meta;
                else _index.Apps.Add(meta);

                Directory.CreateDirectory(_ws.AppDir(meta.Id));
                JsonStore.WriteAtomic(_ws.AppMetaPath(meta.Id), JsonLite.Serialize(meta));
                SaveUnlocked();
            }
        }

        public void Rename(string id, string name)
        {
            var app = Get(id);
            if (app == null) throw new KeyNotFoundException("应用不存在：" + id);
            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0) throw new ArgumentException("名称不能为空");
            if (trimmed.Length > 40) throw new ArgumentException("名称不能超过 40 字");
            app.Name = trimmed;
            Upsert(app);
        }

        public void Pin(string id, bool pinned)
        {
            var app = Get(id);
            if (app == null) throw new KeyNotFoundException("应用不存在：" + id);
            app.Pinned = pinned;
            Upsert(app);
        }

        /// <summary>打开一次：计数 + 记录时间（用于"最近使用"排序）。</summary>
        public void TouchOpen(string id)
        {
            var app = Get(id);
            if (app == null) return;
            app.LaunchCount = app.LaunchCount + 1;
            app.LastOpenedAt = TimeUtil.Now();

            lock (_gate)
            {
                var existing = _index.Apps.FindIndex(a => a.Id == id);
                if (existing >= 0) _index.Apps[existing] = app;
                JsonStore.WriteAtomic(_ws.AppMetaPath(id), JsonLite.Serialize(app));
                SaveUnlocked();
            }
        }

        /// <summary>删除应用：目录移入回收站（不立即物理删除）。</summary>
        public void Delete(string id)
        {
            var app = Get(id);
            if (app == null) throw new KeyNotFoundException("应用不存在：" + id);

            lock (_gate)
            {
                var dir = _ws.AppDir(id);
                if (Directory.Exists(dir))
                {
                    Directory.CreateDirectory(_ws.TrashDir);
                    var target = Path.Combine(_ws.TrashDir, id + "-" + TimeUtil.Now());
                    try { Directory.Move(dir, target); }
                    catch { try { Directory.Delete(dir, true); } catch { /* 忽略 */ } }
                }

                _index.Apps.RemoveAll(a => a.Id == id);
                SaveUnlocked();
            }
        }

        public Manifest ReadManifest(AppMeta app)
        {
            if (app == null || app.CurrentVersion <= 0) return null;
            var path = Path.Combine(_ws.VersionDir(app.Id, app.CurrentVersion), "manifest.json");
            var json = JsonStore.ReadOrNull(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return Manifest.FromJson(json); }
            catch { return null; }
        }

        /// <summary>应用入口的绝对路径（经路径守卫）。</summary>
        public string ResolveEntry(AppMeta app, Manifest manifest)
        {
            var dir = _ws.VersionDir(app.Id, app.CurrentVersion);
            var entry = manifest != null && !string.IsNullOrWhiteSpace(manifest.Entry) ? manifest.Entry : "index.html";
            return PathGuard.ResolveWithin(dir, entry);
        }

        public string CurrentVersionDir(AppMeta app)
        {
            return _ws.VersionDir(app.Id, app.CurrentVersion);
        }
    }
}
