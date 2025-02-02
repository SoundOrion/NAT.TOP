using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace NATS.TOP.COMMON;


#region 出力文字列生成（CSV用／従来出力用）

public class Utility
{
    /// <summary>
    /// プレーンテキスト版の統計情報表示（元 Golang の generateParagraphPlainText 相当）
    /// ※CSV出力以外の場合に利用できます
    /// </summary>
    public static string GenerateParagraphPlainText(Engine engine, Stats stats)
    {
        Varz varz = stats.Varz;
        double inMsgsRate = stats.Rates?.InMsgsRate ?? 0;
        double outMsgsRate = stats.Rates?.OutMsgsRate ?? 0;
        string inBytesRate = Psize(engine.DisplayRawBytes, (long)(stats.Rates?.InBytesRate ?? 0));
        string outBytesRate = Psize(engine.DisplayRawBytes, (long)(stats.Rates?.OutBytesRate ?? 0));

        var sb = new StringBuilder();
        sb.AppendLine($"NATS server version {varz.Version} (uptime: {varz.Uptime}) {stats.Error}");
        sb.AppendLine($"Server: {varz.Name}");
        sb.AppendLine($"  ID:   {varz.ID}");
        sb.AppendLine($"  Load: CPU:  {varz.CPU:F1}%  Memory: {Psize(false, varz.Mem)}  Slow Consumers: {varz.SlowConsumers}");
        sb.AppendLine($"  In:   Msgs: {Nsize(engine.DisplayRawBytes, varz.InMsgs)}  Bytes: {Psize(engine.DisplayRawBytes, varz.InBytes)}  Msgs/Sec: {inMsgsRate:F1}  Bytes/Sec: {inBytesRate}");
        sb.AppendLine($"  Out:  Msgs: {Nsize(engine.DisplayRawBytes, varz.OutMsgs)}  Bytes: {Psize(engine.DisplayRawBytes, varz.OutBytes)}  Msgs/Sec: {outMsgsRate:F1}  Bytes/Sec: {outBytesRate}");
        sb.AppendLine();
        sb.AppendLine($"Connections Polled: {stats.Connz.NumConns}");
        // ヘッダー行（単純なカラム区切り）
        sb.AppendLine("HOST\tCID\tNAME\tSUBS\tPENDING\tMSGS_TO\tMSGS_FROM\tBYTES_TO\tBYTES_FROM\tLANG\tVERSION\tUPTIME\tLAST_ACTIVITY");
        if (stats.Connz?.Conns != null)
        {
            foreach (var conn in stats.Connz.Conns)
            {
                string hostname = engine.LookupDNS ? DNSLookup(conn.IP) : $"{conn.IP}:{conn.Port}";
                sb.Append($"{hostname}\t{conn.Cid}\t{conn.Name}\t{conn.NumSubs}\t{Nsize(engine.DisplayRawBytes, conn.Pending)}\t");
                if (!engine.ShowRates)
                {
                    sb.Append($"{Nsize(engine.DisplayRawBytes, conn.OutMsgs)}\t{Nsize(engine.DisplayRawBytes, conn.InMsgs)}\t");
                    sb.Append($"{Psize(engine.DisplayRawBytes, conn.OutBytes)}\t{Psize(engine.DisplayRawBytes, conn.InBytes)}\t");
                }
                else
                {
                    Rates rates = stats.Rates;
                    ConnRates cr = (rates != null && rates.Connections.ContainsKey(conn.Cid))
                        ? rates.Connections[conn.Cid] : new ConnRates();
                    sb.Append($"{Nsize(engine.DisplayRawBytes, (long)cr.OutMsgsRate)}\t{Nsize(engine.DisplayRawBytes, (long)cr.InMsgsRate)}\t");
                    sb.Append($"{Psize(engine.DisplayRawBytes, (long)cr.OutBytesRate)}\t{Psize(engine.DisplayRawBytes, (long)cr.InBytesRate)}\t");
                }
                sb.AppendLine($"{conn.Lang}\t{conn.Version}\t{conn.Uptime}\t{FormatDateTime(conn.LastActivity)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// CSV 出力版（出力区切り文字を指定する場合）
    /// </summary>
    public static string GenerateParagraphCSV(Engine engine, Stats stats, string delimiter)
    {
        Varz varz = stats.Varz;
        var sb = new StringBuilder();
        sb.AppendLine($"NATS server version{delimiter}{varz.Version}{delimiter}(uptime: {varz.Uptime}){delimiter}{stats.Error}");
        sb.AppendLine("Server:");
        sb.AppendLine($"Load{delimiter}CPU{delimiter}{varz.CPU:F1}%{delimiter}Memory{delimiter}{Psize(false, varz.Mem)}{delimiter}Slow Consumers{delimiter}{varz.SlowConsumers}");
        sb.AppendLine($"In{delimiter}Msgs{delimiter}{Nsize(engine.DisplayRawBytes, varz.InMsgs)}{delimiter}Bytes{delimiter}{Psize(engine.DisplayRawBytes, varz.InBytes)}{delimiter}Msgs/Sec{delimiter}{stats.Rates?.InMsgsRate:F1}{delimiter}Bytes/Sec{delimiter}{Psize(engine.DisplayRawBytes, (long)stats.Rates?.InBytesRate)}");
        sb.AppendLine($"Out{delimiter}Msgs{delimiter}{Nsize(engine.DisplayRawBytes, varz.OutMsgs)}{delimiter}Bytes{delimiter}{Psize(engine.DisplayRawBytes, varz.OutBytes)}{delimiter}Msgs/Sec{delimiter}{stats.Rates?.OutMsgsRate:F1}{delimiter}Bytes/Sec{delimiter}{Psize(engine.DisplayRawBytes, (long)stats.Rates?.OutBytesRate)}");
        sb.AppendLine();
        sb.AppendLine($"Connections Polled{delimiter}{stats.Connz.NumConns}");

        List<string> headers = new()
        {
            "HOST", "CID", "NAME", "SUBS", "PENDING",
            "MSGS_TO", "MSGS_FROM", "BYTES_TO", "BYTES_FROM", "LANG", "VERSION", "UPTIME", "LAST_ACTIVITY"
        };
        if (engine.DisplaySubs)
        {
            headers.Add("SUBSCRIPTIONS");
        }
        sb.AppendLine(string.Join(delimiter, headers));

        foreach (var conn in stats.Connz.Conns)
        {
            string hostname = engine.LookupDNS ? DNSLookup(conn.IP) : $"{conn.IP}:{conn.Port}";
            List<string> row = new()
            {
                hostname,
                conn.Cid.ToString(),
                conn.Name ?? "",
                conn.NumSubs.ToString(),
                Nsize(engine.DisplayRawBytes, conn.Pending)
            };

            if (!engine.ShowRates)
            {
                row.Add(Nsize(engine.DisplayRawBytes, conn.OutMsgs));
                row.Add(Nsize(engine.DisplayRawBytes, conn.InMsgs));
                row.Add(Psize(engine.DisplayRawBytes, conn.OutBytes));
                row.Add(Psize(engine.DisplayRawBytes, conn.InBytes));
            }
            else
            {
                Rates rates = stats.Rates;
                ConnRates cr = (rates != null && rates.Connections.ContainsKey(conn.Cid))
                    ? rates.Connections[conn.Cid] : new ConnRates();
                row.Add(Nsize(engine.DisplayRawBytes, (long)cr.OutMsgsRate));
                row.Add(Nsize(engine.DisplayRawBytes, (long)cr.InMsgsRate));
                row.Add(Psize(engine.DisplayRawBytes, (long)cr.OutBytesRate));
                row.Add(Psize(engine.DisplayRawBytes, (long)cr.InBytesRate));
            }
            row.Add(conn.Lang);
            row.Add(conn.Version);
            row.Add(conn.Uptime);
            row.Add(conn.LastActivity);
            if (engine.DisplaySubs)
            {
                string subs = conn.Subs != null ? string.Join(", ", conn.Subs) : "";
                row.Add(subs);
            }
            sb.AppendLine(string.Join(delimiter, row));
        }
        return sb.ToString();
    }

    /// <summary>
    /// DNS ルックアップ（結果はキャッシュする）
    /// </summary>
    public static Dictionary<string, string> dnsCache = new();

    public static string DNSLookup(string ip)
    {
        if (dnsCache.TryGetValue(ip, out string? value))
            return value;
        try
        {
            var entry = Dns.GetHostEntry(ip);
            string hostname = entry.HostName;
            dnsCache[ip] = hostname;
            return hostname;
        }
        catch
        {
            dnsCache[ip] = ip;
            return ip;
        }
    }

    /// <summary>
    /// バイトサイズを人間に読みやすい文字列に変換
    /// </summary>
    public static string Psize(bool displayRawValue, long s)
    {
        double size = s;
        const double kibibyte = 1024;
        const double mebibyte = 1024 * 1024;
        const double gibibyte = 1024 * 1024 * 1024;

        if (displayRawValue || size < kibibyte)
            return $"{size:0}";
        if (size < mebibyte)
            return $"{size / kibibyte:0.0}K";
        if (size < gibibyte)
            return $"{size / mebibyte:0.0}M";
        return $"{size / gibibyte:0.0}G";
    }

    /// <summary>
    /// 数値を読みやすい形式に変換（K, M, B, T 単位）
    /// </summary>
    public static string Nsize(bool displayRawValue, long s)
    {
        double size = s;
        const double k = 1000;
        const double m = k * 1000;
        const double b = m * 1000;
        const double t = b * 1000;

        if (displayRawValue || size < k)
            return $"{size:0}";
        if (size < m)
            return $"{size / k:0.0}K";
        if (size < b)
            return $"{size / m:0.0}M";
        if (size < t)
            return $"{size / b:0.0}B";
        return $"{size / t:0.0}T";
    }

    /// <summary>
    /// 日付フォーマットを変換
    /// </summary>
    public static string FormatDateTime(string isoDateTime)
    {
        try
        {
            DateTimeOffset dto = DateTimeOffset.Parse(isoDateTime);
            DateTime dt = dto.DateTime;
            return dt.ToString("yyyy/MM/dd HH:mm");
        }
        catch
        {
            return isoDateTime;
        }
    }
}

#endregion
