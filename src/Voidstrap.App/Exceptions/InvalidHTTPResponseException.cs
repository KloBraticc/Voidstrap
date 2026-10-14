using System;

namespace Voidstrap.Exceptions;

internal sealed class InvalidHTTPResponseException(string message) : Exception(message)
{
}
