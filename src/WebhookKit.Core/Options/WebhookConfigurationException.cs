// Copyright (c) Ehsan. Licensed under the MIT License.
namespace WebhookKit.Core.Options;

/// <summary>Thrown for programmer configuration errors (null names, duplicates). Never contains secrets.</summary>
public sealed class WebhookConfigurationException : InvalidOperationException
{
    /// <summary>Create with a message.</summary>
    public WebhookConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Create with a message and inner error.</summary>
    public WebhookConfigurationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
