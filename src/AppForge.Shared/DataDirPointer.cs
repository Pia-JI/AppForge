using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace AppForge.Shared
{
    /// <summary>
    /// 数据目录"指针"：把"这台机器上 AppForge 的数据到底放在哪"记在注册表（HKCU）里。
    ///
    /// 为什么需要它：
    ///   数据目录的解析规则是"能写就行"，而回退链里包含"exe 同目录"。
    ///   于是同一个程序的不同入口会算出**不同**的目录：
    ///     · 启动AppForge.cmd → src\AppForge.Host\bin\Release\AppForge.exe → bin\Release\data
    ///     · dist\AppForge.exe（单文件）→ AppForge-payload\&lt;指纹&gt;\data
    ///   用户看到的就是"两个入口的数据不一样"。
    ///
    /// 用 HKCU 而不是文件来记：注册表几乎不会被"文件保护/受控文件夹访问"拦，
    /// 而且与 exe 的位置无关——不管从哪个入口启动，都读同一个值。
    ///
    /// 顺带记一份 KnownDataDirs（曾经用过的所有数据目录），
    /// 首次统一时可以把散落各处的应用合并过来，不至于"换了个目录，应用全没了"。
    /// </summary>
    public static class DataDirPointer
    {
        private const string KeyPath = @"Software\AppForge";
        private const string DataDirValue = "DataDir";
        private const string KnownValue = "KnownDataDirs";
        private const int MaxKnown = 12;

        private static RegistryKey OpenKey()
        {
            try { return Registry.CurrentUser.CreateSubKey(KeyPath); }
            catch { return null; }
        }

        /// <summary>读取"约定的数据目录"。没有记录/读取失败返回 null。</summary>
        public static string Read()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key == null) return null;
                    var value = key.GetValue(DataDirValue) as string;
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写入"约定的数据目录"（值没变化就不写，避免无谓的注册表写入）。</summary>
        public static bool Write(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null) return false;
                    var current = key.GetValue(DataDirValue) as string;
                    if (string.Equals(current, dir, StringComparison.OrdinalIgnoreCase)) return false;
                    key.SetValue(DataDirValue, dir.Trim(), RegistryValueKind.String);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读取"曾经用过的数据目录"列表（用于合并旧数据）。</summary>
        public static List<string> ReadKnown()
        {
            var result = new List<string>();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key == null) return result;
                    var raw = key.GetValue(KnownValue) as string[];
                    if (raw == null) return result;
                    foreach (var item in raw)
                    {
                        if (string.IsNullOrWhiteSpace(item)) continue;
                        var value = item.Trim();
                        if (!result.Contains(value)) result.Add(value);
                    }
                }
            }
            catch
            {
                // 忽略
            }
            return result;
        }

        /// <summary>把某个数据目录登记进"曾经用过"列表（最新的排最前，超出上限的丢弃）。</summary>
        public static void Remember(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                var known = ReadKnown();
                known.RemoveAll(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
                known.Insert(0, dir.Trim());
                while (known.Count > MaxKnown) known.RemoveAt(known.Count - 1);

                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key == null) return;
                    key.SetValue(KnownValue, known.ToArray(), RegistryValueKind.MultiString);
                }
            }
            catch
            {
                // 忽略
            }
        }
    }
}
