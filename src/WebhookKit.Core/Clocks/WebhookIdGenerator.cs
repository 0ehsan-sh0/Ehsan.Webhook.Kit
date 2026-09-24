using System.Security.Cryptography;
using WebhookKit.Abstractions;

namespace WebhookKit.Core.Clocks;

public sealed class WebhookIdGenerator : IWebhookIdGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private readonly IWebhookClock _clock;

    public WebhookIdGenerator(IWebhookClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string Create()
    {
        var unixMilliseconds = _clock.UtcNow.ToUnixTimeMilliseconds();
        if (unixMilliseconds < 0)
        {
            unixMilliseconds = 0;
        }

        var timestamp = (ulong)unixMilliseconds;
        Span<byte> bytes = stackalloc byte[16];
        for (var index = 0; index < 6; index++)
        {
            bytes[5 - index] = (byte)(timestamp >> (index * 8));
        }

        RandomNumberGenerator.Fill(bytes[6..]);

        Span<char> encoded = stackalloc char[26];
        var outputIndex = 0;
        var buffer = 0;
        var bufferBits = 2;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bufferBits += 8;
            while (bufferBits >= 5)
            {
                bufferBits -= 5;
                encoded[outputIndex++] = Alphabet[(buffer >> bufferBits) & 31];
                buffer &= (1 << bufferBits) - 1;
            }
        }

        return new string(encoded);
    }
}
