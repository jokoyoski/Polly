using System.ComponentModel.DataAnnotations;
using Polly.Timeout;

namespace Polly;

/// <summary>
/// Extension methods for adding timeouts to a <see cref="ResilienceStrategyBuilder"/>.
/// </summary>
public static class TimeoutResilienceStrategyBuilderExtensions
{
    /// <summary>
    /// Adds a timeout resilience strategy to the builder.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The builder instance.</param>
    /// <param name="timeout">The timeout value. This value should be greater than <see cref="TimeSpan.Zero"/>.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationException">Thrown when the options produced from the arguments are invalid.</exception>
    public static TBuilder AddTimeout<TBuilder>(this TBuilder builder, TimeSpan timeout)
        where TBuilder : ResilienceStrategyBuilderBase
    {
        Guard.NotNull(builder);

        return builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = timeout
        });
    }

    /// <summary>
    /// Adds a timeout resilience strategy to the builder.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The builder instance.</param>
    /// <param name="options">The timeout options.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ValidationException">Thrown when <paramref name="options"/> are invalid.</exception>
    public static TBuilder AddTimeout<TBuilder>(this TBuilder builder, TimeoutStrategyOptions options)
        where TBuilder : ResilienceStrategyBuilderBase
    {
        Guard.NotNull(builder);
        Guard.NotNull(options);

        builder.AddStrategy(context => new TimeoutResilienceStrategy(options, context.TimeProvider, context.Telemetry), options);
        return builder;
    }
}
