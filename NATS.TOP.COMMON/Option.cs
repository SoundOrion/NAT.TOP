using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NATS.TOP.COMMON;

/// <summary>
/// コマンドラインオプション
/// </summary>
public class Options
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
