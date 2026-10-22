using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AppForge.Shared
{
    public sealed class FileDiff
    {
        public string Path { get; set; }
        public string Status { get; set; }   // added | removed | modified
        public int Added { get; set; }
        public int Removed { get; set; }
        public string Preview { get; set; }
        public string Note { get; set; }

        public IDictionary<string, object> ToJson()
        {
            var d = JsonLite.New();
            d["path"] = Path;
            d["status"] = Status;
            d["added"] = Added;
            d["removed"] = Removed;
            d["preview"] = Preview ?? string.Empty;
            d["note"] = Note ?? string.Empty;
            return d;
        }
    }

    /// <summary>版本/文件对比：逐文件给出 新增/删除/修改 与行级增删统计（用于版本 diff 视图）。</summary>
    public static class VersionDiff
    {
        private const long MaxLcsCells = 2000000;

        public static IList<FileDiff> CompareDirs(string fromDir, string toDir, int maxPreviewLines)
        {
            var from = ReadLinesFromDir(fromDir);
            var to = ReadLinesFromDir(toDir);
            var paths = new HashSet<string>(from.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var key in to.Keys) paths.Add(key);

            var result = new List<FileDiff>();
            foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string[] oldLines, newLines;
                var hasOld = from.TryGetValue(path, out oldLines);
                var hasNew = to.TryGetValue(path, out newLines);

                if (!hasOld && hasNew)
                {
                    result.Add(new FileDiff
                    {
                        Path = path,
                        Status = "added",
                        Added = newLines.Length,
                        Preview = BuildPreview(new string[0], newLines, maxPreviewLines)
                    });
                }
                else if (hasOld && !hasNew)
                {
                    result.Add(new FileDiff
                    {
                        Path = path,
                        Status = "removed",
                        Removed = oldLines.Length,
                        Preview = BuildPreview(oldLines, new string[0], maxPreviewLines)
                    });
                }
                else
                {
                    var preview = new List<string>();
                    int added, removed;
                    var note = DiffLines(oldLines, newLines, preview, maxPreviewLines, out added, out removed);
                    if (added == 0 && removed == 0 && string.IsNullOrEmpty(note)) continue; // 无变化
                    result.Add(new FileDiff
                    {
                        Path = path,
                        Status = "modified",
                        Added = added,
                        Removed = removed,
                        Preview = string.Join("\n", preview),
                        Note = note
                    });
                }
            }
            return result;
        }

        public static IDictionary<string, object> Compare(
            Workspace ws, string appId, int fromSeq, int toSeq, int maxPreviewLines)
        {
            var fromDir = ws.VersionDir(appId, fromSeq);
            var toDir = ws.VersionDir(appId, toSeq);
            if (!Directory.Exists(fromDir) || !Directory.Exists(toDir))
                throw new KeyNotFoundException("版本不存在（from=" + fromSeq + " to=" + toSeq + "）");

            var diffs = CompareDirs(fromDir, toDir, maxPreviewLines);

            var files = new List<object>();
            var addedTotal = 0;
            var removedTotal = 0;
            foreach (var diff in diffs)
            {
                files.Add(diff.ToJson());
                addedTotal += diff.Added;
                removedTotal += diff.Removed;
            }

            var d = JsonLite.New();
            d["appId"] = appId;
            d["from"] = fromSeq;
            d["to"] = toSeq;
            d["files"] = files;
            d["fileCount"] = files.Count;
            d["addedLines"] = addedTotal;
            d["removedLines"] = removedTotal;
            return d;
        }

        private static IDictionary<string, string[]> ReadLinesFromDir(string dir)
        {
            var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(dir)) return map;

            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = file.Substring(dir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/');
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > 512 * 1024) continue;   // 超大文件跳过
                    map[rel] = File.ReadAllLines(file);
                }
                catch
                {
                    // 跳过无法读取的文件
                }
            }
            return map;
        }

        private static string BuildPreview(string[] oldLines, string[] newLines, int maxPreviewLines)
        {
            var preview = new List<string>();
            foreach (var line in oldLines)
            {
                if (preview.Count >= maxPreviewLines) break;
                preview.Add("- " + line);
            }
            foreach (var line in newLines)
            {
                if (preview.Count >= maxPreviewLines) break;
                preview.Add("+ " + line);
            }
            return string.Join("\n", preview);
        }

        /// <summary>基于 LCS 的行级 diff；返回提示（文件过大时降级为行数估计）。</summary>
        private static string DiffLines(
            string[] oldLines,
            string[] newLines,
            List<string> preview,
            int maxPreviewLines,
            out int added,
            out int removed)
        {
            added = 0;
            removed = 0;

            var rows = oldLines.Length + 1;
            var cols = newLines.Length + 1;
            if ((long)rows * cols > MaxLcsCells)
            {
                added = Math.Max(0, newLines.Length - oldLines.Length);
                removed = Math.Max(0, oldLines.Length - newLines.Length);
                if (preview.Count < maxPreviewLines) preview.Add("（文件较大，仅给出整体行数变化）");
                return "large-file";
            }

            var dp = new int[rows, cols];
            for (var i = 1; i < rows; i++)
            {
                for (var j = 1; j < cols; j++)
                {
                    dp[i, j] = string.Equals(oldLines[i - 1], newLines[j - 1], StringComparison.Ordinal)
                        ? dp[i - 1, j - 1] + 1
                        : Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }

            var ops = new List<string>();
            var x = oldLines.Length;
            var y = newLines.Length;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && string.Equals(oldLines[x - 1], newLines[y - 1], StringComparison.Ordinal))
                {
                    x--;
                    y--;
                    continue;
                }
                if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
                {
                    ops.Add("+ " + newLines[y - 1]);
                    added++;
                    y--;
                }
                else if (x > 0)
                {
                    ops.Add("- " + oldLines[x - 1]);
                    removed++;
                    x--;
                }
            }

            ops.Reverse();
            foreach (var op in ops)
            {
                if (preview.Count >= maxPreviewLines) break;
                preview.Add(op);
            }
            return null;
        }
    }
}
