using System.Net.Http;
using System.Threading;

namespace Voidstrap;

internal sealed class HttpClientLoggingHandler : MessageProcessingHandler
{
    public HttpClientLoggingHandler(HttpMessageHandler innerHandler)
        : base(innerHandler) { }

    internal static readonly HttpRequestOptionsKey<bool> NotFoundExpected = new("Voidstrap.NotFoundExpected");

    protected override HttpRequestMessage ProcessRequest(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return request;
    }

    protected override HttpResponseMessage ProcessResponse(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (
            !response.IsSuccessStatusCode
            && !(
                response.StatusCode == System.Net.HttpStatusCode.NotFound
                && response.RequestMessage?.Options.TryGetValue(NotFoundExpected, out bool expected) == true
                && expected
            )
        )
            App.Logger.WriteLine(
                "HttpClientLoggingHandler::ProcessResponse",
                $"{(int)response.StatusCode} {response.ReasonPhrase} {SafeUri(response.RequestMessage?.RequestUri)}"
            );
        return response;
    }

    private static string SafeUri(System.Uri? uri)
    {
        if (uri == null)
            return "";
        return uri.IsAbsoluteUri ? uri.GetLeftPart(System.UriPartial.Path) : uri.OriginalString.Split('?', '#')[0];
    }
}
