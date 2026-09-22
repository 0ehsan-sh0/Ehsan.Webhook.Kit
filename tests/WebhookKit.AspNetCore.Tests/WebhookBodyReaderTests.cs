// Copyright (c) Ehsan. Licensed under the MIT License.
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using WebhookKit.AspNetCore.Exceptions;
using Xunit;

namespace WebhookKit.AspNetCore.Tests;

public sealed class WebhookBodyReaderTests
{
    private readonly WebhookBodyReader _sut = new();

    [Fact]
    public async Task ReadRawBodyAsync_NullContext_ThrowsArgumentNullException()
    {
        var act = async () => await _sut.ReadRawBodyAsync(null!, 1024);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReadRawBodyAsync_NegativeMaxSizeBytes_ThrowsArgumentOutOfRangeException()
    {
        var context = new DefaultHttpContext();
        var act = async () => await _sut.ReadRawBodyAsync(context, -1);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadRawBodyAsync_ValidBody_ReturnsCorrectBytesAndRewindsStream()
    {
        var context = new DefaultHttpContext();
        byte[] expectedBytes = Encoding.UTF8.GetBytes("{\"event\":\"payment.success\",\"amount\":100}");
        context.Request.Body = new MemoryStream(expectedBytes);
        context.Request.ContentLength = expectedBytes.Length;

        var result = await _sut.ReadRawBodyAsync(context, 1024);

        result.Should().Equal(expectedBytes);
        context.Request.Body.Position.Should().Be(0);

        // Verify downstream reading
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var downstreamContent = await reader.ReadToEndAsync();
        downstreamContent.Should().Be("{\"event\":\"payment.success\",\"amount\":100}");
    }

    [Fact]
    public async Task ReadRawBodyAsync_EmptyBody_ReturnsEmptyArray()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream();
        context.Request.ContentLength = 0;

        var result = await _sut.ReadRawBodyAsync(context, 1024);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadRawBodyAsync_OversizedContentLength_ThrowsWebhookPayloadTooLargeExceptionImmediately()
    {
        var context = new DefaultHttpContext();
        byte[] payload = new byte[2048];
        context.Request.Body = new MemoryStream(payload);
        context.Request.ContentLength = 2048;

        var act = async () => await _sut.ReadRawBodyAsync(context, 1024);

        var ex = await act.Should().ThrowAsync<WebhookPayloadTooLargeException>();
        ex.Which.ActualBytes.Should().Be(2048);
        ex.Which.MaxBytes.Should().Be(1024);
        context.Request.Body.Position.Should().Be(0); // Never even read from stream
    }

    [Fact]
    public async Task ReadRawBodyAsync_OversizedChunkedBody_ThrowsWebhookPayloadTooLargeExceptionDuringRead()
    {
        var context = new DefaultHttpContext();
        byte[] payload = new byte[2048];
        context.Request.Body = new MemoryStream(payload);
        context.Request.ContentLength = null; // simulate chunked encoding where length is not advertised upfront

        var act = async () => await _sut.ReadRawBodyAsync(context, 1024);

        var ex = await act.Should().ThrowAsync<WebhookPayloadTooLargeException>();
        ex.Which.ActualBytes.Should().BeGreaterThan(1024);
        ex.Which.MaxBytes.Should().Be(1024);
    }

    [Fact]
    public async Task ReadRawBodyAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[100]);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _sut.ReadRawBodyAsync(context, 1024, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
