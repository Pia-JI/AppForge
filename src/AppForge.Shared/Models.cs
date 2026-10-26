﻿using System;
using System.Collections.Generic;

namespace AppForge.Shared
{
    /// <summary>全局设置（可持久化；API Key 不在此处，单独 DPAPI 加密存储）。</summary>
    public sealed class Settings
    {
        public string BaseUrl { get; set; }
        public string Model { get; set; }
        public double Temperature { get; set; }
        public int MaxTokens { get; set; }
        public int TimeoutSeconds { get; set; }
        public int KeepVersions { get; set; }
        public bool FirstRunDone { get; set; }
        public bool AutoStart { get; set; }
        public bool HotkeyEnabled { get; set; }
        public int WindowWidth { get; set; }
        public int WindowHeight { get; set; }
        public int WindowX { get; set; }
        public int WindowY { get; set; }
        public string LastOpenAppId { get; set; }
        /// <summary>设置项结构版本（用于一次性迁移，避免每次启动改写用户显式配置）。</summary>
        public int SettingsRevision { get; set; }

        public static Settings CreateDefault()
        {
            return new Settings
            {
                BaseUrl = Defaults.BaseUrl,
                Model = Defaults.Model,
                Temperature = 0.3,
                MaxTokens = Defaults.MaxTokens,
                SettingsRevision = Defaults.SettingsRevision,
                TimeoutSeconds = 180,
                KeepVersions = 10,
                FirstRunDone = false,
                AutoStart = false,
                HotkeyEnabled = true,
                WindowWidth = 1180,
                WindowHeight = 780,
                WindowX = int.MinValue,
                WindowY = int.MinValue,
                LastOpenAppId = null
            };
        }

        public Settings Clone()
        {
            return (Settings)MemberwiseClone();
        }

        /// <summary>把前端传来的 patch 合并进当前设置（只接受白名单字段）。</summary>
        public Settings ApplyPatch(IDictionary<string, object> patch)
        {
            if (patch == null) return this;
            var next = Clone();

            var baseUrl = JsonLite.Str(patch, "baseUrl");
            if (baseUrl != null) next.BaseUrl = baseUrl.Trim();
            var model = JsonLite.Str(patch, "model");
            if (model != null) next.Model = model.Trim();
            if (patch.ContainsKey("temperature")) next.Temperature = JsonLite.Double(patch, "temperature", next.Temperature);
            if (patch.ContainsKey("maxTokens")) next.MaxTokens = JsonLite.Int(patch, "maxTokens", next.MaxTokens);
            if (patch.ContainsKey("timeoutSeconds")) next.TimeoutSeconds = JsonLite.Int(patch, "timeoutSeconds", next.TimeoutSeconds);
            if (patch.ContainsKey("keepVersions")) next.KeepVersions = JsonLite.Int(patch, "keepVersions", next.KeepVersions);
            if (patch.ContainsKey("autoStart")) next.AutoStart = JsonLite.Bool(patch, "autoStart", next.AutoStart);
            if (patch.ContainsKey("hotkeyEnabled")) next.HotkeyEnabled = JsonLite.Bool(patch, "hotkeyEnabled", next.HotkeyEnabled);
            if (patch.ContainsKey("lastOpenAppId")) next.LastOpenAppId = JsonLite.Str(patch, "lastOpenAppId");
            return next;
        }

        public IList<string> Validate()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(BaseUrl))
            {
                errors.Add("API 地址不能为空");
            }
            else
            {
                Uri uri;
                if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    errors.Add("API 地址必须是 http(s) 的完整 URL，例如 https://api.example.com/v1");
            }
            if (string.IsNullOrWhiteSpace(Model)) errors.Add("模型名不能为空");
            if (Temperature < 0 || Temperature > 2) errors.Add("温度需在 0 ~ 2 之间");
            if (MaxTokens < 256 || MaxTokens > 128000) errors.Add("最大 tokens 需在 256 ~ 128000 之间");
            if (TimeoutSeconds < 10 || TimeoutSeconds > 600) errors.Add("超时需在 10 ~ 600 秒之间");
            if (KeepVersions < 1 || KeepVersions > 100) errors.Add("版本保留数需在 1 ~ 100 之间");
            return errors;
        }
    }

    public static class Defaults
    {
        /// <summary>默认网关（用户可在设置页修改）。</summary>
        public const string BaseUrl = "https://api.example.com/v1";
        /// <summary>默认模型（该网关支持 function calling，适合生成软件）。</summary>
        public const string Model = "gpt-4o-mini";
        /// <summary>
        /// 单轮输出上限。v1.0.3 起由 8192 提到 16000：8192 会让较大的文件写到一半被截断
        /// （finish_reason=length），模型只能整份重写，既慢又容易把轮次耗光。
        /// </summary>
        public const int MaxTokens = 16000;
        /// <summary>v1.0.2 及以前的默认输出上限，用于老配置一次性升级。</summary>
        public const int LegacyMaxTokens = 8192;
        /// <summary>设置项结构版本：用于"只做一次"的配置迁移（0 = v1.0.2 及以前的设置）。</summary>
        public const int SettingsRevision = 1;
        public const string ProductName = "AppForge 造物台";
        public const int SchemaVersion = 1;
    }

    /// <summary>应用元数据（卡片墙 + 启动所需）。</summary>
    public sealed class AppMeta
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Type { get; set; }
        public string Icon { get; set; }
        public int CurrentVersion { get; set; }
        public string Status { get; set; }
        public bool Pinned { get; set; }
        public int LaunchCount { get; set; }
        public long LastOpenedAt { get; set; }
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public long Bytes { get; set; }

        public IDictionary<string, object> ToJson()
        {
            var d = JsonLite.New();
            d["id"] = Id;
            d["name"] = Name;
            d["description"] = Description ?? string.Empty;
            d["type"] = Type;
            d["icon"] = Icon ?? "🧩";
            d["currentVersion"] = CurrentVersion;
            d["status"] = Status ?? "ready";
            d["pinned"] = Pinned;
            d["launchCount"] = LaunchCount;
            d["lastOpenedAt"] = LastOpenedAt;
            d["createdAt"] = CreatedAt;
            d["updatedAt"] = UpdatedAt;
            d["bytes"] = Bytes;
            return d;
        }
    }

    public sealed class IndexFile
    {
        public int SchemaVersion { get; set; }
        public List<AppMeta> Apps { get; set; }

        public IndexFile()
        {
            SchemaVersion = Defaults.SchemaVersion;
            Apps = new List<AppMeta>();
        }
    }

    /// <summary>依赖项状态（首启自检 / 设置页展示）。</summary>
    public sealed class DepStatus
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string State { get; set; } // ok | missing | unsupported
        public string Message { get; set; }
        public bool Required { get; set; }

        public IDictionary<string, object> ToJson()
        {
            var d = JsonLite.New();
            d["name"] = Name;
            d["displayName"] = DisplayName;
            d["version"] = Version ?? string.Empty;
            d["state"] = State ?? "ok";
            d["message"] = Message ?? string.Empty;
            d["required"] = Required;
            return d;
        }
    }

    public sealed class AppInfo
    {
        public string Product { get; set; }
        public string Version { get; set; }
        public string DataDir { get; set; }
        public string OsBuild { get; set; }
        public string WebView2 { get; set; }
        public string DotNet { get; set; }
        public bool HasApiKey { get; set; }
    }

    public sealed class TestModelResult
    {
        public bool Ok { get; set; }
        public long LatencyMs { get; set; }
        public string Model { get; set; }
        public string Sample { get; set; }
        public string Error { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
    }
}
