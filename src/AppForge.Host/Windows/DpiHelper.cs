using System;
using System.Runtime.InteropServices;

namespace AppForge.Host.Windows
{
    /// <summary>
    /// DPI 助手：WinForms 的尺寸是物理像素，而 WebView2 按窗口 DPI 缩放渲染。
    /// 为了让"布局按 CSS 逻辑像素设计"与"窗口实际尺寸"一致，这里统一做逻辑→物理换算。
    /// </summary>
    internal static class DpiHelper
    {
        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        private static float? _scale;

        /// <summary>系统 DPI 缩放比例（96 → 1.0，192 → 2.0）。</summary>
        public static float Scale
        {
            get
            {
                if (_scale.HasValue) return _scale.Value;
                var scale = 1f;
                try
                {
                    var dpi = GetDpiForSystem();
                    if (dpi >= 96) scale = dpi / 96f;
                }
                catch
                {
                    // Vista/7 或受限环境：按 100% 处理
                }
                _scale = scale;
                return scale;
            }
        }

        /// <summary>逻辑像素 → 物理像素。</summary>
        public static int Scaled(int logical)
        {
            var value = (int)Math.Round(logical * Scale);
            return value < 1 ? 1 : value;
        }

        /// <summary>物理像素 → 逻辑像素。</summary>
        public static int Unscaled(int physical)
        {
            var value = (int)Math.Round(physical / Scale);
            return value < 1 ? 1 : value;
        }
    }
}
