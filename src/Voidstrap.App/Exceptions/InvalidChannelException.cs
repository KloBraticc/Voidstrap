using System;
using System.Net;

namespace Voidstrap.Exceptions;

public class InvalidChannelException(HttpStatusCode? statusCode) : Exception
{
	public HttpStatusCode? StatusCode = statusCode;
}
