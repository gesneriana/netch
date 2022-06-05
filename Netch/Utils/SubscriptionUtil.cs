using System.Diagnostics;
using System.Net;
using Netch.Models;
using Newtonsoft.Json;

namespace Netch.Utils;

public static class SubscriptionUtil
{
    private static readonly object ServerLock = new();

    public static Task UpdateServersAsync(string? proxyServer = default)
    {
        return Task.WhenAll(Global.Settings.Subscription.Select(item => UpdateServerCoreAsync(item, proxyServer)));
    }

    private static async Task UpdateServerCoreAsync(Subscription item, string? proxyServer)
    {
        try
        {
            if (!item.Enable)
                return;

            var request = WebUtil.CreateRequest(item.Link);

            if (!string.IsNullOrEmpty(item.UserAgent))
                request.UserAgent = item.UserAgent;

            if (!string.IsNullOrEmpty(proxyServer))
                request.Proxy = new WebProxy(proxyServer);

            List<Server> servers;

            var (code, result) = await WebUtil.DownloadStringAsync(request);
            if (code == HttpStatusCode.OK)
                servers = ShareLink.ParseText(result);
            else
                throw new Exception($"{item.Remark} Response Status Code: {code}");

            // 使用socks5或者http代理去解析域名为ip
            if (Global.Settings.ForceParsing)
            {
                if (string.IsNullOrWhiteSpace(Global.Settings.UpStreamProxy))
                {
                    Global.MainForm.NotifyTip("Please set the upstream proxy used by the update subscription first.");
                }
                else
                {
                    await ResolveDomainNamesAsync(servers);
                }
            }

            foreach (var server in servers)
                server.Group = item.Remark;

            lock (ServerLock)
            {
                Global.Settings.Server.RemoveAll(server => server.Group.Equals(item.Remark));
                Global.Settings.Server.AddRange(servers);
            }

            Global.MainForm.NotifyTip(i18N.TranslateFormat("Update {1} server(s) from {0}", item.Remark, servers.Count));
        }
        catch (Exception e)
        {
            Global.MainForm.NotifyTip($"{i18N.TranslateFormat("Update servers failed from {0}", item.Remark)}\n{e.Message}", info: false);
            Log.Warning(e, "Update servers failed");
        }
    }

    private static async Task ResolveDomainNamesAsync(List<Server> serverList)
    {
        Dictionary<string, string> domains = new Dictionary<string, string>();
        foreach (var server in serverList)
        {
            domains[server.Hostname] = String.Empty;  // Domain name deduplication
        }

        foreach (var domain in domains.Keys.ToList())
        {
            var urlString = $"https://dns.google/resolve?name={domain}&type=A";

            // 如果是win7, 那么调用我用golang写的程序请求Google DNS解析域名, Win7 会有TLS握手报错的bug
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "./Resources/http-tool.exe", // 这个程序的源码仓库是 https://github.com/gesneriana/http-tool  如果不放心请自行编译, 替换Resources中的即可
                    Arguments = $"\"{Global.Settings.UpStreamProxy}\" \"{urlString}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.WaitForExit();
            var dnsResult = await proc.StandardOutput.ReadToEndAsync();

            if (string.IsNullOrWhiteSpace(dnsResult))
            {
                continue;
            }
            var dnsData = JsonConvert.DeserializeObject<DNSQuery>(dnsResult);
            if (dnsData == null || dnsData.Answer == null)
            {
                continue;
            }
            domains[domain] = dnsData.Answer.FirstOrDefault().data;
        }

        foreach (var server in serverList)
        {
            if (domains.TryGetValue(server.Hostname, out var ip))
            {
                if (string.IsNullOrWhiteSpace(ip))
                {
                    continue;
                }
                server.Hostname = ip;  // Forcibly resolve the domain name to ip, solve DNS pollution
            }
        }
    }
}