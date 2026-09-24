using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using WebhookKit.Abstractions;
using WebhookKit.AspNetCore.Pipeline;

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

    public WebhookEndpointFilter(IWebhookEndpointService endpointService)
    {
        _endpointService = endpointService ?? throw new ArgumentNullException(nameof(endpointService));
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
            SetSafeConfigurationFailure(context);
            return;
        }

        WebhookEndpointOptions options;
        try
        {
            options = attribute.CreateOptions();
        }
        catch (ArgumentException)
        {
            SetSafeConfigurationFailure(context);
            return;
        }

        var result = await _endpointService.ProcessAsync(
            context.HttpContext,
            options,
            context.HttpContext.RequestAborted);

        if (!ShouldExecute(policy, result.Outcome))
        {
            context.Result = new StatusCodeResult(result.StatusCode);
            return;
        }

        if (!TryValidateContextContract(context, result.Context))
        {
            SetSafeConfigurationFailure(context);
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

    private static void SetSafeConfigurationFailure(ResourceExecutingContext context)
    {
        context.Result = new StatusCodeResult(StatusCodes.Status500InternalServerError);
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
