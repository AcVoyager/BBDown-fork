using BBDown.Core.Entity;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static BBDown.Core.Entity.Entity;
using static BBDown.Core.Util.HTTPUtil;


namespace BBDown.Core.Fetcher;

/// <summary>
/// 收藏夹解析
/// https://space.bilibili.com/3/favlist
///
/// 私密收藏夹需要登录: 有网页cookie时使用cookie; 只有TV/APP的access_token时,
/// 使用TV端appkey签名请求(web接口同样接受access_key鉴权).
/// </summary>
public class FavListFetcher : IFetcher
{
    // 与 BBDown logintv 获取 access_token 时使用的 appkey 一致, 签名必须使用同一组
    private const string TvAppKey = "4409e2ce8ffd12b8";
    private const string TvAppSec = "59b43e04ad6965f34319062b478f83dd";

    public async Task<VInfo> FetchAsync(string id)
    {
        id = id[6..];
        var favId = id.Split(':')[0];
        var mid = id.Split(':')[1];
        //查找默认收藏夹
        if (favId == "")
        {
            using var listJson = await GetApiDataAsync("https://api.bilibili.com/x/v3/fav/folder/created/list-all",
                new() { ["up_mid"] = mid }, "获取收藏夹列表失败");
            var list = listJson.RootElement.GetProperty("data").GetProperty("list");
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
                throw new Exception("获取收藏夹列表失败: 该用户没有可访问的收藏夹(私密收藏夹需要登录)");
            favId = list[0].GetProperty("id").ToString();
        }

        int pageSize = 20;
        int index = 1;
        List<Page> pagesInfo = new();

        using var infoJson = await GetApiDataAsync("https://api.bilibili.com/x/v3/fav/resource/list",
            ResourceListParams(favId, 1, pageSize), "获取收藏夹信息失败");
        var data = infoJson.RootElement.GetProperty("data");
        int totalCount = data.GetProperty("info").GetProperty("media_count").GetInt32();
        int totalPage = (int)Math.Ceiling((double)totalCount / pageSize);
        var title = data.GetProperty("info").GetProperty("title").GetString()!;
        var intro = data.GetProperty("info").GetProperty("intro").GetString()!;
        long pubTime = data.GetProperty("info").GetProperty("ctime").GetInt64();
        var medias = GetMedias(data);
        var pageDocs = new List<JsonDocument>();

        try
        {
            for (int page = 2; page <= totalPage; page++)
            {
                var jsonDoc = await GetApiDataAsync("https://api.bilibili.com/x/v3/fav/resource/list",
                    ResourceListParams(favId, page, pageSize), "获取收藏夹视频列表失败");
                pageDocs.Add(jsonDoc);
                var pageMedias = GetMedias(jsonDoc.RootElement.GetProperty("data"));
                // media_count 包含失效视频, 实际列表可能提前结束
                if (pageMedias.Count == 0) break;
                medias.AddRange(pageMedias);
            }

            foreach (var m in medias)
            {
                //只处理视频类型(可以直接在query param上指定type=2)
                // if (m.GetProperty("type").GetInt32() != 2) continue;
                //只处理未失效视频
                if (m.GetProperty("attr").GetInt32() != 0) continue;

                var pageCount = m.GetProperty("page").GetInt32();
                if (pageCount > 1)
                {
                    var tmpInfo = await new NormalInfoFetcher().FetchAsync(m.GetProperty("id").ToString());
                    foreach (var item in tmpInfo.PagesInfo)
                    {
                        Page p = new(index++, item)
                        {
                            title = m.GetProperty("title").ToString() + $"_P{item.index}_{item.title}",
                            cover = tmpInfo.Pic,
                            desc = m.GetProperty("intro").ToString()
                        };
                        if (!pagesInfo.Contains(p)) pagesInfo.Add(p);
                    }
                }
                else
                {
                    // 非普通视频(ugc为null)时没有first_cid, 交给NormalInfoFetcher获取
                    if (!m.TryGetProperty("ugc", out var ugc) || ugc.ValueKind != JsonValueKind.Object)
                    {
                        var tmpInfo = await new NormalInfoFetcher().FetchAsync(m.GetProperty("id").ToString());
                        foreach (var item in tmpInfo.PagesInfo)
                        {
                            Page p = new(index++, item);
                            if (!pagesInfo.Contains(p)) pagesInfo.Add(p);
                        }
                        continue;
                    }
                    var upper = m.GetProperty("upper");
                    Page page = new(index++,
                        m.GetProperty("id").ToString(),
                        ugc.GetProperty("first_cid").ToString(),
                        "", //epid
                        m.GetProperty("title").ToString(),
                        m.GetProperty("duration").GetInt32(),
                        "",
                        m.GetProperty("pubtime").GetInt64(),
                        m.GetProperty("cover").ToString(),
                        m.GetProperty("intro").ToString(),
                        upper.ValueKind == JsonValueKind.Object ? upper.GetProperty("name").ToString() : "",
                        upper.ValueKind == JsonValueKind.Object ? upper.GetProperty("mid").ToString() : "");
                    if (!pagesInfo.Contains(page)) pagesInfo.Add(page);
                }
            }
        }
        finally
        {
            foreach (var doc in pageDocs) doc.Dispose();
        }

        if (pagesInfo.Count == 0)
            throw new Exception($"收藏夹「{title.Trim()}」中没有可下载的视频(可能均已失效)");

        var info = new VInfo
        {
            Title = title.Trim(),
            Desc = intro.Trim(),
            Pic = "",
            PubTime = pubTime,
            PagesInfo = pagesInfo,
            IsBangumi = false
        };

        return info;
    }

    private static Dictionary<string, string> ResourceListParams(string favId, int page, int pageSize) => new()
    {
        ["media_id"] = favId,
        ["pn"] = page.ToString(),
        ["ps"] = pageSize.ToString(),
        ["order"] = "mtime",
        ["type"] = "2",
        ["tid"] = "0",
        ["platform"] = "web",
    };

    /// <summary>medias 在空收藏夹或超出页码时为 null</summary>
    private static List<JsonElement> GetMedias(JsonElement data)
    {
        return data.TryGetProperty("medias", out var medias) && medias.ValueKind == JsonValueKind.Array
            ? medias.EnumerateArray().ToList()
            : new List<JsonElement>();
    }

    /// <summary>
    /// 请求web接口并检查返回码. 没有cookie但有access_token时, 附加签名后的access_key.
    /// </summary>
    private static async Task<JsonDocument> GetApiDataAsync(string baseUrl, Dictionary<string, string> parms, string errorPrefix)
    {
        bool useToken = string.IsNullOrEmpty(Config.COOKIE) && !string.IsNullOrEmpty(Config.TOKEN);
        string query = useToken ? SignedQuery(parms) : string.Join("&", parms.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var doc = JsonDocument.Parse(await GetWebSourceAsync($"{baseUrl}?{query}"));
        var root = doc.RootElement;
        int code = root.TryGetProperty("code", out var codeElem) && codeElem.ValueKind == JsonValueKind.Number ? codeElem.GetInt32() : 0;
        bool hasData = root.TryGetProperty("data", out var dataElem) && dataElem.ValueKind == JsonValueKind.Object;
        if (code != 0 || !hasData)
        {
            string message = root.TryGetProperty("message", out var msgElem) && msgElem.ValueKind == JsonValueKind.String
                ? msgElem.GetString()!
                : "未知错误";
            doc.Dispose();
            string hint = code == -403 || code == -101
                ? (string.IsNullOrEmpty(Config.COOKIE) && string.IsNullOrEmpty(Config.TOKEN)
                    ? ", 该收藏夹可能是私密的, 请先登录(BBDown login 或 BBDown logintv 并使用 -tv)"
                    : ", 该收藏夹可能是私密的, 且当前登录的账号无权访问")
                : "";
            throw new Exception($"{errorPrefix}(code={code}): {message}{hint}");
        }
        return doc;
    }

    private static string SignedQuery(Dictionary<string, string> parms)
    {
        var all = new SortedDictionary<string, string>(parms, StringComparer.Ordinal)
        {
            ["access_key"] = Config.TOKEN,
            ["appkey"] = TvAppKey,
            ["ts"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString(),
        };
        string query = string.Join("&", all.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        string sign = string.Concat(MD5.HashData(Encoding.UTF8.GetBytes(query + TvAppSec)).Select(b => b.ToString("x2")));
        return $"{query}&sign={sign}";
    }
}
