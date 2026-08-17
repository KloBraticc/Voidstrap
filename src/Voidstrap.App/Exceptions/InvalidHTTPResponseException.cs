using System;

namespace Voidstrap.Exceptions;

internal class InvalidHTTPResponseException(string message) : Exception(message)
{
}
