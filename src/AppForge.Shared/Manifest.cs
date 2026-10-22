using System;
using System.Collections.Generic;
using System.IO;

namespace AppForge.Shared
{
    /// <summary>
    /// 应用清单：宿主与生成器之间唯一的契约。
    /// 宿主只认 manifest.json 里声明的入口/尺寸/能力，不猜测应用内部结构。
    /// </summary>
    public sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public string Entry { get; set; }
        public string Icon { get; set; }
        public WindowSpec Window { get; set; }
        public List<string> Capabilities { get; set; }
        public bool Offline { get; set; }
        public BuildSpec Build { get; set; }

        public Manifest()
        {
            SchemaVersion = Defaults.SchemaVersion;
            Type = AppTypes.StaticWeb;
            Entry = "index.html";
            Offline = true;
            Capabilities = new List<string> { "storage" };
            Window = new WindowSpec();
            Icon = "🧩";
        }

        public sealed class WindowSpec
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int MinWidth { get; set; }
            public int MinHeight { get; set; }
            public bool Resizable { get; set; }

            public WindowSpec()
            {
                Width = 960;
                Height = 720;
                MinWidth = 480;
                MinHeight = 360;
                Resizable = true;
            }
        }

        public sealed class BuildSpec
        {
            public string Cmd { get; set; }
            public string OutDir { get; set; }
        }

        /// <summary>输出 camelCase 契约（与 02 架构 §8.4 一致）。</summary>
        public string ToJson()
        {
            var d = JsonLite.New();
            d["schemaVersion"] = SchemaVersion;
            d["id"] = Id;
            d["name"] = Name;
            d["description"] = Description ?? string.Empty;
            d["type"] = Type;
            d["entry"] = Entry;
            d["icon"] = Icon ?? "🧩";
            d["offline"] = Offline;

            var capabilities = new List<object>();
            if (Capabilities != null)
            {
                foreach (var capability in Capabilities) capabilities.Add(capability);
            }
            d["capabilities"] = capabilities;

            if (Window != null)
            {
                var w = JsonLite.New();
                w["width"] = Window.Width;
                w["height"] = Window.Height;
                w["minWidth"] = Window.MinWidth;
                w["minHeight"] = Window.MinHeight;
                w["resizable"] = Window.Resizable;
                d["window"] = w;
            }

            if (Build != null)
            {
                var b = JsonLite.New();
                b["cmd"] = Build.Cmd;
                b["outDir"] = Build.OutDir;
                d["build"] = b;
            }
            else
            {
                d["build"] = null;
            }

            return JsonLite.Serialize(d);
        }

        /// <summary>读取清单：兼容 camelCase 与历史 PascalCase。</summary>
        public static Manifest FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var d = JsonLite.ParseObject(json);
            if (d.Count == 0) return null;

            var manifest = new Manifest
            {
                SchemaVersion = ToInt(Raw(d, "schemaVersion"), 1),
                Id = ToStr(Raw(d, "id")),
                Name = ToStr(Raw(d, "name")),
                Description = ToStr(Raw(d, "description")),
                Type = ToStr(Raw(d, "type")),
                Entry = ToStr(Raw(d, "entry")),
                Icon = ToStr(Raw(d, "icon")),
                Offline = ToBool(Raw(d, "offline"), true)
            };

            var capabilities = Raw(d, "capabilities") as System.Collections.IList;
            if (capabilities != null)
            {
                manifest.Capabilities = new List<string>();
                foreach (var item in capabilities)
                {
                    if (item != null) manifest.Capabilities.Add(item.ToString());
                }
            }

            var window = Raw(d, "window") as IDictionary<string, object>;
            if (window != null)
            {
                manifest.Window = new WindowSpec
                {
                    Width = ToInt(Raw(window, "width"), 960),
                    Height = ToInt(Raw(window, "height"), 720),
                    MinWidth = ToInt(Raw(window, "minWidth"), 480),
                    MinHeight = ToInt(Raw(window, "minHeight"), 360),
                    Resizable = ToBool(Raw(window, "resizable"), true)
                };
            }

            var build = Raw(d, "build") as IDictionary<string, object>;
            if (build != null && !string.IsNullOrEmpty(ToStr(Raw(build, "cmd"))))
            {
                manifest.Build = new BuildSpec
                {
                    Cmd = ToStr(Raw(build, "cmd")),
                    OutDir = ToStr(Raw(build, "outDir"))
                };
            }

            return manifest;
        }

        private static object Raw(IDictionary<string, object> dict, string key)
        {
            if (dict == null || key == null) return null;
            object direct;
            if (dict.TryGetValue(key, out direct)) return direct;
            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return null;
        }

        private static string ToStr(object value)
        {
            return value == null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static int ToInt(object value, int fallback)
        {
            if (value == null) return fallback;
            try { return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        private static bool ToBool(object value, bool fallback)
        {
            if (value == null) return fallback;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out parsed) ? parsed : fallback;
        }

        /// <summary>纯结构校验（不碰磁盘）。</summary>
        public IList<string> ValidateShape()
        {
            var errors = new List<string>();

            if (SchemaVersion != Defaults.SchemaVersion) errors.Add("schemaVersion 必须是 " + Defaults.SchemaVersion);
            if (string.IsNullOrWhiteSpace(Id)) errors.Add("缺少 id");
            else if (!AppId.IsValid(Id)) errors.Add("id 格式非法（应为 ap 开头的 10 位小写字母数字）");
            if (string.IsNullOrWhiteSpace(Name)) errors.Add("缺少 name");
            if (Name != null && Name.Length > 40) errors.Add("name 过长（≤40 字）");
            if (Type != AppTypes.StaticWeb && Type != AppTypes.NodeScript) errors.Add("type 仅支持 static-web（v1）");
            if (string.IsNullOrWhiteSpace(Entry)) errors.Add("缺少 entry");
            else if (Entry.IndexOf(':') >= 0 || Entry.StartsWith("/") || Entry.StartsWith("\\") || Entry.Contains(".."))
                errors.Add("entry 必须是版本目录内的相对路径");
            else if (Type == AppTypes.StaticWeb && !Entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                errors.Add("static-web 的 entry 必须是 .html");
            if (!Offline) errors.Add("v1 要求 offline 必须为 true（禁止外链资源）");
            if (Window != null)
            {
                if (Window.Width < 200 || Window.Width > 4096) errors.Add("window.width 需在 200~4096");
                if (Window.Height < 200 || Window.Height > 4096) errors.Add("window.height 需在 200~4096");
            }
            return errors;
        }

        /// <summary>结构 + 磁盘校验：入口文件必须真实存在。</summary>
        public IList<string> ValidateOnDisk(string versionDir)
        {
            var errors = new List<string>(ValidateShape());
            if (errors.Count > 0) return errors;
            try
            {
                var entryPath = PathGuard.ResolveWithin(versionDir, Entry);
                if (!File.Exists(entryPath)) errors.Add("entry 指向的文件不存在：" + Entry);
            }
            catch (PathGuardException ex)
            {
                errors.Add("entry 路径越界：" + ex.Message);
            }
            return errors;
        }
    }
}
