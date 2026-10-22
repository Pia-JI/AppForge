using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AppForge.Shared
{
    /// <summary>设置存储：settings.json（原子写）。</summary>
    public sealed class SettingsStore
    {
        private readonly Workspace _ws;
        private Settings _current;

        public SettingsStore(Workspace workspace)
        {
            _ws = workspace;
            _current = Settings.CreateDefault();
        }

        public Settings Current { get { return _current; } }

        public void Load()
        {
            var json = JsonStore.ReadOrNull(_ws.SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                _current = Settings.CreateDefault();
                Save(_current);
                return;
            }

            try
            {
                var loaded = JsonLite.Deserialize<Settings>(json);
                _current = loaded ?? Settings.CreateDefault();
                // 兼容旧版本/缺字段
                var def = Settings.CreateDefault();
                if (string.IsNullOrWhiteSpace(_current.BaseUrl)) _current.BaseUrl = def.BaseUrl;
                if (string.IsNullOrWhiteSpace(_current.Model)) _current.Model = def.Model;
                if (_current.MaxTokens <= 0) _current.MaxTokens = def.MaxTokens;
                if (_current.TimeoutSeconds <= 0) _current.TimeoutSeconds = def.TimeoutSeconds;
                if (_current.KeepVersions <= 0) _current.KeepVersions = def.KeepVersions;
                if (_current.WindowWidth <= 0) _current.WindowWidth = def.WindowWidth;
                if (_current.WindowHeight <= 0) _current.WindowHeight = def.WindowHeight;
            }
            catch
            {
                _current = Settings.CreateDefault();
                Save(_current);
            }
        }

        public void Save(Settings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            _current = settings;
            JsonStore.WriteAtomic(_ws.SettingsPath, JsonLite.Serialize(settings));
        }
    }

    /// <summary>密钥存储：Windows DPAPI（CurrentUser）加密，磁盘上永不明文。</summary>
    public sealed class SecretsStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AppForge.Secrets.v1");
        private readonly string _path;

        public SecretsStore(Workspace workspace)
        {
            _path = workspace.SecretsPath;
        }

        public bool HasApiKey()
        {
            return !string.IsNullOrEmpty(GetApiKey());
        }

        public string GetApiKey()
        {
            try
            {
                if (!File.Exists(_path)) return string.Empty;
                var b64 = File.ReadAllText(_path, Encoding.UTF8).Trim();
                if (b64.Length == 0) return string.Empty;
                var cipher = Convert.FromBase64String(b64);
                var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return string.Empty;
            }
        }

        public void SetApiKey(string apiKey)
        {
            var key = (apiKey ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                JsonStore.TryDelete(_path);
                return;
            }

            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser);
            JsonStore.WriteAtomic(_path, Convert.ToBase64String(cipher));
        }

        /// <summary>脱敏展示：sk-…abcd</summary>
        public string GetMaskedKey()
        {
            return Mask(GetApiKey());
        }

        public static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            if (key.Length <= 8) return "••••";
            return key.Substring(0, 3) + "••••" + key.Substring(key.Length - 4);
        }
    }
}
