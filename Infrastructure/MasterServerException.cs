namespace CnCNetServer;

using System;

internal sealed class MasterServerException : Exception
{
    public MasterServerException(string message)
        : base(message)
    {
    }
}