using System.Text.Json;

namespace GalDanmaku.Core.Text;

public sealed record TextSourceOptions
{
    public string Endpoint { get; init; } = "ws://127.0.0.1:2333/api/ws/text/origin";
    public int InitialRetryMilliseconds { get; init; } = 500;
    public int MaximumRetryMilliseconds { get; init; } = 10000;
    public int ConnectTimeoutMilliseconds { get; init; } = 5000;
    public int MergeGapMilliseconds { get; init; } = 1500;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint) || Endpoint.Length > 2048 || !Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("ws" or "wss") || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("请输入有效的 ws:// 或 wss:// 地址，不能包含用户名、密码或片段。例：ws://127.0.0.1:2333/api/ws/text/origin");
        if (InitialRetryMilliseconds is < 50 or > 60000 || MaximumRetryMilliseconds < InitialRetryMilliseconds || MaximumRetryMilliseconds > 120000)
            throw new ArgumentException("初始重连间隔须为 50–60000 毫秒，最大间隔须不小于初始值且不超过 120000 毫秒。");
        if (ConnectTimeoutMilliseconds is < 100 or > 60000)
            throw new ArgumentException("连接超时须为 100–60000 毫秒。");
        if (MergeGapMilliseconds is < 0 or > 30000)
            throw new ArgumentException("逐字合并间隔须为 0–30000 毫秒。");
    }

    public static TextSourceOptions Load(string path)
    {
        TextSourceOptions options = JsonSerializer.Deserialize<TextSourceOptions>(File.ReadAllText(path))
            ?? throw new JsonException("文本源配置必须是 JSON 对象。");
        options.Validate();
        return options;
    }
}
