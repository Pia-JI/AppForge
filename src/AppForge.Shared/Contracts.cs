using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AppForge.Shared
{
    /// <summary>宿主 ⇄ 前端唯一的 RPC 方法表（前端只允许调用这里登记的方法）。</summary>
    public static class RpcMethods
    {
        public const string Ping = "ui.ping";
        public const string Info = "app.info";
        public const string AppsList = "apps.list";
        public const string AppsGet = "apps.get";
        public const string AppsRename = "apps.rename";
        public const string AppsPin = "apps.pin";
        public const string AppsDelete = "apps.delete";
        public const string LaunchOpen = "launch.open";
        public const string SettingsGet = "settings.get";
        public const string SettingsSet = "settings.set";
        public const string SettingsListModels = "settings.listModels";
        public const string SettingsTestModel = "settings.testModel";
        public const string SettingsSetApiKey = "settings.setApiKey";
        public const string DepStatusMethod = "dep.status";
        public const string OpenDataDir = "app.openDataDir";
        public const string RevealApp = "fs.reveal";
        public const string GenStart = "gen.start";
        public const string GenModify = "gen.modify";
        public const string GenCancel = "gen.cancel";
        public const string VersionsList = "versions.list";
        public const string VersionsRollback = "versions.rollback";
        public const string VersionsDiff = "versions.diff";
        public const string AppsExport = "apps.export";
        public const string AppsShortcut = "apps.shortcut";
        public const string TrashList = "trash.list";
        public const string TrashRestore = "trash.restore";
        public const string DiagInfo = "diag.info";
        public const string DiagExport = "diag.export";
        public const string SystemOpenLogs = "system.openLogs";

        public static readonly string[] All =
        {
            Ping, Info, AppsList, AppsGet, AppsRename, AppsPin, AppsDelete, LaunchOpen,
            SettingsGet, SettingsSet, SettingsListModels, SettingsTestModel, SettingsSetApiKey,
            DepStatusMethod, OpenDataDir, RevealApp, GenStart, GenModify, GenCancel,
            VersionsList, VersionsRollback, VersionsDiff,
            AppsExport, AppsShortcut, TrashList, TrashRestore, DiagInfo, DiagExport, SystemOpenLogs
        };
    }

    public static class ErrorCodes
    {
        public const string BadRequest = "bad_request";
        public const string NotFound = "not_found";
        public const string NotAllowed = "not_allowed";
        public const string Conflict = "conflict";
        public const string Upstream = "upstream_error";
        public const string Internal = "internal_error";
    }

    public static class AppTypes
    {
        public const string StaticWeb = "static-web";
        public const string NodeScript = "node-script";
    }

    /// <summary>虚拟主机：每个应用独立 origin，实现存储隔离。</summary>
    public static class Hosts
    {
        public const string Shell = "appforge.local";

        public static string ForApp(string appId)
        {
            return "app-" + appId + "." + Shell;
        }

        public static string ShellUrl(string path)
        {
            return "https://" + Shell + "/" + (path ?? string.Empty).TrimStart('/');
        }

        public static string AppUrl(string appId, string path)
        {
            return "https://" + ForApp(appId) + "/" + (path ?? string.Empty).TrimStart('/');
        }

        /// <summary>草稿预览/自测用的虚拟主机（每个 run 独立 origin）。</summary>
        public static string ForDraft(string runId)
        {
            return "draft-" + runId + "." + Shell;
        }
    }

    public static class AppId
    {
        private const string Alphabet = "abcdefghijkmnpqrstuvwxyz23456789"; // 去掉易混字符
        private static readonly Regex Valid = new Regex("^ap[a-z0-9]{8}$", RegexOptions.Compiled);

        public static string New()
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var chars = new char[8];
            for (var i = 0; i < 8; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            return "ap" + new string(chars);
        }

        public static bool IsValid(string id)
        {
            return !string.IsNullOrEmpty(id) && Valid.IsMatch(id);
        }

        /// <summary>生成任务 ID（用于草稿目录与草稿虚拟主机名，必须主机名安全）。</summary>
        public static string NewRun()
        {
            var bytes = new byte[8];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var chars = new char[8];
            for (var i = 0; i < 8; i++) chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            return "run" + new string(chars);
        }
    }

    public static class TimeUtil
    {
        public static long Now()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        public static string FileStamp()
        {
            return DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }
    }

    public static class TextUtil
    {
        public static string Ellipsis(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}
