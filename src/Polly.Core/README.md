# Introduction

The Polly V8 API exposes unified and non-allocating resilience API that is described in the sections below.

## Core API

At the heart of Polly V8 is the [ResilienceStrategy](ResilienceStrategy.cs) class that is responsible for execution of user code. It's one class that handles all Polly V7 scenarios:

- `ISyncPolicy`
- `IAsyncPolicy`
- `ISyncPolicy<T>`
- `IAsyncPolicy<T>`

``` csharp
public abstract class ResilienceStrategy
{
    // the main method that all the others call
    protected virtual ValueTask<Outcome<TResult>> ExecuteCoreAsync<TResult, TState>(Func<ResilienceContext, TState, ValueTask<Outcome<TResult>>> execution, ResilienceContext context, TState state);

    // convenience methods for various types of user-callbacks
    public void Execute(Action callback);

    public TResult Execute<TResult>(Func<TResult> callback);

    public Task ExecuteAsync(Func<CancellationToken, Task> callback, CancellationToken cancellationToken = default);

    public Task<TResult> ExecuteAsync(Func<CancellationToken, Task<TResult>> callback, CancellationToken cancellationToken = default);

    public ValueTask ExecuteValueTaskAsync(Func<CancellationToken, ValueTask> callback, CancellationToken cancellationToken = default);

    public ValueTask<TResult> ExecuteValueTaskAsync(Func<CancellationToken, ValueTask<TResult>> callback, CancellationToken cancellationToken = default);
    
    // omitted for simplicity
}
```

The [ResilienceContext](ResilienceContext.cs) is defined as:

``` csharp
public sealed class ResilienceContext
{
    public CancellationToken CancellationToken { get; set; }

    public bool IsSynchronous { get; }

    public bool IsVoid { get; }

    public bool ContinueOnCapturedContext { get; }

    public Type ResultType { get; }

    // omitted for simplicity
}
```

The `ResilienceStrategy` unifies the 4 different policies used now in Polly. User actions are executed under a single API. The are many methods
exposed on this class that cover different scenarios:

- Synchronous void methods.
- Synchronous methods with result.
- Asynchronous void methods.
- Asynchronous methods with result.

For example, the synchronous `Execute` method is implemented as:

``` csharp
public void Execute(Action execute)
{
    var context = ResilienceContext.Get();

    context.IsSynchronous = true;
    context.ResultType = typeof(VoidResult);

    try
    {
        strategy.ExecuteCoreAsync(static (context, state) =>
        {
            state();
            return new ValueTask<Outcome<VoidResult>>(new(VoidResult.Instance));
        }, 
        context, 
        execute).GetAwaiter().GetResult();
    }
    finally
    {
        ResilienceContext.Return(context);
    }
}
```

In the preceding example:

- We rent a `ResilienceContext` from the pool.
- We store the information about the execution mode by setting the `IsSynchronous` and `ResultType` properties on the context. Here, we use internal `VoidResult` marker to say this user-callback returns no result.
- We pass the user-callback, and use the `State` to avoid closure allocation.
- We block the execution.
- We return `ResilienceContext` to the pool.

Underlying implementation decides how to execute this user-callback by reading the `ResilienceContext`:

``` csharp
internal class DelayStrategy : ResilienceStrategy
{
    private readonly TimeProvider _timeProvider;

    public DelayStrategy(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    protected override async ValueTask<T> ExecuteCoreAsync<T, TState>(
        Func<ResilienceContext, TState, ValueTask<Outcome<T>>> callback, 
        ResilienceContext context, 
        TState state)
    {
        await _timeProvider.DelayAsync(context).ContinueOnCapturedContext(context.ContinueOnCapturedContext);

        return await callback(context, state).ContinueOnCapturedContext(context.ContinueOnCapturedContext);
    }
}
```

In the preceding example we are calling the `DelayAsync` extension for `TimeProvider` that accepts the `ResilienceContext`. The extension is using `Thread.Sleep` for synchronous executions and `Task.Delay` for asynchronous executions.

This way, the responsibility of how to execute method is lifted from the user and instead passed to the policy. User cares only about the `ResilienceStrategy` class. User uses only a single strategy to execute all scenarios. Previously, user had to decide whether to use sync vs async, typed vs non-typed policies.

The life of extensibility author is also simplified as they only maintain one implementation of strategy instead of multiple ones. See the duplications in [`Polly.Retry`](https://github.com/App-vNext/Polly/tree/main/src/Polly/Retry).

### Generic Resilience Strategy

Polly also exposes the sealed `ResilienceStrategy<T>` strategy that is just a simple wrapper over `ResilienceStrategy`. This strategy is used for scenarios when the consumer handles the single result type.

## Creation of `ResilienceStrategy`

This API exposes the following builders:

- [ResilienceStrategyBuilder](ResilienceStrategyBuilder.cs): Used to create resilience strategies that can execute all types of callbacks. In general, these strategies only handle exceptions. 
- [ResilienceStrategyBuilder<T>](ResilienceStrategyBuilder.TResult.cs): Used to create generic resilience strategies that can only execute callbacks that return the same result type.
- [ResilienceStrategyBuilderBase](ResilienceStrategyBuilderBase.cs): The base class for both builders above. You can use it as a target for strategy extensions that work for both builders above.  

To create a strategy or pipeline of strategies you chain various extensions for `ResilienceStrategyBuilder` followed by the `Build` call:

Single strategy:

``` csharp
var resilienceStrategy = new ResilienceStrategyBuilder().AddRetry().Build();
```

Pipeline of strategies:

``` csharp
var resilienceStrategy = new ResilienceStrategyBuilder()
    .AddRetry()
    .AddCircuitBreaker()
    .AddTimeout(new TimeoutStrategyOptions() { ... })
    .Build();
```

## Extensibility

The resilience extensibility is simple. You just expose extensions for `ResilienceStrategyBuilder` that use the `ResilienceStrategyBuilder.AddStrategy` methods.

If you want to create a resilience strategy that works for both generic and non-generic builders you can use `ResilienceStrategyBuilderBase` as a target:

``` csharp
public static TBuilder AddMyStrategy<TBuilder>(this TBuilder builder)
    where TBuilder : ResilienceStrategyBuilderBase
{
    return builder.AddStrategy(new MyStrategy());
}
```

# Resilience Strategy Delegates

Resilience strategies leverage the following delegate types:

- **Predicates**: These are essential when a resilience strategy needs to determine whether or not to handle the execution result.
- **Events**: These are invoked when significant events occur within the resilience strategy.
- **Generators**: These are used when the resilience strategy requires a certain value from the caller.

## Delegate Signature Guidelines

The suggested signatures for these delegates are as follows:

**Predicates**
- `Func<OutcomeArguments<T, TArgs>, ValueTask<bool>>`: This is the predicate for the generic outcome.
- `Func<OutcomeArguments<object, TArgs>, ValueTask<bool>>`: This is the predicate for the non-generic outcome.

**Events**
- `Func<OutcomeArguments<T, TArgs>, ValueTask>`: This is the event for the generic outcome.
- `Func<OutcomeArguments<object, TArgs>, ValueTask>`: This is the event for the non-generic outcome.
- `Func<Args, ValueTask>`: This is the event utilized by strategies that do not operate with an outcome (for example, Timeout, RateLimiter).

**Generators**
- `Func<OutcomeArguments<T, TArgs>, ValueTask<TValue>>`: This is the generator for the generic outcome.
- `Func<OutcomeArguments<object, TArgs>, ValueTask<TValue>>`: This is the generator for the non-generic outcome.
- `Func<Args, ValueTask<TValue>>`: This is the generator used by strategies that do not operate with an outcome (for example, Timeout, RateLimiter).

It's essential to note that all these delegates are asynchronous and return a `ValueTask`. 

The **`OutcomeArguments<T, TArgs>`** captures the following information that can be used by the delegate:

- `Outcome<T>`: This captures the result of an operation that yields a result of a specific type, `TResult`, or an exception.
- `Context`: The `ResilienceContext` of the operation.
- `Arguments`: Additional arguments associated with the operation. Each resilience strategy can define different arguments for different operations or events.

## Examples

Below are a few examples showcasing the usage of these delegates:

A non-generic predicate defining retries for multiple result types:

``` csharp
new ResilienceStrategyBuilder()
   .AddRetry(new RetryStrategyOptions
    {
        ShouldRetry = args => args switch
        {
            { Exception: InvalidOperationException } => PredicateResult.True,
            { Result: string result } when result == Failure => PredicateResult.True,
            { Result: int result } when result == -1 => PredicateResult.True,
            _ => PredicateResult.False
        },
    })
    .Build();
```

A generic predicate defining retries for a single result type:

``` csharp
new ResilienceStrategyBuilder()
   .AddRetry(new RetryStrategyOptions<string>
    {
        ShouldRetry = args => args switch
        {
            { Exception: InvalidOperationException } => PredicateResult.True,
            { Result: result } when result == Failure => PredicateResult.True,
            _ => PredicateResult.False
        },
    })
    .Build();
```

## Registering Custom Callbacks

When setting the delegates, ensure to respect the `ResilienceContext.IsSynchronous` property's value and execute your delegates synchronously for synchronous executions. In addition, use the `ResilienceContext.ContinueOnCapturedContext` property when your user code uses execution with synchronization context (for example, asynchronous calls in UI applications, such as in Windows Forms or WPF applications).
