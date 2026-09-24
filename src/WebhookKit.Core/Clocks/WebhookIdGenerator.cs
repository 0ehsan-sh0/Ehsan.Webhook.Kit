using System.Security.Cryptography;
using WebhookKit.Abstractions;

namespace WebhookKit.Core.Clocks;

/// <summary>Generates cryptographically random, time-ordered ULID identifiers.</summary>
internal sealed class WebhookIdGenerator : IWebhookIdGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private readonly IWebhookClock _clock;

    /// <summary>Creates a generator using the supplied clock for its timestamp component.</summary>
    /// <param name="clock">The clock used for the time component.</param>
    public WebhookIdGenerator(IWebhookClock clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Creates a new identifier for one HTTP transmission.</summary>
    /// <returns>A canonical 26-character ULID string.</returns>
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
