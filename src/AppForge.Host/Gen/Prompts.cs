using System;
using System.Collections.Generic;
using AppForge.Shared;

namespace AppForge.Host.Gen
{
    /// <summary>生成引擎的提示词与工具定义（与 02 架构 9.4 生成规范一致）。</summary>
    public static class Prompts
    {
        public static IList<object> BuildTools()
        {
            var tools = new List<object>();

            tools.Add(Tool("set_app_info",
                "设置应用名称、描述与图标。必须在写文件之前调用一次。",
                new Dictionary<string, object>
                {
                    { "name", Prop("string", "应用名称，4~12 个中文字符") },
                    { "description", Prop("string", "一句话描述，20~40 字") },
                    { "icon", Prop("string", "一个 emoji 作为图标，例如 ✅") }
                },
                new[] { "name", "description", "icon" }));

            tools.Add(Tool("write_file",
                "写入（或覆盖/追加）草稿目录里的一个文件。一次只写一个文件：append=false 时 content 必须是完整文件内容；" +
                "单次 content ≤400 行。文件更大的话，请把它拆成多个文件（如 index.html + style.css + app.js），" +
                "每个文件一次写完——不要用「写一半再追加」的方式拼半个文件。",
                new Dictionary<string, object>
                {
                    { "path", Prop("string", "相对路径，例如 index.html 或 app.js") },
                    { "content", Prop("string", "文件内容（append=true 时为追加片段）") },
                    { "append", Prop("boolean", "是否追加到文件末尾：true=追加（文件不存在则等同新建），false 或不填=覆盖整个文件") }
                },
                new[] { "path", "content" }));

            tools.Add(Tool("read_file",
                "读取草稿目录里已有文件的内容（用于确认当前代码）。",
                new Dictionary<string, object> { { "path", Prop("string", "相对路径") } },
                new[] { "path" }));

            tools.Add(Tool("list_files",
                "列出草稿目录里已存在的文件。",
                new Dictionary<string, object>(),
                new string[0]));

            tools.Add(Tool("done",
                "所有文件写完后调用，表示生成完成。写完最后一个文件的同一轮里立刻调用 done，不要再多等一轮。",
                new Dictionary<string, object> { { "summary", Prop("string", "一句话说明做了什么") } },
                new string[0]));

            return tools;
        }

        private static object Tool(string name, string description, Dictionary<string, object> properties, string[] required)
        {
            var fn = JsonLite.New();
            fn["name"] = name;
            fn["description"] = description;
            var parameters = JsonLite.New();
            parameters["type"] = "object";
            parameters["properties"] = properties;
            parameters["required"] = new List<object>(required);
            fn["parameters"] = parameters;

            var tool = JsonLite.New();
            tool["type"] = "function";
            tool["function"] = fn;
            return tool;
        }

        private static object Prop(string type, string description)
        {
            var d = JsonLite.New();
            d["type"] = type;
            d["description"] = description;
            return d;
        }

        public static string SystemPrompt()
        {
            return string.Join("\n", new[]
            {
                "你是 AppForge 的应用生成器：根据用户的自然语言需求，现场写出一个可离线运行的网页应用。",
                "",
                "【硬性规范】",
                "1. 默认只写一个 index.html：内联 CSS 与 JS（除非体量确实大才拆成 app.js/style.css，且总文件数 ≤ 8）。",
                "2. 禁止引用任何外部资源（不要 CDN、不要外链图片/字体/脚本）；图标用 emoji 或内联 SVG。",
                "3. 数据持久化只能用 localStorage，key 必须带前缀：app:<appId>:。",
                "4. 单文件 ≤ 1200 行、≤ 200KB；整体 ≤ 8 个文件。",
                "5. 简单应用要精简：能 250 行内写完就别拆页、别过度设计，优先保证一次写完且可运行。",
                "6. 每个文件一次写完（≤400 行）；内容多就拆文件：CSS 放 style.css、JS 放 app.js，index.html 里用相对路径引用。" +
                "不要写「半个文件」，也不要把同一个文件整篇重写两遍。",
                "7. 页面必须有 <body data-app-root> 或主容器带 data-app-root 属性；body 里必须有可见内容。",
                "8. 交互逻辑必须真的写出来：按钮要有 JS 事件绑定，数据要读写 localStorage；不能只给静态界面。",
                "9. 所有初始化逻辑用 try/catch 包裹；不得抛未捕获异常；DOM 操作前判空。",
                "10. 界面使用中文；跟随系统深浅色（prefers-color-scheme）；布局适配 900px 宽窗口。",
                "11. 禁止 eval 远程代码、禁止读写本地文件、禁止后台自启、禁止自动下载可执行文件。",
                "",
                "【工作方式】",
                "· 第一次回复请同时给出 set_app_info 和第一个文件（同一轮多个工具调用），减少往返；",
                "· 然后用 write_file 逐个写入文件（一次一个，每个文件都是完整内容）；",
                "· 文件太大就拆成 index.html / style.css / app.js 三个，而不是写骨架再追加；",
                "· 需要确认时用 read_file / list_files；",
                "· 最后一个文件写完后，在同一轮立刻调用 done（不要再等一轮）；",
                "· 只调用工具，不要在正文里贴大段代码。"
            });
        }

        /// <summary>轮次即将用尽时的收尾提示（final=true 更强制，用在最后一轮）。</summary>
        public static string WrapUpPrompt(bool final)
        {
            if (!final)
            {
                return "时间/轮次即将用尽：请立刻收尾——确保 index.html 入口完整可运行，然后调用 done。";
            }
            return "这是最后一轮：不要再新开文件，也不要整篇重写大文件。请确保 index.html 入口完整可运行" +
                   "（必要时用 write_file 写一个精简但可运行的版本），本轮必须调用 done。";
        }

        /// <summary>响应被输出长度截断后的分段写入引导。</summary>
        public static string SegmentWritePrompt(string path, int lineLimit)
        {
            var target = string.IsNullOrEmpty(path) ? "刚才那个大文件" : path;
            return "你上一次的 write_file（" + target + "）因为响应被输出长度截断，内容不完整，所以没有写入。请改成：\n" +
                   "· 把内容拆成多个文件写：index.html（结构）+ style.css（样式）+ app.js（逻辑），每个文件 ≤" + lineLimit + " 行、一次写完；\n" +
                   "· 每个文件用 write_file（append=false）写完整内容，不要只写一半就停；\n" +
                   "· 全部写完后再调用 done。不要把同一个大文件整篇重写一遍。";
        }

        public static string UserPrompt(string requirement, string appId)
        {
            return "用户需求：\n" + requirement.Trim() + "\n\n" +
                   "应用 ID（用于 localStorage key 前缀）：app:" + appId + ":\n" +
                   "请按规范生成，先 set_app_info，再写文件，最后 done。";
        }

        public static string RepairPrompt(string appId, string errors, string filesSummary)
        {
            return string.Join("\n", new[]
            {
                "刚才生成的应用运行自检失败，请修复。",
                "",
                "【错误】",
                errors,
                "",
                "【当前文件】",
                filesSummary,
                "",
                "【修复要求】",
                "· 只修改与错误相关的文件，用 write_file 覆盖完整文件；",
                "· 修复后再次调用 done；",
                "· 不要引入外部资源，不要改变应用功能范围。",
                "应用 ID 前缀仍为 app:" + appId + ":"
            });
        }

        /// <summary>增量修改：给出当前文件内容，要求只改相关文件并写完整文件。</summary>
        public static string ModifyPrompt(string requirement, string appId, string appName, string filesDump)
        {
            return string.Join("\n", new[]
            {
                "这是一个已经存在的应用「" + appName + "」，用户要求修改它。",
                "",
                "【用户的修改要求】",
                requirement.Trim(),
                "",
                "【当前文件内容】",
                filesDump,
                "",
                "【修改要求】",
                "· 先调用 set_app_info 保持或微调应用名称与描述（无需变化就照抄原值）；",
                "· 只修改与本次需求相关的文件，未涉及的文件不要动；",
                "· 每次 write_file 必须写完整文件内容（不是 diff 片段）；",
                "· 保持原有功能不回归，不要引入外部资源；",
                "· 改完调用 done。",
                "应用 ID 前缀仍为 app:" + appId + ":"
            });
        }

        /// <summary>把当前版本文件拼成提示词上下文（小文件给全文，大文件截断，总量封顶）。</summary>
        public static string BuildFilesDump(IDictionary<string, string> files, int perFileLimit, int totalLimit)
        {
            var sb = new System.Text.StringBuilder();
            var used = 0;
            foreach (var kv in files)
            {
                var content = kv.Value ?? string.Empty;
                var isLarge = content.Length > perFileLimit;
                var shown = isLarge ? content.Substring(0, perFileLimit) : content;
                var block = "--- 文件：" + kv.Key + (isLarge ? "（已截断，仅显示前 " + perFileLimit + " 字符）" : "") + " ---\n" + shown + "\n";
                if (used + block.Length > totalLimit)
                {
                    sb.AppendLine("--- 其余文件已省略（总量超限） ---");
                    break;
                }
                sb.Append(block);
                used += block.Length;
            }
            return sb.ToString();
        }
    }
}
