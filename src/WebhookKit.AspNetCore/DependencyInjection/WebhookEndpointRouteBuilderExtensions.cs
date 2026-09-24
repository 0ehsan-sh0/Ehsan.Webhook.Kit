using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WebhookKit.AspNetCore.Pipeline;

namespace WebhookKit.AspNetCore.DependencyInjection;

/// <summary>Maps WebhookKit minimal API endpoints.</summary>
public static class WebhookEndpointRouteBuilderExtensions
{
    /// <summary>Maps a POST endpoint for a provider using synchronous defaults.</summary>
    /// <param name="routes">The endpoint route builder.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="providerName">Configured provider name.</param>
    /// <returns>The mapped route handler builder.</returns>
    public static RouteHandlerBuilder MapWebhook(
        this IEndpointRouteBuilder routes,
        string pattern,
        string providerName)
    {
        return routes.MapWebhook(pattern, new WebhookEndpointOptions(providerName));
    }

    /// <summary>Maps a POST endpoint using a complete options snapshot.</summary>
    /// <param name="routes">The endpoint route builder.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="options">Endpoint options; they are validated and snapshotted during mapping.</param>
    /// <returns>The mapped route handler builder.</returns>
    public static RouteHandlerBuilder MapWebhook(
        this IEndpointRouteBuilder routes,
        string pattern,
        WebhookEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var snapshot = options.Snapshot();
        var builder = routes.MapPost(
            pattern,
            async (HttpContext context, IWebhookEndpointService service, CancellationToken cancellationToken) =>
            {
                var result = await service.ProcessAsync(context, snapshot, cancellationToken).ConfigureAwait(false);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = result.StatusCode;
                }
            });

        builder.WithMetadata(new WebhookEndpointMetadata(snapshot));
        builder.Produces(StatusCodes.Status200OK);
        builder.Produces(StatusCodes.Status202Accepted);
        builder.Produces(StatusCodes.Status400BadRequest);
        builder.Produces(StatusCodes.Status401Unauthorized);
        builder.Produces(StatusCodes.Status413PayloadTooLarge);
        builder.Produces(StatusCodes.Status500InternalServerError);
        builder.Produces(StatusCodes.Status503ServiceUnavailable);

        if (!snapshot.IncludeInSchema)
        {
            builder.ExcludeFromDescription();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.OperationId))
        {
            builder.WithName(snapshot.OperationId);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Summary))
        {
            builder.WithSummary(snapshot.Summary);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Description))
        {
            builder.WithDescription(snapshot.Description);
        }

        if (snapshot.Tags.Count > 0)
        {
            builder.WithTags([.. snapshot.Tags]);
        }

        return builder;
    }

    /// <summary>Maps a POST endpoint using a provider name and response/metadata options.</summary>
    /// <param name="routes">The endpoint route builder.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="providerName">Configured provider name.</param>
    /// <param name="options">Endpoint options whose provider value is replaced by <paramref name="providerName"/>.</param>
    /// <returns>The mapped route handler builder.</returns>
    public static RouteHandlerBuilder MapWebhook(
        this IEndpointRouteBuilder routes,
        string pattern,
        string providerName,
        WebhookEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = new WebhookEndpointOptions(providerName)
        {
            Mode = options.Mode,
            IncludeInSchema = options.IncludeInSchema,
            OperationId = options.OperationId,
            Summary = options.Summary,
            Description = options.Description,
            Tags = options.Tags,
            Response = options.Response
        }.Snapshot();

        return routes.MapWebhook(pattern, snapshot);
    }
}
