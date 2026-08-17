using System.Net.Http;
using System.Threading;

namespace Voidstrap;

internal class HttpClientLoggingHandler : MessageProcessingHandler
{
	public HttpClientLoggingHandler(HttpMessageHandler innerHandler)
		: base(innerHandler)
	{
	}

	protected override HttpRequestMessage ProcessRequest(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		return request;
	}

	protected override HttpResponseMessage ProcessResponse(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (!response.IsSuccessStatusCode)
			App.Logger.WriteLine("HttpClientLoggingHandler::ProcessResponse", $"{(int)response.StatusCode} {response.ReasonPhrase} {SafeUri(response.RequestMessage?.RequestUri)}");
		return response;
	}

	private static string SafeUri(System.Uri? uri)
	{
		if (uri == null)
			return "";
		return uri.IsAbsoluteUri ? uri.GetLeftPart(System.UriPartial.Path) : uri.OriginalString.Split('?', '#')[0];
	}
}
