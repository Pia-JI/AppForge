using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AppForge.Shared
{
    public sealed class TrashEntry
    {
        public string AppId { get; set; }
        public string Dir { get; set; }
        public string Name { get; set; }
        public long DeletedAt { get; set; }
        public long Bytes { get; set; }

        public IDictionary<string, object> ToJson()
        {
            var d = JsonLite.New();
            d["appId"] = AppId;
            d["name"] = Name ?? AppId;
            d["deletedAt"] = DeletedAt;
            d["bytes"] = Bytes;
            return d;
        }
    }

    /// <summary>
    /// 回收站：删除应用时目录被移到 data\trash\&lt;appId&gt;-&lt;ts&gt;，
    /// 这里提供列出 / 恢复 / 超期清理（默认 7 天）。
    /// </summary>
    public sealed class TrashService
    {
        private readonly Workspace _ws;
        private readonly AppRepository _repo;

        public TrashService(Workspace workspace, AppRepository repo)
        {
            _ws = workspace;
            _repo = repo;
        }

        public IList<TrashEntry> List()
        {
            var list = new List<TrashEntry>();
            if (!Directory.Exists(_ws.TrashDir)) return list;

            foreach (var dir in Directory.GetDirectories(_ws.TrashDir))
            {
                var name = Path.GetFileName(dir);
                var index = name.LastIndexOf('-');
                if (index <= 0) continue;

                var appId = name.Substring(0, index);
                long stamp;
                long.TryParse(name.Substring(index + 1), out stamp);

                var entry = new TrashEntry
                {
                    AppId = appId,
                    Dir = dir,
                    Name = appId,
                    DeletedAt = stamp,
                    Bytes = JsonStore.DirectoryBytes(dir)
                };

                try
                {
                    var appJson = JsonStore.ReadOrNull(Path.Combine(dir, "app.json"));
                    if (!string.IsNullOrWhiteSpace(appJson))
                    {
                        var meta = JsonLite.Deserialize<AppMeta>(appJson);
                        if (meta != null && !string.IsNullOrWhiteSpace(meta.Name)) entry.Name = meta.Name;
                    }
                }
                catch
                {
                    // 用 appId 兜底
                }

                list.Add(entry);
            }

            return list.OrderByDescending(e => e.DeletedAt).ToList();
        }

        public AppMeta Restore(string appId)
        {
            if (!AppId.IsValid(appId)) throw new ArgumentException("非法的应用 ID");
            if (_repo.Get(appId) != null) throw new InvalidOperationException("应用已存在，无需恢复");

            var entry = List().FirstOrDefault(e => string.Equals(e.AppId, appId, StringComparison.Ordinal));
            if (entry == null) throw new KeyNotFoundException("回收站里没有该应用：" + appId);

            var target = _ws.AppDir(appId);
            if (Directory.Exists(target)) throw new InvalidOperationException("目标目录已存在，无法恢复");

            Directory.CreateDirectory(_ws.AppsDir);
            Directory.Move(entry.Dir, target);

            var json = JsonStore.ReadOrNull(Path.Combine(target, "app.json"));
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException("应用元数据缺失，无法恢复");
            var meta = JsonLite.Deserialize<AppMeta>(json);
            if (meta == null) throw new InvalidOperationException("应用元数据损坏，无法恢复");

            meta.Status = "ready";
            _repo.Upsert(meta);
            return meta;
        }

        /// <summary>清理超过 N 天的回收站条目，返回清理数量。</summary>
        public int Purge(int olderThanDays)
        {
            var purged = 0;
            var cutoff = TimeUtil.Now() - (long)Math.Max(0, olderThanDays) * 24 * 60 * 60 * 1000;
            foreach (var entry in List())
            {
                if (entry.DeletedAt > cutoff) continue;
                try
                {
                    Directory.Delete(entry.Dir, true);
                    purged++;
                }
                catch
                {
                    // 忽略
                }
            }
            return purged;
        }
    }
}
