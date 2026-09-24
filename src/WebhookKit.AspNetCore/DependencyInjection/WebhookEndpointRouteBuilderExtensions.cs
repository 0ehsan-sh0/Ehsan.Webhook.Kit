using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using WebhookKit.AspNetCore.Pipeline;

namespace WebhookKit.AspNetCore.DependencyInjection;

public static class WebhookEndpointRouteBuilderExtensions
{
    public static RouteHandlerBuilder MapWebhook(
        this IEndpointRouteBuilder routes,
        string pattern,
        string providerName)
    {
        return routes.MapWebhook(pattern, new WebhookEndpointOptions(providerName));
    }

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
                return Results.StatusCode(result.StatusCode);
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
