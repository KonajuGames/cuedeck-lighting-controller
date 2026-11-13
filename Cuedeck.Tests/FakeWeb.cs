using System.Net;
using System.Text;

namespace Cuedeck.Tests;

/// <summary>
/// Answers requests from a table of URL → (status, body, headers), and keeps every request it's sent.
/// </summary>
sealed class FakeWeb : HttpMessageHandler
{
    public readonly Dictionary<string, (HttpStatusCode Code, byte[] Body, string[] Cookies)> Pages = [];
    public readonly List<(HttpMethod Method, string Url, string Cookie, string Body)> Sent = [];

    public void Json(string url, string json, HttpStatusCode code = HttpStatusCode.OK, params string[] cookies) =>
        Pages[url] = (code, Encoding.UTF8.GetBytes(json), cookies);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Sent.Add((request.Method, url, request.Headers.TryGetValues("Cookie", out var c) ? string.Join("; ", c) : "", body));
        if (!Pages.TryGetValue(url, out var page))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        var response = new HttpResponseMessage(page.Code) { Content = new ByteArrayContent(page.Body) };
        foreach (var cookie in page.Cookies)
            response.Headers.Add("Set-Cookie", cookie);
        return response;
    }
}
