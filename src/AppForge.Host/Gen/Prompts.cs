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
                "每个文件一次写完——不要用「写一半再追加」的方式拼半个文件。同一轮可以返回多个 write_file 调用，" +
                "但总输出不要超过单轮上限；短文件可同一轮一起写。",
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
                "所有文件写完、且逐条核对需求都实现后才能调用。写完最后一个文件的同一轮里立刻调用 done，不要再多等一轮。",
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
                "1. 简单应用（预计 ≤250 行）可以只写一个 index.html；稍复杂的应用一开始就拆成 index.html + style.css + app.js，" +
                "需要更多模块时再拆 calc.js/tools.js 等，总文件数 ≤ 8。不要先把所有 CSS/JS 都塞进一个巨大的 index.html 再被截断。",
                "2. 禁止引用任何外部资源（不要 CDN、不要外链图片/字体/脚本）；图标用 emoji 或内联 SVG。",
                "3. 数据持久化只能用 localStorage，key 必须带前缀：app:<appId>:。",
                "4. 单个 write_file 的 content ≤400 行；单文件 ≤1200 行、≤200KB；整体 ≤ 8 个文件。超过就继续拆模块。",
                "5. 已经正确、且本次不需要改的文件不要重写；不要把同一个文件整篇重写两遍。",
                "6. 页面必须有 <body data-app-root> 或主容器带 data-app-root 属性；body 里必须有可见内容。",
                "7. 交互逻辑必须真的写出来：按钮要有 JS 事件绑定，数据要读写 localStorage；不能只给静态界面或占位文字。",
                "8. 所有初始化逻辑用 try/catch 包裹；不得抛未捕获异常；DOM 操作前判空。",
                "9. 界面使用中文；跟随系统深浅色（prefers-color-scheme）；布局适配 900px 宽窗口。",
                "10. 禁止 eval 远程代码、禁止读写本地文件、禁止后台自启、禁止自动下载可执行文件。",
                "",
                "【完成标准（必须逐条满足）】",
                "· set_app_info 的 description 只能写实际已经实现的功能；描述里写到的每个工具/页面/按钮都必须真的能用。",
                "· 工具箱/多模块应用：每个 tab、每个主要按钮、每个输入控件都必须有真实逻辑，不能只有占位文字。",
                "· 调 done 前先逐条核对用户需求：缺页面、缺按钮、缺事件、缺持久化、缺本地引用文件，都算未完成。",
                "· 发现缺功能就继续写，先把功能补齐；不要用“后续可扩展”之类的话代替实现。",
                "",
                "【工作方式】",
                "· 第一次回复同时给出 set_app_info 和第一个文件（同一轮多个工具调用）；第一个文件优先写入口骨架，不要一上来就塞满 400 行；",
                "· 后续每轮优先写完 1~2 个完整文件；短文件（如 style.css + app.js）可以在同一轮一起写；大文件拆模块，不要用 append 拼半个文件；",
                "· 需要确认时用 read_file / list_files；",
                "· 最后一个文件写完后，在同一轮立刻调用 done（不要再等一轮）；",
                "· 只调用工具，不要在正文里贴大段代码；不要反复重写已完成文件。"
            });
        }

        private static string MissingText(IList<string> missing)
        {
            return missing == null || missing.Count == 0
                ? string.Empty
                : "；缺少被引用文件：" + string.Join("、", missing);
        }

        /// <summary>
        /// 模型只回复文字、但入口还不完整：继续写，不许把"说完了"当成"做完了"。
        /// </summary>
        public static string ContinueIncompletePrompt(string entry, IList<string> missing)
        {
            return "现在还不能结束：" + entry + " 尚未达到可运行状态" + MissingText(missing) + "。\n" +
                   "请继续使用工具完成应用，不要只回复说明文字，也不要调用 done。\n" +
                   "先 list_files / read_file 检查已有内容，再补齐缺失文件和交互逻辑；全部完成后立即调用 done。";
        }

        /// <summary>
        /// 模型过早调用 done：拒绝收尾，明确告诉它还缺什么。
        /// </summary>
        public static string DoneRejectedPrompt(string entry, IList<string> missing)
        {
            return "done 已拒绝：" + entry + " 还不是完整可运行的应用" + MissingText(missing) + "。\n" +
                   "入口必须闭合、包含 data-app-root、包含脚本，并且本地引用的 js/css 都必须存在。\n" +
                   "不要重写已经完成的部分，只补齐缺失内容；补齐后再调用 done。";
        }

        /// <summary>
        /// 基础轮次用尽但应用未完成：自动续轮，禁止用"精简版"替换已完成功能。
        /// </summary>
        public static string ExtendBudgetPrompt(int extensionNo, int remainingThisExtension, string entry, IList<string> missing)
        {
            return "应用尚未完成，轮次预算已自动延长（第 " + extensionNo + " 次，本档还剩 " + remainingThisExtension + " 轮）。\n" +
                   "当前入口：" + entry + MissingText(missing) + "。\n" +
                   "禁止收尾、禁止简化功能、禁止把已经写好的大文件整篇重写成精简版。\n" +
                   "请继续实现尚未完成的需求：缺文件就写文件，缺交互就补脚本；完成后立即调用 done。";
        }

        /// <summary>
        /// 文件看起来完整、但模型还没明确 done 时的最后核对。避免把“骨架能打开”误判成完成。
        /// </summary>
        public static string CompletionAuditPrompt(string entry)
        {
            return "收尾前做一次完整性核对，不要直接结束：\n" +
                   "1. 逐条对照用户需求，以及应用自己展示的标题、副标题、每个 tab、按钮、输入框和说明文字；\n" +
                   "   凡是文字里宣称的功能（例如工具数量、导出、筛选、格式化、校验、持久化）都必须有真实实现，不能只有占位按钮或空函数。\n" +
                   "2. 特别检查：set_app_info 的 description、首页副标题、tab 名称里列出的功能，是否都能在 JS 里找到对应逻辑并真正生效；\n" +
                   "   例如宣称有 7 个工具却只实现了 4 个，必须补齐剩余 3 个后才能收尾。\n" +
                   "3. 如果还缺功能，立刻用 write_file 补齐（只改相关文件，不要重写已完成文件），不要只回复文字；\n" +
                   "4. 如果发现入口引用的本地 js/css 缺失，也先补齐；\n" +
                   "5. 只有全部实现、且 " + entry + " 可完整运行时，才调用 done。\n" +
                   "如果确实全部完成，请再次调用 done，并用一句话说明已核对哪些功能；如果刚调用过 done，这一步是必须的复核。";
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

        /// <summary>
        /// 入口文件里引用了还没写出来的 js/css：应用一定跑不起来（白屏 / 点击无反应）。
        /// 这时要明确催模型补齐，而不是当成"原地打转"提前收尾。
        /// </summary>
        public static string MissingFilesPrompt(string entry, IList<string> missing)
        {
            var list = missing == null || missing.Count == 0 ? "被引用的文件" : string.Join("、", missing);
            return entry + " 里引用了 " + list + "，但这些文件还没有写出来，应用现在打开会缺资源（白屏或没有交互）。\n" +
                   "请用 write_file 把它们补齐（每个文件一次写完整），或者把 " + entry + " 里对应的引用去掉。\n" +
                   "补齐后再调用 done；不要重复重写已经写好的文件。";
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
