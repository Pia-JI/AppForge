using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AppForge.Shared
{
    /// <summary>
    /// JSON 读写封装：只用 .NET Framework 自带的 JavaScriptSerializer，零第三方依赖。
    /// 对外统一使用 camelCase 字典，避免 C# 命名与前端约定互相污染。
    /// </summary>
    public static class JsonLite
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 200
        };

        public static string Serialize(object value)
        {
            return value == null ? "null" : Serializer.Serialize(value);
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return default(T);
            return Serializer.Deserialize<T>(json);
        }

        public static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return Serializer.DeserializeObject(json);
        }

        public static IDictionary<string, object> ParseObject(string json)
        {
            var obj = Parse(json) as IDictionary<string, object>;
            return obj ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        // ---------- 取值助手（宽容类型转换） ----------

        public static string Str(IDictionary<string, object> d, string key, string fallback = null)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            var s = v as string;
            return s ?? Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(IDictionary<string, object> d, string key, bool fallback = false)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            var s = Convert.ToString(v, CultureInfo.InvariantCulture);
            bool b;
            return bool.TryParse(s, out b) ? b : fallback;
        }

        public static long Long(IDictionary<string, object> d, string key, long fallback = 0)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static int Int(IDictionary<string, object> d, string key, int fallback = 0)
        {
            var l = Long(d, key, fallback);
            if (l > int.MaxValue) return int.MaxValue;
            if (l < int.MinValue) return int.MinValue;
            return (int)l;
        }

        public static double Double(IDictionary<string, object> d, string key, double fallback = 0)
        {
            if (d == null) return fallback;
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static IDictionary<string, object> Obj(IDictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v)) return null;
            return v as IDictionary<string, object>;
        }

        public static IList List(IDictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(key, out v)) return null;
            return v as IList;
        }

        public static IDictionary<string, object> New()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }
    }
}
