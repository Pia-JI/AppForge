using System;
using AppForge.Shared;
using Microsoft.Web.WebView2.Core;

namespace AppForge.Host.Web
{
    /// <summary>
    /// 虚拟主机映射：把 https://&lt;host&gt;/ 映射到本地目录。
    /// 每个应用一个独立 host ⇒ 独立 origin ⇒ localStorage/IndexedDB 天然隔离（FR-4.2）。
    /// WebView2 会拒绝映射目录之外的路径访问，等于自带路径越界防护。
    /// </summary>
    public static class VirtualHosts
    {
        public static void MapShell(CoreWebView2 core, string folder)
        {
            core.SetVirtualHostNameToFolderMapping(
                Hosts.Shell,
                folder,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        public static void MapApp(CoreWebView2 core, string appId, string folder)
        {
            core.SetVirtualHostNameToFolderMapping(
                Hosts.ForApp(appId),
                folder,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        /// <summary>草稿自测/预览：把草稿目录挂到一个独立 host。host 由调用方给出（如 Hosts.ForDraft(runId)）。</summary>
        public static void MapDraft(CoreWebView2 core, string host, string folder)
        {
            core.SetVirtualHostNameToFolderMapping(
                host,
                folder,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }
    }
}
