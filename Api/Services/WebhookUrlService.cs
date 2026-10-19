namespace Api.Services
{
    public class WebhookUrlService
    {
        private readonly LinkGenerator _linkGenerator;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public WebhookUrlService(LinkGenerator linkGenerator, IHttpContextAccessor httpContextAccessor)
        {
            _linkGenerator = linkGenerator;
            _httpContextAccessor = httpContextAccessor;
        }

        /// <summary>
        /// Generate full absolute URL for any webhook action.
        /// </summary>
        /// <param name="controller">Controller name without "Controller" suffix</param>
        /// <param name="action">Action method name</param>
        /// <param name="routeValues">Optional route parameters</param>
        /// <returns>Full URL including scheme, host, path</returns>
        public string GetWebhookUrl(string action, object routeValues = null)
        {
            var context = _httpContextAccessor.HttpContext;
            if (context == null)
            {
                throw new InvalidOperationException("HttpContext is not available.");
            }

            var url = _linkGenerator.GetUriByAction(
                httpContext: context,
                action: action,
                controller: "Webhook",
                values: routeValues
            );

            return url ?? throw new InvalidOperationException($"Could not generate URL for webhook/{action}");
        }
    }
}