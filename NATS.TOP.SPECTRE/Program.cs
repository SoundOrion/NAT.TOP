using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Spectre.Console;
using NATS.TOP.COMMON;

namespace NatsTopCSharp;

class Program
{
    /// <summary>
    /// エントリポイント
    /// </summary>
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
                ? Utility.GenerateParagraphCSV(engine, stats, opts.OutputDelimiter)
                : Utility.GenerateParagraphPlainText(engine, stats);
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
        using CancellationTokenSource cts = new();
        Task monitoringTask = engine.MonitorStats(cts.Token);

        // カーソルを非表示にする
        Console.CursorVisible = false;

        // メインループ：一定間隔で最新統計情報を表示＆キー入力処理
        int refreshCount = 0;
        while (!cts.IsCancellationRequested)
        {
            if (engine.LastStats != null)
            {
                Console.SetCursorPosition(0, 0); // カーソルを左上に移動
                AnsiConsole.Clear();
                RenderStats(engine, engine.LastStats);
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

    #region Spectre.Console 表示処理

    /// <summary>
    /// Spectre.Console を使って最新の統計情報を表示する
    /// </summary>
    static void RenderStats(Engine engine, Stats stats)
    {
        Varz varz = stats.Varz;

        // サーバ情報の文字列を構築し、Panel として表示
        var serverInfo = new StringBuilder();
        serverInfo.AppendLine($"[bold]NATS server version:[/] {varz.Version} (uptime: {varz.Uptime}) {stats.Error}");
        serverInfo.AppendLine($"[bold]Server:[/] {varz.Name}");
        serverInfo.AppendLine($"[bold]ID:[/] {varz.ID}");
        serverInfo.AppendLine($"[bold]Load:[/] CPU: {varz.CPU:F1}%  Memory: {Utility.Psize(false, varz.Mem)}  Slow Consumers: {varz.SlowConsumers}");
        serverInfo.AppendLine($"[bold]In:[/]  Msgs: {Utility.Nsize(engine.DisplayRawBytes, varz.InMsgs)}  Bytes: {Utility.Psize(engine.DisplayRawBytes, varz.InBytes)}  Msgs/Sec: {stats.Rates?.InMsgsRate:F1}  Bytes/Sec: {Utility.Psize(engine.DisplayRawBytes, (long)(stats.Rates?.InBytesRate ?? 0))}");
        serverInfo.AppendLine($"[bold]Out:[/] Msgs: {Utility.Nsize(engine.DisplayRawBytes, varz.OutMsgs)}  Bytes: {Utility.Psize(engine.DisplayRawBytes, varz.OutBytes)}  Msgs/Sec: {stats.Rates?.OutMsgsRate:F1}  Bytes/Sec: {Utility.Psize(engine.DisplayRawBytes, (long)(stats.Rates?.OutBytesRate ?? 0))}");
        serverInfo.AppendLine($"");
        serverInfo.AppendLine($"Connections Polled: {stats.Connz.NumConns}");
        var panel = new Panel(serverInfo.ToString())
        {
            Border = BoxBorder.Rounded,
            Header = new PanelHeader("Server Info", Justify.Center)
        };
        AnsiConsole.Write(panel);

        // 接続情報を表示する Table を構築
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.Expand = true;

        // ヘッダー列（必要に応じて NAME 列・SUBSCRIPTIONS 列を追加）
        table.AddColumn("HOST");
        table.AddColumn("CID");
        table.AddColumn("NAME");
        table.AddColumn("SUBS");
        table.AddColumn("PENDING");

        if (!engine.ShowRates)
        {
            table.AddColumn("MSGS_TO");
            table.AddColumn("MSGS_FROM");
            table.AddColumn("BYTES_TO");
            table.AddColumn("BYTES_FROM");
        }
        else
        {
            table.AddColumn("OUT_MSGS_RATE");
            table.AddColumn("IN_MSGS_RATE");
            table.AddColumn("OUT_BYTES_RATE");
            table.AddColumn("IN_BYTES_RATE");
        }

        table.AddColumn("LANG");
        table.AddColumn("VERSION");
        table.AddColumn("UPTIME");
        table.AddColumn("LAST_ACTIVITY");
        if (engine.DisplaySubs)
        {
            table.AddColumn("SUBSCRIPTIONS");
        }

        // 各接続ごとに行を追加
        if (stats.Connz?.Conns != null)
        {
            foreach (var conn in stats.Connz.Conns)
            {
                string hostname = engine.LookupDNS ? Utility.DNSLookup(conn.IP) : $"{conn.IP}:{conn.Port}";
                string cid = conn.Cid.ToString();
                string name = conn.Name ?? "";
                string subsCount = conn.NumSubs.ToString();
                string pending = Utility.Nsize(engine.DisplayRawBytes, conn.Pending);
                string colMsgsTo, colMsgsFrom, colBytesTo, colBytesFrom;

                if (!engine.ShowRates)
                {
                    colMsgsTo = Utility.Nsize(engine.DisplayRawBytes, conn.OutMsgs);
                    colMsgsFrom = Utility.Nsize(engine.DisplayRawBytes, conn.InMsgs);
                    colBytesTo = Utility.Psize(engine.DisplayRawBytes, conn.OutBytes);
                    colBytesFrom = Utility.Psize(engine.DisplayRawBytes, conn.InBytes);
                }
                else
                {
                    Rates rates = stats.Rates;
                    ConnRates cr = (rates != null && rates.Connections.ContainsKey(conn.Cid))
                        ? rates.Connections[conn.Cid] : new ConnRates();
                    colMsgsTo = Utility.Nsize(engine.DisplayRawBytes, (long)cr.OutMsgsRate);
                    colMsgsFrom = Utility.Nsize(engine.DisplayRawBytes, (long)cr.InMsgsRate);
                    colBytesTo = Utility.Psize(engine.DisplayRawBytes, (long)cr.OutBytesRate);
                    colBytesFrom = Utility.Psize(engine.DisplayRawBytes, (long)cr.InBytesRate);
                }

                string lang = conn.Lang;
                string version = conn.Version;
                string uptime = conn.Uptime;
                string lastActivity = Utility.FormatDateTime(conn.LastActivity);
                string subscriptions = engine.DisplaySubs ? (conn.Subs != null ? string.Join(", ", conn.Subs) : "") : "";

                var row = new List<string>
                {
                    hostname,
                    cid,
                    name,
                    subsCount,
                    pending
                };

                if (!engine.ShowRates)
                {
                    row.Add(colMsgsTo);
                    row.Add(colMsgsFrom);
                    row.Add(colBytesTo);
                    row.Add(colBytesFrom);
                }
                else
                {
                    row.Add(colMsgsTo);
                    row.Add(colMsgsFrom);
                    row.Add(colBytesTo);
                    row.Add(colBytesFrom);
                }

                row.Add(lang);
                row.Add(version);
                row.Add(uptime);
                row.Add(lastActivity);
                if (engine.DisplaySubs)
                {
                    row.Add(subscriptions);
                }
                table.AddRow(row.ToArray());
            }
        }
        AnsiConsole.Write(table);
    }

    #endregion

}
