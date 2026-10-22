using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AppForge.Shared
{
    /// <summary>单个版本的信息（来自 versions\vNNNN\version.json，缺失时从目录推断）。</summary>
    public sealed class VersionMeta
    {
        public int Seq { get; set; }
        public string Note { get; set; }
        public string Kind { get; set; }      // create | modify
        public long CreatedAt { get; set; }
        public string Prompt { get; set; }
        public string Model { get; set; }
        public string Entry { get; set; }
        public long Bytes { get; set; }

        public IDictionary<string, object> ToJson()
        {
            var d = JsonLite.New();
            d["seq"] = Seq;
            d["note"] = Note ?? string.Empty;
            d["kind"] = Kind ?? "create";
            d["createdAt"] = CreatedAt;
            d["prompt"] = TextUtil.Ellipsis(Prompt, 300);
            d["model"] = Model ?? string.Empty;
            d["entry"] = Entry ?? "index.html";
            d["bytes"] = Bytes;
            return d;
        }

        public static VersionMeta FromJson(string json)
        {
            var d = JsonLite.ParseObject(json);
            return new VersionMeta
            {
                Seq = JsonLite.Int(d, "seq"),
                Note = JsonLite.Str(d, "note") ?? string.Empty,
                Kind = JsonLite.Str(d, "kind") ?? "create",
                CreatedAt = JsonLite.Long(d, "createdAt"),
                Prompt = JsonLite.Str(d, "prompt") ?? string.Empty,
                Model = JsonLite.Str(d, "model") ?? string.Empty,
                Entry = JsonLite.Str(d, "entry") ?? "index.html",
                Bytes = JsonLite.Long(d, "bytes")
            };
        }
    }

    /// <summary>
    /// 版本服务：每个应用每次"跑通"都落一个 vNNNN 快照；支持列出、回滚、按保留策略清理。
    /// 版本目录即完整应用快照（含 manifest.json + version.json + 全部文件）。
    /// </summary>
    public sealed class VersionService
    {
        private readonly Workspace _ws;
        private readonly AppRepository _repo;

        public VersionService(Workspace workspace, AppRepository repo)
        {
            _ws = workspace;
            _repo = repo;
        }

        public IList<VersionMeta> List(string appId)
        {
            var result = new List<VersionMeta>();
            var versionsDir = _ws.VersionsDir(appId);
            if (!Directory.Exists(versionsDir)) return result;

            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                int seq;
                if (!TryParseSeq(Path.GetFileName(dir), out seq)) continue;
                result.Add(ReadAt(dir, seq));
            }

            return result.OrderByDescending(v => v.Seq).ToList();
        }

        public VersionMeta Read(string appId, int seq)
        {
            var dir = _ws.VersionDir(appId, seq);
            return Directory.Exists(dir) ? ReadAt(dir, seq) : null;
        }

        private static VersionMeta ReadAt(string dir, int seq)
        {
            var meta = new VersionMeta { Seq = seq, Kind = "create", Entry = "index.html" };
            var json = JsonStore.ReadOrNull(Path.Combine(dir, "version.json"));
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = VersionMeta.FromJson(json);
                    if (parsed != null)
                    {
                        parsed.Seq = seq;
                        meta = parsed;
                    }
                }
                catch
                {
                    // 损坏则回退到目录推断
                }
            }

            if (meta.CreatedAt == 0)
            {
                try { meta.CreatedAt = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir)).ToUnixTimeMilliseconds(); }
                catch { meta.CreatedAt = 0; }
            }

            try
            {
                var manifestJson = JsonStore.ReadOrNull(Path.Combine(dir, "manifest.json"));
                if (!string.IsNullOrWhiteSpace(manifestJson))
                {
                    var manifest = Manifest.FromJson(manifestJson);
                    if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Entry)) meta.Entry = manifest.Entry;
                }
            }
            catch
            {
                // 忽略
            }

            meta.Bytes = JsonStore.DirectoryBytes(dir);
            return meta;
        }

        public void WriteMeta(string versionDir, VersionMeta meta)
        {
            JsonStore.WriteAtomic(Path.Combine(versionDir, "version.json"),
                JsonLite.Serialize(meta.ToJson()));
        }

        public int NextSeq(string appId)
        {
            var list = List(appId);
            return list.Count == 0 ? 1 : list.Max(v => v.Seq) + 1;
        }

        /// <summary>回滚：把 currentVersion 指回指定版本（不删除新版本）。</summary>
        public AppMeta Rollback(string appId, int seq)
        {
            var app = _repo.Get(appId);
            if (app == null) throw new KeyNotFoundException("应用不存在：" + appId);

            var dir = _ws.VersionDir(appId, seq);
            if (!Directory.Exists(dir)) throw new KeyNotFoundException("版本不存在：v" + seq.ToString("0000"));

            app.CurrentVersion = seq;
            app.UpdatedAt = TimeUtil.Now();
            app.Bytes = JsonStore.DirectoryBytes(dir);
            _repo.Upsert(app);
            return app;
        }

        /// <summary>按保留策略清理旧版本（当前版本与 protectedSeqs 永不删除）。</summary>
        public IList<int> Prune(string appId, int keep, IEnumerable<int> protectedSeqs = null)
        {
            var removed = new List<int>();
            var list = List(appId).OrderByDescending(v => v.Seq).ToList();
            if (list.Count <= keep) return removed;

            var app = _repo.Get(appId);
            var currentSeq = app != null ? app.CurrentVersion : -1;
            var protect = new HashSet<int>(protectedSeqs ?? new int[0]) { currentSeq };

            for (var i = keep; i < list.Count; i++)
            {
                var version = list[i];
                if (protect.Contains(version.Seq)) continue;
                try
                {
                    Directory.Delete(_ws.VersionDir(appId, version.Seq), true);
                    removed.Add(version.Seq);
                }
                catch
                {
                    // 清理失败不影响主流程
                }
            }
            return removed;
        }

        private static bool TryParseSeq(string dirName, out int seq)
        {
            seq = 0;
            if (string.IsNullOrEmpty(dirName) || dirName.Length < 2) return false;
            if (dirName[0] != 'v' && dirName[0] != 'V') return false;
            return int.TryParse(dirName.Substring(1), out seq) && seq > 0;
        }
    }
}
