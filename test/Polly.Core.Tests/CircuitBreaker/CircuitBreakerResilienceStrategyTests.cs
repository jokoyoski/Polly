using Microsoft.Extensions.Time.Testing;
using Moq;
using Polly.CircuitBreaker;
using Polly.Telemetry;

namespace Polly.Core.Tests.CircuitBreaker;

public class CircuitBreakerResilienceStrategyTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider;
    private readonly Mock<CircuitBehavior> _behavior;
    private readonly ResilienceStrategyTelemetry _telemetry;
    private readonly SimpleCircuitBreakerStrategyOptions<int> _options;
    private readonly CircuitStateController<int> _controller;

    public CircuitBreakerResilienceStrategyTests()
    {
        _timeProvider = new FakeTimeProvider();
        _behavior = new Mock<CircuitBehavior>(MockBehavior.Strict);
        _telemetry = TestUtilities.CreateResilienceTelemetry(Mock.Of<DiagnosticSource>());
        _options = new SimpleCircuitBreakerStrategyOptions<int>();
        _controller = new CircuitStateController<int>(
            CircuitBreakerConstants.DefaultBreakDuration,
            null,
            null,
            null,
            _behavior.Object,
            _timeProvider,
            _telemetry);
    }

    [Fact]
    public void Ctor_Ok()
    {
        this.Invoking(_ => Create()).Should().NotThrow();
    }

    [Fact]
    public void Ctor_StateProvider_EnsureAttached()
    {
        _options.StateProvider = new CircuitBreakerStateProvider();
        Create();

        _options.StateProvider.IsInitialized.Should().BeTrue();

        _options.StateProvider.CircuitState.Should().Be(CircuitState.Closed);
        _options.StateProvider.LastHandledOutcome.Should().Be(null);
    }

    [Fact]
    public async Task Ctor_ManualControl_EnsureAttached()
    {
        _options.ShouldHandle = args => new ValueTask<bool>(args.Exception is InvalidOperationException);
        _options.ManualControl = new CircuitBreakerManualControl();
        var strategy = Create();

        await _options.ManualControl.IsolateAsync(CancellationToken.None);
        strategy.Invoking(s => s.Execute(_ => 0)).Should().Throw<IsolatedCircuitException>();

        _behavior.Setup(v => v.OnCircuitClosed());
        await _options.ManualControl.CloseAsync(CancellationToken.None);

        _behavior.Setup(v => v.OnActionSuccess(CircuitState.Closed));
        strategy.Invoking(s => s.Execute(_ => 0)).Should().NotThrow();

        _options.ManualControl.Dispose();
        strategy.Invoking(s => s.Execute(_ => 0)).Should().Throw<ObjectDisposedException>();

        _behavior.VerifyAll();
    }

    [Fact]
    public void Execute_HandledResult_OnFailureCalled()
    {
        _options.ShouldHandle = args => new ValueTask<bool>(args.Result is -1);
        var strategy = Create();
        var shouldBreak = false;

        _behavior.Setup(v => v.OnActionFailure(CircuitState.Closed, out shouldBreak));
        strategy.Execute(_ => -1).Should().Be(-1);

        _behavior.VerifyAll();
    }

    [Fact]
    public void Execute_UnhandledResult_OnActionSuccess()
    {
        _options.ShouldHandle = args => new ValueTask<bool>(args.Result is -1);
        var strategy = Create();

        _behavior.Setup(v => v.OnActionSuccess(CircuitState.Closed));
        strategy.Execute(_ => 0).Should().Be(0);

        _behavior.VerifyAll();
    }

    [Fact]
    public void Execute_HandledException_OnFailureCalled()
    {
        _options.ShouldHandle = args => new ValueTask<bool>(args.Exception is InvalidOperationException);
        var strategy = Create();
        var shouldBreak = false;

        _behavior.Setup(v => v.OnActionFailure(CircuitState.Closed, out shouldBreak));

        strategy.Invoking(s => s.Execute<int>(_ => throw new InvalidOperationException())).Should().Throw<InvalidOperationException>();

        _behavior.VerifyAll();
    }

    [Fact]
    public void Execute_UnhandledException_NoCalls()
    {
        _options.ShouldHandle = args => new ValueTask<bool>(args.Exception is InvalidOperationException);
        var strategy = Create();

        strategy.Invoking(s => s.Execute(_ => throw new ArgumentException())).Should().Throw<ArgumentException>();

        _behavior.VerifyNoOtherCalls();
    }

    public void Dispose() => _controller.Dispose();

    [Fact]
    public void Execute_Ok()
    {
        _options.ShouldHandle = _ => PredicateResult.False;
        _behavior.Setup(v => v.OnActionSuccess(CircuitState.Closed));

        Create().Invoking(s => s.Execute(_ => { })).Should().NotThrow();
    }

    private CircuitBreakerResilienceStrategy<int> Create()
    {
        return new(_options.ShouldHandle!, _controller, _options.StateProvider, _options.ManualControl, true);
    }
}
