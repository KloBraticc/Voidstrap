using System;

namespace Voidstrap.Exceptions;

internal sealed class ChecksumFailedException(string message) : Exception(message)
{
}
