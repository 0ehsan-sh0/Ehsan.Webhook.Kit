using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.Pipeline;
using WebhookKit.AspNetCore.Responses;

namespace WebhookKit.AspNetCore.Mvc;

public sealed class WebhookEndpointFilter : IAsyncResourceFilter
{
    internal static readonly object ContextItemKey = new();
    private const WebhookEndpointActionPolicy SupportedPolicy =
        WebhookEndpointActionPolicy.Processed |
        WebhookEndpointActionPolicy.Accepted |
        WebhookEndpointActionPolicy.Duplicate |
        WebhookEndpointActionPolicy.Ignored;

    private readonly IWebhookEndpointService _endpointService;
    private readonly WebhookResponseWriter _responseWriter;

    public WebhookEndpointFilter(
        IWebhookEndpointService endpointService,
        WebhookResponseWriter? responseWriter = null)
    {
        _endpointService = endpointService ?? throw new ArgumentNullException(nameof(endpointService));
        _responseWriter = responseWriter ?? new WebhookResponseWriter(new DefaultWebhookResponseFormatter());
    }

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext context,
        ResourceExecutionDelegate next)
    {
        var attribute = FindAttribute(context.ActionDescriptor);
        if (attribute is null)
        {
            await next();
            return;
        }

        var policy = attribute.ActionPolicy;
        if ((policy & ~SupportedPolicy) != 0)
        {
            await SetSafeConfigurationFailureAsync(context);
            return;
        }

        WebhookEndpointOptions options;
        try
        {
            options = attribute.CreateOptions();
        }
        catch (ArgumentException)
        {
            await SetSafeConfigurationFailureAsync(context);
            return;
        }

        var result = await _endpointService.ProcessAsync(
            context.HttpContext,
            options,
            context.HttpContext.RequestAborted);

        if (!ShouldExecute(policy, result.Outcome))
        {
            if (!context.HttpContext.Response.HasStarted)
            {
                context.HttpContext.Response.StatusCode = result.StatusCode;
            }

            context.Result = new EmptyResult();
            return;
        }

        if (!TryValidateContextContract(context, result.Context))
        {
            await SetSafeConfigurationFailureAsync(context);
            return;
        }

        context.HttpContext.Items[ContextItemKey] = result.Context;
        await next();
    }

    private static WebhookEndpointAttribute? FindAttribute(ActionDescriptor actionDescriptor)
    {
        return actionDescriptor.EndpointMetadata?.OfType<WebhookEndpointAttribute>().FirstOrDefault();
    }

    private static bool ShouldExecute(
        WebhookEndpointActionPolicy policy,
        WebhookEndpointOutcome outcome)
    {
        return outcome switch
        {
            WebhookEndpointOutcome.Processed => (policy & WebhookEndpointActionPolicy.Processed) != 0,
            WebhookEndpointOutcome.Accepted => (policy & WebhookEndpointActionPolicy.Accepted) != 0,
            WebhookEndpointOutcome.Duplicate => (policy & WebhookEndpointActionPolicy.Duplicate) != 0,
            WebhookEndpointOutcome.Ignored => (policy & WebhookEndpointActionPolicy.Ignored) != 0,
            _ => false
        };
    }

    private static bool TryValidateContextContract(
        ResourceExecutingContext context,
        WebhookContext? webhookContext)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor controllerAction)
        {
            return true;
        }

        var nullabilityContext = new NullabilityInfoContext();
        foreach (var parameter in controllerAction.MethodInfo.GetParameters())
        {
            if (parameter.ParameterType != typeof(WebhookContext))
            {
                continue;
            }

            if (webhookContext is null && nullabilityContext.Create(parameter).WriteState != NullabilityState.Nullable)
            {
                return false;
            }
        }

        return true;
    }

    private async Task SetSafeConfigurationFailureAsync(ResourceExecutingContext context)
    {
        var options = new WebhookEndpointOptions("configuration");
        var result = WebhookEndpointResult.Create(
            context.HttpContext,
            WebhookEndpointOutcome.ConfigurationError,
            StatusCodes.Status500InternalServerError,
            "webhook-configuration-error",
            "Webhook processing is not configured.",
            null,
            options);
        await _responseWriter.WriteAsync(
                context.HttpContext,
                result,
                context.HttpContext.RequestAborted)
            .ConfigureAwait(false);
        context.Result = new EmptyResult();
    }
}

internal sealed class WebhookContextModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        return context.Metadata.ModelType == typeof(WebhookContext)
            ? new WebhookContextModelBinder()
            : null;
    }
}

internal sealed class WebhookContextModelBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        if (bindingContext.HttpContext.Items.TryGetValue(WebhookEndpointFilter.ContextItemKey, out var value))
        {
            bindingContext.Result = ModelBindingResult.Success(value as WebhookContext);
        }

        return Task.CompletedTask;
    }
}
