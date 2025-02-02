using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NATS.TOP.COMMON;

/// <summary>
/// NATS サーバーから統計情報を取得・計算するエンジンクラス
/// </summary>
public class Engine
{
    public string Host { get; set; }
    public int Port { get; set; }
    public int Conns { get; set; }
    public int Delay { get; set; }
    public string Uri { get; set; }
    public string SortOpt { get; set; }
    public bool DisplaySubs { get; set; } = false;
    public bool ShowRates { get; set; } = false;
    public bool DisplayRawBytes { get; set; } = false;
    public bool LookupDNS { get; set; } = false;

    public Stats LastStats { get; set; }
    public Dictionary<ulong, ConnInfo> LastConnz { get; set; } = new();
    public HttpClient HttpClient { get; set; }

    // JSON シリアライザオプションをキャッシュ（任意）
    private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    public Engine(string host, int port, int conns, int delay)
    {
        Host = host;
        Port = port;
        Conns = conns;
        Delay = delay;
    }

    public void SetupHTTP()
    {
        HttpClient = new HttpClient();
        Uri = $"http://{Host}:{Port}";
    }

    public void SetupHTTPS(string caCertOpt, string certOpt, string keyOpt, bool skipVerify)
    {
        HttpClientHandler handler = new();
        if (skipVerify)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        if (!string.IsNullOrEmpty(caCertOpt))
        {
            var caCert = new X509Certificate2(File.ReadAllBytes(caCertOpt));
            handler.ClientCertificates.Add(caCert);
        }
        if (!string.IsNullOrEmpty(certOpt) && !string.IsNullOrEmpty(keyOpt))
        {
            var cert = new X509Certificate2(certOpt);
            handler.ClientCertificates.Add(cert);
        }
        HttpClient = new HttpClient(handler);
        Uri = $"https://{Host}:{Port}";
    }

    // 指定パス（/varz, /connz）に GET リクエストし JSON を逆シリアライズする
    public async Task<object> Request(string path)
    {
        string url = Uri + path;
        if (path.StartsWith("/connz"))
        {
            url += $"?limit={Conns}&sort={SortOpt}";
            if (DisplaySubs)
            {
                url += "&subs=1";
            }
        }
        HttpResponseMessage response = await HttpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        if (path == "/varz")
        {
            Varz varz = JsonSerializer.Deserialize<Varz>(body, jsonOptions);
            return varz;
        }
        else if (path.StartsWith("/connz"))
        {
            Connz connz = JsonSerializer.Deserialize<Connz>(body, jsonOptions);
            return connz;
        }
        else
        {
            throw new Exception($"invalid path '{path}'");
        }
    }

    public async Task<Varz> RequestVarz() => (Varz)await Request("/varz");

    // 統計情報を取得し、レート計算などを行う
    public async Task<Stats> FetchStats()
    {
        Stats stats = new();
        try
        {
            // /varz と /connz の取得を並列実行
            Task<object> varzTask = Request("/varz");
            Task<object> connzTask = Request("/connz");
            await Task.WhenAll(varzTask, connzTask);
            stats.Varz = (Varz)varzTask.Result;
            stats.Connz = (Connz)connzTask.Result;
        }
        catch (Exception ex)
        {
            stats.Error = ex.Message;
            return stats;
        }

        // 前回統計がある場合、経過時間から各種レートを計算
        if (LastStats != null)
        {
            TimeSpan tdelta = stats.Varz.Now - LastStats.Varz.Now;
            if (tdelta.TotalSeconds > 0)
            {
                long inMsgsDelta = stats.Varz.InMsgs - LastStats.Varz.InMsgs;
                long outMsgsDelta = stats.Varz.OutMsgs - LastStats.Varz.OutMsgs;
                long inBytesDelta = stats.Varz.InBytes - LastStats.Varz.InBytes;
                long outBytesDelta = stats.Varz.OutBytes - LastStats.Varz.OutBytes;

                Rates rates = new()
                {
                    InMsgsRate = inMsgsDelta / tdelta.TotalSeconds,
                    OutMsgsRate = outMsgsDelta / tdelta.TotalSeconds,
                    InBytesRate = inBytesDelta / tdelta.TotalSeconds,
                    OutBytesRate = outBytesDelta / tdelta.TotalSeconds,
                    Connections = new Dictionary<ulong, ConnRates>()
                };

                // 各接続のレート計算
                if (stats.Connz?.Conns != null)
                {
                    foreach (var conn in stats.Connz.Conns)
                    {
                        ConnRates cr = new();
                        if (LastConnz.ContainsKey(conn.Cid))
                        {
                            ConnInfo lastConn = LastConnz[conn.Cid];
                            cr.InMsgsRate = conn.InMsgs - lastConn.InMsgs;
                            cr.OutMsgsRate = conn.OutMsgs - lastConn.OutMsgs;
                            cr.InBytesRate = conn.InBytes - lastConn.InBytes;
                            cr.OutBytesRate = conn.OutBytes - lastConn.OutBytes;
                        }
                        rates.Connections[conn.Cid] = cr;
                    }
                }
                stats.Rates = rates;
            }
        }

        // 最新の統計情報をキャッシュ
        LastStats = stats;
        LastConnz.Clear();
        if (stats.Connz?.Conns != null)
        {
            foreach (var conn in stats.Connz.Conns)
            {
                LastConnz[conn.Cid] = conn;
            }
        }
        return stats;
    }

    // 一定間隔で統計情報を取得し続ける
    public async Task MonitorStats(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await FetchStats();
            await Task.Delay(Delay * 1000, token);
        }
    }
}
