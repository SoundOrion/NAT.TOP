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
                Console.Clear();
                string text = Utility.GenerateParagraphPlainText(engine, engine.LastStats);
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

}
