using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AppForge.Shared
{
    /// <summary>
    /// 内置示例应用（体验模式的 3 张卡片）：
    /// 让用户在没有 API Key 时也能立刻看到"应用能打开、能持久化、能秒开"。
    /// </summary>
    public static class SeedData
    {
        private sealed class SeedDef
        {
            public string Id;
            public string Name;
            public string Description;
            public string Icon;
            public string Resource;
            public int Width;
            public int Height;
        }

        private static readonly SeedDef[] Defs =
        {
            new SeedDef
            {
                Id = "apdemo0001", Name = "待办清单", Icon = "✅",
                Description = "本地待办：添加、勾选、删除，数据保存在本机",
                Resource = "seed/todo/index.html", Width = 720, Height = 760
            },
            new SeedDef
            {
                Id = "apdemo0002", Name = "番茄钟", Icon = "🍅",
                Description = "25 分钟专注 + 5 分钟休息，自动记录完成轮数",
                Resource = "seed/pomodoro/index.html", Width = 640, Height = 640
            },
            new SeedDef
            {
                Id = "apdemo0003", Name = "单位换算", Icon = "🔢",
                Description = "长度/重量/温度快速换算，输入即出结果",
                Resource = "seed/converter/index.html", Width = 760, Height = 620
            }
        };

        /// <summary>首次运行时写入示例应用；返回是否执行了写入。</summary>
        public static bool EnsureSeeded(Workspace ws, AppRepository repo)
        {
            var marker = Path.Combine(ws.DataDir, ".seeded");
            if (File.Exists(marker)) return false;
            if (repo.Count > 0)
            {
                SafeWriteMarker(marker);
                return false;
            }

            foreach (var def in Defs)
            {
                try { CreateApp(ws, repo, def); }
                catch { /* 单个示例失败不影响启动 */ }
            }

            SafeWriteMarker(marker);
            return true;
        }

        private static void SafeWriteMarker(string marker)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker));
                File.WriteAllText(marker, TimeUtil.Now().ToString());
            }
            catch { /* 忽略 */ }
        }

        private static void CreateApp(Workspace ws, AppRepository repo, SeedDef def)
        {
            var versionDir = ws.VersionDir(def.Id, 1);
            Directory.CreateDirectory(versionDir);

            var html = ReadResource(def.Resource);
            if (html == null) throw new InvalidOperationException("缺少内置示例资源：" + def.Resource);
            File.WriteAllText(Path.Combine(versionDir, "index.html"), html, new UTF8Encoding(false));

            var manifest = new Manifest
            {
                Id = def.Id,
                Name = def.Name,
                Description = def.Description,
                Type = AppTypes.StaticWeb,
                Entry = "index.html",
                Icon = def.Icon,
                Offline = true,
                Window = new Manifest.WindowSpec
                {
                    Width = def.Width,
                    Height = def.Height,
                    MinWidth = 360,
                    MinHeight = 360,
                    Resizable = true
                }
            };
            File.WriteAllText(Path.Combine(versionDir, "manifest.json"), manifest.ToJson(), new UTF8Encoding(false));

            var now = TimeUtil.Now();
            var meta = new AppMeta
            {
                Id = def.Id,
                Name = def.Name,
                Description = def.Description,
                Type = AppTypes.StaticWeb,
                Icon = def.Icon,
                CurrentVersion = 1,
                Status = "ready",
                Pinned = false,
                LaunchCount = 0,
                LastOpenedAt = 0,
                CreatedAt = now,
                UpdatedAt = now,
                Bytes = JsonStore.DirectoryBytes(versionDir)
            };
            repo.Upsert(meta);
        }

        private static string ReadResource(string logicalName)
        {
            var asm = typeof(SeedData).Assembly;
            var wanted = logicalName.Replace('\\', '/');
            var actual = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.Replace('\\', '/').Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (actual == null) return null;

            using (var stream = asm.GetManifestResourceStream(actual))
            {
                if (stream == null) return null;
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        public static IList<string> ListResourceNames()
        {
            return typeof(SeedData).Assembly.GetManifestResourceNames().ToList();
        }
    }
}
