using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GiveALikeBlazorApp.Services;

/// <summary>
/// Calls the bilibili Web API to like a video, using cookies
/// extracted from the local browser by <see cref="CookieExtractor"/>.
/// </summary>
public sealed class BiliApiClient
{
    private readonly CookieExtractor _cookies;
    private readonly ILogger<BiliApiClient> _logger;

    public BiliApiClient(CookieExtractor cookies, ILogger<BiliApiClient> logger)
    {
        _cookies = cookies;
        _logger = logger;
    }

    public sealed record LikeResult(bool Success, string Message);

    /// <summary>
    /// Attempts to like the video identified by <paramref name="bvid"/>.
    /// </summary>
    public async Task<LikeResult> LikeVideoAsync(string bvid)
    {
        // 1. Extract cookies from the browser
        var creds = _cookies.ExtractBiliCookies();
        if (creds is null)
        {
            return new LikeResult(false,
                "无法从本地浏览器读取登录信息，请确保已在 Edge 或 Chrome 中登录 B 站，" +
                "且浏览器未处于无痕模式。");
        }

        // 2. Build an HttpClient with the extracted cookies
        var container = new CookieContainer();
        container.Add(new Uri("https://api.bilibili.com"),
            new Cookie("SESSDATA", Uri.EscapeDataString(creds.Sessdata)));
        container.Add(new Uri("https://api.bilibili.com"),
            new Cookie("bili_jct", creds.BiliJct));

        using var handler = new HttpClientHandler { CookieContainer = container, UseCookies = true };
        using var client = new HttpClient(handler);

        client.DefaultRequestHeaders.Add("Referer", "https://www.bilibili.com");
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0");

        // 3. POST the like request
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["bvid"] = bvid,
            ["like"] = "1",
            ["csrf"] = creds.BiliJct
        });

        try
        {
            _logger.LogInformation("Sending like request for {Bvid}", bvid);

            var resp = await client.PostAsync(
                "https://api.bilibili.com/x/web-interface/archive/like", form);

            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogDebug("Bilibili API response: {Body}", body);

            var api = JsonSerializer.Deserialize<BiliResponse>(body);
            if (api is null)
                return new LikeResult(false, "无法解析 B 站 API 响应");

            return api.Code switch
            {
                0     => new LikeResult(true, "点赞成功！🎉"),
                65006 => new LikeResult(true, "您已经点赞过该视频了 👍"),
                -101  => new LikeResult(false, "登录信息已过期，请重新登录 B 站后再试"),
                -111  => new LikeResult(false, "CSRF 校验失败，请重新登录 B 站后再试"),
                _     => new LikeResult(false, $"B 站返回错误：{api.Message} (code {api.Code})")
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error calling bilibili API");
            return new LikeResult(false, $"网络请求失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error calling bilibili API");
            return new LikeResult(false, $"操作失败：{ex.Message}");
        }
    }

    private sealed record BiliResponse(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("message")] string? Message);
}
