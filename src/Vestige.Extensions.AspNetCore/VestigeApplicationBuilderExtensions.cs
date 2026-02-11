using Microsoft.AspNetCore.Builder;

namespace Vestige.Extensions.AspNetCore;

/// <summary>Extension methods for adding the Vestige middleware to the request pipeline.</summary>
public static class VestigeApplicationBuilderExtensions
{
    /// <summary>
    /// Add the <see cref="WideEventMiddleware"/> to the pipeline.
    /// Place this early — typically right after exception handling middleware.
    /// </summary>
    public static IApplicationBuilder UseVestige(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<WideEventMiddleware>();
    }
}
