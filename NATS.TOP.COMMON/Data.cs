using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace NATS.TOP.COMMON;

#region データクラス

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

public class Connz
{
    [JsonPropertyName("num_connections")]
    public int NumConns { get; set; }

    [JsonPropertyName("connections")]
    public List<ConnInfo> Conns { get; set; } = new();
}

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
    public Dictionary<ulong, ConnRates> Connections { get; set; } = new();
}

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
