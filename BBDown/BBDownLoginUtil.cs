using QRCoder;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using static BBDown.BBDownUtil;
using static BBDown.Core.Logger;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using BBDown.Core.Util;

namespace BBDown;

internal static class BBDownLoginUtil
{
    // 不使用CookieContainer, 以便读取原始的 Set-Cookie 响应头
    private static readonly HttpClient RawCookieClient = new(new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static readonly string[] RequiredWebCookies = ["SESSDATA", "bili_jct", "DedeUserID"];

    public static async Task<string> GetLoginStatusAsync(string qrcodeKey)
    {
        string queryUrl = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}&source=main-fe-header";
        return await HTTPUtil.GetWebSourceAsync(queryUrl);
    }

    /// <summary>轮询扫码状态, 同时返回响应中的 Set-Cookie</summary>
    private static async Task<(string body, List<string> setCookies)> PollWebLoginAsync(string qrcodeKey)
    {
        string queryUrl = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}&source=main-fe-header";
        using var request = new HttpRequestMessage(HttpMethod.Get, queryUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        using var response = (await RawCookieClient.SendAsync(request)).EnsureSuccessStatusCode();
        var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];
        return (await response.Content.ReadAsStringAsync(), setCookies);
    }

    /// <summary>
    /// 访问登录成功后返回的跨域登录url(及其跳转), 收集沿途下发的 Set-Cookie.
    /// 仅在轮询响应本身没有下发cookie时使用.
    /// </summary>
    private static async Task<List<string>> CollectCookiesFromUrlAsync(string url)
    {
        var cookies = new List<string>();
        for (int i = 0; i < 5 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            using var response = await RawCookieClient.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var values)) cookies.AddRange(values);
            if (response.Headers.Location is null) break;
            url = new Uri(uri, response.Headers.Location).AbsoluteUri;
        }
        return cookies;
    }

    /// <summary>
    /// 由 Set-Cookie 头(以及旧版接口在url参数中返回的cookie)生成 BBDown.data 的内容:
    /// "SESSDATA=...;bili_jct=...;DedeUserID=...;...;Expires=unix秒".
    /// 缺少 SESSDATA 时返回 null.
    /// </summary>
    private static string? BuildWebCookie(IEnumerable<string> setCookies, string loginUrl)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        long? expires = null;
        foreach (var header in setCookies)
        {
            var parts = header.Split(';');
            var kv = parts[0].Split('=', 2);
            if (kv.Length != 2) continue;
            string name = kv[0].Trim(), value = kv[1].Trim();
            if (name == "" || value == "") continue;
            //转义英文逗号 否则部分场景会出问题
            cookies[name] = value.Replace(",", "%2C");
            if (name != "SESSDATA") continue;
            foreach (var attr in parts.Skip(1).Select(a => a.Trim()))
            {
                if (attr.StartsWith("Expires=", StringComparison.OrdinalIgnoreCase)
                    && DateTimeOffset.TryParseExact(attr[8..].Trim(), "r", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var dt))
                {
                    expires = dt.ToUnixTimeSeconds();
                }
                else if (attr.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(attr[8..].Trim(), out var maxAge))
                {
                    expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + maxAge;
                }
            }
        }

        // 旧版接口: cookie 直接放在跨域登录url的参数里
        int q = loginUrl.IndexOf('?');
        if (!cookies.ContainsKey("SESSDATA") && q >= 0)
        {
            foreach (var pair in loginUrl[(q + 1)..].Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] != "" && kv[1] != "" && !cookies.ContainsKey(kv[0]))
                    cookies[kv[0]] = kv[1].Replace(",", "%2C");
            }
        }

        if (!cookies.ContainsKey("SESSDATA")) return null;
        if (expires is not null && !cookies.ContainsKey("Expires")) cookies["Expires"] = expires.Value.ToString(CultureInfo.InvariantCulture);
        return string.Join(";", cookies.Select(c => $"{c.Key}={c.Value}"));
    }

    /// <summary>用新cookie请求nav接口, 确认登录有效. 网络失败时返回 (null, null)</summary>
    private static async Task<(bool? isLogin, string? uname)> CheckWebCookieAsync(string cookie)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.bilibili.com/x/web-interface/nav");
            request.Headers.TryAddWithoutValidation("User-Agent", HTTPUtil.UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await RawCookieClient.SendAsync(request);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return (false, null);
            bool isLogin = data.TryGetProperty("isLogin", out var l) && l.ValueKind == JsonValueKind.True;
            string? uname = data.TryGetProperty("uname", out var u) ? u.GetString() : null;
            return (isLogin, uname);
        }
        catch (Exception e)
        {
            LogDebug("检查登录状态失败: {0}", e.Message);
            return (null, null);
        }
    }

    public static async Task LoginWEB()
    {
        try
        {
            Log("获取登录地址...");
            string loginUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header";
            string url = JsonDocument.Parse(await HTTPUtil.GetWebSourceAsync(loginUrl)).RootElement.GetProperty("data").GetProperty("url").ToString();
            string qrcodeKey = GetQueryString("qrcode_key", url);
            //Log(oauthKey);
            //Log(url);
            bool flag = false;
            Log("生成二维码...");
            QRCodeGenerator qrGenerator = new();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            PngByteQRCode pngByteCode = new(qrCodeData);
            await File.WriteAllBytesAsync("qrcode.png", pngByteCode.GetGraphic(7));
            Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
            var consoleQRCode = new ConsoleQRCode(qrCodeData);
            consoleQRCode.GetGraphic();

            while (true)
            {
                await Task.Delay(1000);
                var (w, setCookies) = await PollWebLoginAsync(qrcodeKey);
                int code = JsonDocument.Parse(w).RootElement.GetProperty("data").GetProperty("code").GetInt32();
                if (code == 86038)
                {
                    LogColor("二维码已过期, 请重新执行登录指令.");
                    break;
                }
                else if (code == 86101) //等待扫码
                {
                    continue;
                }
                else if (code == 86090) //等待确认
                {
                    if (!flag)
                    {
                        Log("扫码成功, 请确认...");
                        flag = !flag;
                    }
                }
                else if (code == 0)
                {
                    string crossDomainUrl = JsonDocument.Parse(w).RootElement.GetProperty("data").GetProperty("url").ToString();
                    // 新版接口通过 Set-Cookie 下发cookie, url 中只有 ticket 等参数
                    string? cookie = BuildWebCookie(setCookies, crossDomainUrl);
                    if (cookie is null && !string.IsNullOrEmpty(crossDomainUrl))
                    {
                        LogDebug("轮询响应中没有SESSDATA, 尝试访问跨域登录地址获取cookie");
                        cookie = BuildWebCookie(setCookies.Concat(await CollectCookiesFromUrlAsync(crossDomainUrl)), "");
                    }
                    if (cookie is null)
                    {
                        LogError("登录失败: 未能从登录响应中获取SESSDATA, 请使用 --debug 查看详细信息后反馈");
                        LogDebug("Set-Cookie: {0}", string.Join(" | ", setCookies.Select(c => c.Split(';')[0].Split('=')[0])));
                        break;
                    }
                    var missing = RequiredWebCookies.Where(n => !cookie.Split(';').Any(c => c.StartsWith(n + "="))).ToList();
                    if (missing.Count > 0) LogWarn("登录cookie缺少: " + string.Join(", ", missing));

                    var (isLogin, uname) = await CheckWebCookieAsync(cookie);
                    if (isLogin == false)
                    {
                        LogError("登录失败: B站未接受获取到的cookie, 请重新执行登录指令");
                        break;
                    }
                    await File.WriteAllTextAsync(Path.Combine(Program.APP_DIR, "BBDown.data"), cookie);
                    File.Delete("qrcode.png");
                    Log(uname is null ? "登录成功" : $"登录成功: {uname}");
                    break;
                }
                else
                {
                    LogError($"登录失败: {JsonDocument.Parse(w).RootElement.GetProperty("data").GetProperty("message")} (code={code})");
                    break;
                }
            }
        }
        catch (Exception e) { LogError(e.Message); }
    }

    public static async Task LoginTV()
    {
        try
        {
            string loginUrl = "https://passport.snm0516.aisee.tv/x/passport-tv-login/qrcode/auth_code";
            string pollUrl = "https://passport.bilibili.com/x/passport-tv-login/qrcode/poll";
            var parms = GetTVLoginParms();
            Log("获取登录地址...");
            byte[] responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(loginUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
            string web = Encoding.UTF8.GetString(responseArray);
            string url = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("url").ToString();
            string authCode = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("auth_code").ToString();
            Log("生成二维码...");
            QRCodeGenerator qrGenerator = new();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            PngByteQRCode pngByteCode = new(qrCodeData);
            await File.WriteAllBytesAsync("qrcode.png", pngByteCode.GetGraphic(7));
            Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
            var consoleQRCode = new ConsoleQRCode(qrCodeData);
            consoleQRCode.GetGraphic();
            parms.Set("auth_code", authCode);
            parms.Set("ts", GetTimeStamp(true));
            parms.Remove("sign");
            parms.Add("sign", GetSign(ToQueryString(parms)));
            while (true)
            {
                await Task.Delay(1000);
                responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(pollUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
                web = Encoding.UTF8.GetString(responseArray);
                string code = JsonDocument.Parse(web).RootElement.GetProperty("code").ToString();
                if (code == "86038")
                {
                    LogColor("二维码已过期, 请重新执行登录指令.");
                    break;
                }
                else if (code == "86039") //等待扫码
                {
                    continue;
                }
                else
                {
                    string cc = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("access_token").ToString();
                    Log("登录成功: AccessToken=" + cc);
                    //导出cookie
                    await File.WriteAllTextAsync(Path.Combine(Program.APP_DIR, "BBDownTV.data"), "access_token=" + cc);
                    File.Delete("qrcode.png");
                    break;
                }
            }
        }
        catch (Exception e) { LogError(e.Message); }
    }
}