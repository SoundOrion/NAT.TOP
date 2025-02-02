using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace NatsTopCSharp
{
    class Program
    {
        // エントリポイント
        static async Task Main(string[] args)
        {
            // コマンドライン引数をパース
            Options opts = Options.Parse(args);
            if (opts.ShowVersion)
            {
                Console.WriteLine($"nats-top v{opts.Version}");
                return;
            }

            // Engine インスタンス生成
            Engine engine = new(opts.Host, opts.Port, opts.Conns, opts.Delay);
            if (opts.HttpsPort != 0)
            {
                engine.Port = opts.HttpsPort;
                engine.SetupHTTPS(opts.CACert, opts.Cert, opts.Key, opts.SkipVerify);
            }
            else
            {
                engine.SetupHTTP();
            }

            // 初期接続確認（/varz の問い合わせ）
            try
            {
                Varz varz = await engine.RequestVarz();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"nats-top: /varz smoke test failed: {ex.Message}");
                return;
            }

            engine.SortOpt = opts.SortBy;
            engine.DisplaySubs = opts.DisplaySubscriptionsColumn;
            engine.DisplayRawBytes = opts.DisplayRawBytes;
            engine.LookupDNS = opts.LookupDNS;

            // 出力先ファイル指定時は初回統計情報を出力して終了
            if (!string.IsNullOrEmpty(opts.OutputFile))
            {
                Stats stats = await engine.FetchStats();
                string text = !string.IsNullOrEmpty(opts.OutputDelimiter)
                    ? GenerateParagraphCSV(engine, stats, opts.OutputDelimiter)
                    : GenerateParagraphPlainText(engine, stats);
                if (opts.OutputFile == "-")
                {
                    Console.WriteLine(text);
                }
                else
                {
                    File.WriteAllText(opts.OutputFile, text);
                }
                return;
            }

            // 統計情報の監視開始（バックグラウンドタスク）
            CancellationTokenSource cts = new();
            Task monitoringTask = engine.MonitorStats(cts.Token);

            // メインループ：一定間隔で最新統計情報を表示＆キー入力処理
            int refreshCount = 0;
            while (!cts.IsCancellationRequested)
            {
                if (engine.LastStats != null)
                {
                    Console.Clear();
                    string text = GenerateParagraphPlainText(engine, engine.LastStats);
                    Console.WriteLine(text);
                    refreshCount++;
                    if (opts.MaxStatsRefreshes > 0 && refreshCount >= opts.MaxStatsRefreshes)
                    {
                        break;
                    }
                }

                // キー入力のチェック（q または Ctrl+C で終了、space でレート表示の切替、s でサブスクリプション列表示の切替、d で DNS ルックアップ切替、b でバイト表記切替）
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Q || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                    {
                        break;
                    }
                    else if (key.Key == ConsoleKey.Spacebar)
                    {
                        engine.ShowRates = !engine.ShowRates;
                    }
                    else if (key.Key == ConsoleKey.S)
                    {
                        engine.DisplaySubs = !engine.DisplaySubs;
                    }
                    else if (key.Key == ConsoleKey.D)
                    {
                        engine.LookupDNS = !engine.LookupDNS;
                    }
                    else if (key.Key == ConsoleKey.B)
                    {
                        engine.DisplayRawBytes = !engine.DisplayRawBytes;
                    }
                }

                await Task.Delay(500);
            }

            cts.Cancel();
            await monitoringTask;
        }

        #region 出力文字列生成

        // プレーンテキスト版の統計情報表示（元 Golang の generateParagraphPlainText 相当）
        static string GenerateParagraphPlainText(Engine engine, Stats stats)
        {
            Varz varz = stats.Varz;
            // レートは前回との差分から計算済みの値
            double inMsgsRate = stats.Rates?.InMsgsRate ?? 0;
            double outMsgsRate = stats.Rates?.OutMsgsRate ?? 0;
            string inBytesRate = Psize(engine.DisplayRawBytes, (long)(stats.Rates?.InBytesRate ?? 0));
            string outBytesRate = Psize(engine.DisplayRawBytes, (long)(stats.Rates?.OutBytesRate ?? 0));

            string info =
                $"NATS server version {varz.Version} (uptime: {varz.Uptime}) {stats.Error}{Environment.NewLine}" +
                $"Server: {varz.Name}{Environment.NewLine}" +
                $"  ID:   {varz.ID}{Environment.NewLine}" +
                $"  Load: CPU:  {varz.CPU:F1}%  Memory: {Psize(false, varz.Mem)}  Slow Consumers: {varz.SlowConsumers}{Environment.NewLine}" +
                $"  In:   Msgs: {Nsize(engine.DisplayRawBytes, varz.InMsgs)}  Bytes: {Psize(engine.DisplayRawBytes, varz.InBytes)}  Msgs/Sec: {inMsgsRate:F1}  Bytes/Sec: {inBytesRate}{Environment.NewLine}" +
                $"  Out:  Msgs: {Nsize(engine.DisplayRawBytes, varz.OutMsgs)}  Bytes: {Psize(engine.DisplayRawBytes, varz.OutBytes)}  Msgs/Sec: {outMsgsRate:F1}  Bytes/Sec: {outBytesRate}{Environment.NewLine}{Environment.NewLine}" +
                $"Connections Polled: {stats.Connz.NumConns}{Environment.NewLine}";

            // 各列の幅を決定
            int hostSize = 15;
            int nameSize = 0;
            if (stats.Connz?.Conns != null)
            {
                foreach (var conn in stats.Connz.Conns)
                {
                    string hostname = engine.LookupDNS ? DNSLookup(conn.IP) : $"{conn.IP}:{conn.Port}";
                    hostSize = Math.Max(hostSize, hostname.Length + 2);
                    if (!string.IsNullOrEmpty(conn.Name))
                    {
                        nameSize = Math.Max(nameSize, conn.Name.Length + 2);
                    }
                }
            }

            // ヘッダー行
            string header = $"{"HOST".PadRight(hostSize)} {"CID".PadRight(6)}";
            if (nameSize > 0)
            {
                header += " " + "NAME".PadRight(nameSize);
            }
            header += "  SUBS  PENDING  MSGS_TO  MSGS_FROM  BYTES_TO  BYTES_FROM  LANG    VERSION  UPTIME         LAST_ACTIVITY";
            if (engine.DisplaySubs)
            {
                header += "  SUBSCRIPTIONS";
            }
            header += $"{Environment.NewLine}";
            info += header;

            if (stats.Connz?.Conns != null)
            {
                foreach (var conn in stats.Connz.Conns)
                {
                    string hostname = engine.LookupDNS ? DNSLookup(conn.IP) : $"{conn.IP}:{conn.Port}";
                    string row = $"{hostname.PadRight(hostSize)} {conn.Cid.ToString().PadRight(6)}";
                    if (nameSize > 0)
                    {
                        row += " " + (conn.Name ?? "").PadRight(nameSize);
                    }
                    if (!engine.ShowRates)
                    {
                        row += $"  {conn.NumSubs.ToString().PadRight(5)}  {Nsize(engine.DisplayRawBytes, conn.Pending).PadRight(7)}" +
                               $"  {Nsize(engine.DisplayRawBytes, conn.OutMsgs).PadRight(8)}  {Nsize(engine.DisplayRawBytes, conn.InMsgs).PadRight(9)}" +
                               $"  {Psize(engine.DisplayRawBytes, conn.OutBytes).PadRight(9)}  {Psize(engine.DisplayRawBytes, conn.InBytes).PadRight(10)}";
                    }
                    else
                    {
                        // レート表示モード（接続ごとに計算済みのレートを表示）
                        Rates rates = stats.Rates;
                        ConnRates cr = (rates != null && rates.Connections.ContainsKey(conn.Cid))
                            ? rates.Connections[conn.Cid] : new ConnRates();
                        string outMsgs = Nsize(engine.DisplayRawBytes, (long)cr.OutMsgsRate);
                        string inMsgs = Nsize(engine.DisplayRawBytes, (long)cr.InMsgsRate);
                        string outBytes = Psize(engine.DisplayRawBytes, (long)cr.OutBytesRate);
                        string inBytes = Psize(engine.DisplayRawBytes, (long)cr.InBytesRate);
                        row += $"  {conn.NumSubs.ToString().PadRight(5)}  {Nsize(engine.DisplayRawBytes, conn.Pending).PadRight(7)}" +
                               $"  {outMsgs.PadRight(8)}  {inMsgs.PadRight(9)}" +
                               $"  {outBytes.PadRight(9)}  {inBytes.PadRight(10)}";
                    }
                    row += $"  {conn.Lang.PadRight(6)}  {conn.Version.PadRight(7)}  {conn.Uptime.PadRight(14)}  {FormatDateTime(conn.LastActivity).PadRight(14)}";
                    if (engine.DisplaySubs)
                    {
                        string subs = conn.Subs != null ? string.Join(", ", conn.Subs) : "";
                        row += "  " + subs;
                    }
                    row += $"{Environment.NewLine}";
                    info += row;
                }
            }
            return info;
        }

        // CSV 出力版（出力区切り文字を指定する場合）
        static string GenerateParagraphCSV(Engine engine, Stats stats, string delimiter)
        {
            Varz varz = stats.Varz;
            string info =
                $"NATS server version{delimiter}{varz.Version}{delimiter}(uptime: {varz.Uptime}){delimiter}{stats.Error}{Environment.NewLine}" +
                $"Server:{Environment.NewLine}" +
                $"Load{delimiter}CPU{delimiter}{varz.CPU:F1}%{delimiter}Memory{delimiter}{Psize(false, varz.Mem)}{delimiter}Slow Consumers{delimiter}{varz.SlowConsumers}{Environment.NewLine}" +
                $"In{delimiter}Msgs{delimiter}{Nsize(engine.DisplayRawBytes, varz.InMsgs)}{delimiter}Bytes{delimiter}{Psize(engine.DisplayRawBytes, varz.InBytes)}{delimiter}Msgs/Sec{delimiter}{stats.Rates?.InMsgsRate:F1}{delimiter}Bytes/Sec{delimiter}{Psize(engine.DisplayRawBytes, (long)stats.Rates?.InBytesRate)}{Environment.NewLine}" +
                $"Out{delimiter}Msgs{delimiter}{Nsize(engine.DisplayRawBytes, varz.OutMsgs)}{delimiter}Bytes{delimiter}{Psize(engine.DisplayRawBytes, varz.OutBytes)}{delimiter}Msgs/Sec{delimiter}{stats.Rates?.OutMsgsRate:F1}{delimiter}Bytes/Sec{delimiter}{Psize(engine.DisplayRawBytes, (long)stats.Rates?.OutBytesRate)}{Environment.NewLine}{Environment.NewLine}" +
                $"Connections Polled{delimiter}{stats.Connz.NumConns}{Environment.NewLine}";

            List<string> headers = new()
            {
                "HOST", "CID", "NAME", "SUBS", "PENDING",
                "MSGS_TO", "MSGS_FROM", "BYTES_TO", "BYTES_FROM", "LANG", "VERSION", "UPTIME", "LAST_ACTIVITY"
            };
            if (engine.DisplaySubs)
            {
                headers.Add("SUBSCRIPTIONS");
            }
            info += string.Join(delimiter, headers) + $"{Environment.NewLine}";

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
                info += string.Join(delimiter, row) + $"{Environment.NewLine}";
            }
            return info;
        }

        // DNS ルックアップ（結果はキャッシュする）
        static Dictionary<string, string> dnsCache = new();
        static string DNSLookup(string ip)
        {
            if (dnsCache.ContainsKey(ip))
                return dnsCache[ip];
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

        // バイトサイズを人間に読みやすい文字列に変換
        static string Psize(bool displayRawValue, long s)
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

        // 数値を読みやすい形式に変換（K, M, B, T 単位）
        static string Nsize(bool displayRawValue, long s)
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

        static string FormatDateTime(string isoDateTime)
        {
            try
            {
                // DateTimeOffsetをパース
                DateTimeOffset dto = DateTimeOffset.Parse(isoDateTime);

                // DateTimeに変換（ローカルタイムゾーンを考慮）
                DateTime dt = dto.DateTime;

                // yyyy/MM/dd HH:mm 形式でフォーマット
                return dt.ToString("yyyy/MM/dd HH:mm");
            }
            catch
            {
                return isoDateTime;
            }

        }

        #endregion
    }

    #region オプションと Engine クラス

    // コマンドラインオプション
    class Options
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 8222;
        public int HttpsPort { get; set; } = 0;
        public int Conns { get; set; } = 1024;
        public int Delay { get; set; } = 1;
        public string SortBy { get; set; } = "cid";
        public bool LookupDNS { get; set; } = false;
        public string OutputFile { get; set; } = "";
        public string OutputDelimiter { get; set; } = "";
        public bool DisplayRawBytes { get; set; } = false;
        public int MaxStatsRefreshes { get; set; } = -1;
        public bool ShowVersion { get; set; } = false;
        public bool DisplaySubscriptionsColumn { get; set; } = false;
        public string Cert { get; set; } = "";
        public string Key { get; set; } = "";
        public string CACert { get; set; } = "";
        public bool SkipVerify { get; set; } = false;
        public string Version { get; set; } = "0.0.0";

        public static Options Parse(string[] args)
        {
            Options opts = new();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "-s" && i + 1 < args.Length)
                {
                    opts.Host = args[++i];
                }
                else if (arg == "-m" && i + 1 < args.Length && int.TryParse(args[++i], out int port))
                {
                    opts.Port = port;
                }
                else if (arg == "-ms" && i + 1 < args.Length && int.TryParse(args[++i], out int httpsPort))
                {
                    opts.HttpsPort = httpsPort;
                }
                else if (arg == "-n" && i + 1 < args.Length && int.TryParse(args[++i], out int conns))
                {
                    opts.Conns = conns;
                }
                else if (arg == "-d" && i + 1 < args.Length && int.TryParse(args[++i], out int delay))
                {
                    opts.Delay = delay;
                }
                else if (arg == "-sort" && i + 1 < args.Length)
                {
                    opts.SortBy = args[++i];
                }
                else if (arg == "-lookup")
                {
                    opts.LookupDNS = true;
                }
                else if (arg == "-o" && i + 1 < args.Length)
                {
                    opts.OutputFile = args[++i];
                }
                else if (arg == "-l" && i + 1 < args.Length)
                {
                    opts.OutputDelimiter = args[++i];
                }
                else if (arg == "-b")
                {
                    opts.DisplayRawBytes = true;
                }
                else if (arg == "-r" && i + 1 < args.Length && int.TryParse(args[++i], out int max))
                {
                    opts.MaxStatsRefreshes = max;
                }
                else if (arg == "-v" || arg == "--version")
                {
                    opts.ShowVersion = true;
                }
                else if (arg == "-u" || arg == "--display-subscriptions-column")
                {
                    opts.DisplaySubscriptionsColumn = true;
                }
                else if (arg == "-cert" && i + 1 < args.Length)
                {
                    opts.Cert = args[++i];
                }
                else if (arg == "-key" && i + 1 < args.Length)
                {
                    opts.Key = args[++i];
                }
                else if (arg == "-cacert" && i + 1 < args.Length)
                {
                    opts.CACert = args[++i];
                }
                else if (arg == "-k")
                {
                    opts.SkipVerify = true;
                }
            }
            return opts;
        }
    }

    // NATS サーバーから統計情報を取得・計算するエンジンクラス
    class Engine
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
                Varz varz = JsonSerializer.Deserialize<Varz>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return varz;
            }
            else if (path.StartsWith("/connz"))
            {
                Connz connz = JsonSerializer.Deserialize<Connz>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
                stats.Varz = (Varz)await Request("/varz");
                stats.Connz = (Connz)await Request("/connz");
            }
            catch (Exception ex)
            {
                stats.Error = ex.Message;
                return stats;
            }

            // 前回統計がある場合は、経過時間から各種レートを計算する
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
                                cr.InMsgsRate = (conn.InMsgs - lastConn.InMsgs);
                                cr.OutMsgsRate = (conn.OutMsgs - lastConn.OutMsgs);
                                cr.InBytesRate = (conn.InBytes - lastConn.InBytes);
                                cr.OutBytesRate = (conn.OutBytes - lastConn.OutBytes);
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

    #endregion

    #region データクラス

    // /varz エンドポイントの情報
    public class Varz
    {
        [JsonPropertyName("cpu")]
        public float CPU { get; set; }

        [JsonPropertyName("mem")]
        public long Mem { get; set; }

        [JsonPropertyName("uptime")]
        public string Uptime { get; set; }

        [JsonPropertyName("in_msgs")]
        public long InMsgs { get; set; }

        [JsonPropertyName("out_msgs")]
        public long OutMsgs { get; set; }

        [JsonPropertyName("in_bytes")]
        public long InBytes { get; set; }

        [JsonPropertyName("out_bytes")]
        public long OutBytes { get; set; }

        [JsonPropertyName("slow_consumers")]
        public int SlowConsumers { get; set; }

        [JsonPropertyName("server_id")]
        public string ID { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("server_name")]
        public string Name { get; set; }

        [JsonPropertyName("now")]
        public DateTime Now { get; set; }
    }

    // /connz エンドポイントの情報
    public class Connz
    {
        [JsonPropertyName("num_connections")]
        public int NumConns { get; set; }

        [JsonPropertyName("connections")]
        public List<ConnInfo> Conns { get; set; } = new List<ConnInfo>();
    }

    // 各接続の情報
    public class ConnInfo
    {
        [JsonPropertyName("cid")]
        public ulong Cid { get; set; }

        [JsonPropertyName("ip")]
        public string IP { get; set; }

        [JsonPropertyName("port")]
        public int Port { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("subscriptions")]
        public int NumSubs { get; set; }

        [JsonPropertyName("pending_bytes")]
        public long Pending { get; set; }

        [JsonPropertyName("out_msgs")]
        public long OutMsgs { get; set; }

        [JsonPropertyName("in_msgs")]
        public long InMsgs { get; set; }

        [JsonPropertyName("out_bytes")]
        public long OutBytes { get; set; }

        [JsonPropertyName("in_bytes")]
        public long InBytes { get; set; }

        [JsonPropertyName("lang")]
        public string Lang { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; }

        [JsonPropertyName("uptime")]
        public string Uptime { get; set; }

        [JsonPropertyName("last_activity")]
        public string LastActivity { get; set; }

        [JsonPropertyName("subs")]
        public List<string> Subs { get; set; }
    }

    // 統計情報全体
    public class Stats
    {
        [JsonPropertyName("varz")]
        public Varz Varz { get; set; }

        [JsonPropertyName("connz")]
        public Connz Connz { get; set; }

        [JsonPropertyName("rates")]
        public Rates Rates { get; set; }

        [JsonPropertyName("error")]
        public string Error { get; set; } = "";
    }

    // 全体のレート情報
    public class Rates
    {
        [JsonPropertyName("in_msgs_rate")]
        public double InMsgsRate { get; set; }

        [JsonPropertyName("out_msgs_rate")]
        public double OutMsgsRate { get; set; }

        [JsonPropertyName("in_bytes_rate")]
        public double InBytesRate { get; set; }

        [JsonPropertyName("out_bytes_rate")]
        public double OutBytesRate { get; set; }

        [JsonPropertyName("connections")]
        public Dictionary<ulong, ConnRates> Connections { get; set; } = new Dictionary<ulong, ConnRates>();
    }

    // 各接続ごとのレート情報
    public class ConnRates
    {
        [JsonPropertyName("in_msgs_rate")]
        public double InMsgsRate { get; set; }

        [JsonPropertyName("out_msgs_rate")]
        public double OutMsgsRate { get; set; }

        [JsonPropertyName("in_bytes_rate")]
        public double InBytesRate { get; set; }

        [JsonPropertyName("out_bytes_rate")]
        public double OutBytesRate { get; set; }
    }

    #endregion
}
