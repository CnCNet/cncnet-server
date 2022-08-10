namespace CnCNetServer;

using System;

internal sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message)
        : base(message)
    {
    }
}