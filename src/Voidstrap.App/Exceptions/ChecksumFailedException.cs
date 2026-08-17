using System;

namespace Voidstrap.Exceptions;

internal class ChecksumFailedException(string message) : Exception(message)
{
}
