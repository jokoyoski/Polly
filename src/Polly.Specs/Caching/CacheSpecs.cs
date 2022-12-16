﻿using System;
using System.Threading;
using FluentAssertions;
using Polly.Caching;
using Polly.Specs.Helpers;
using Polly.Specs.Helpers.Caching;
using Polly.Utilities;
using Polly.Wrap;
using Xunit;

namespace Polly.Specs.Caching
{
    [Collection(Constants.SystemClockDependentTestCollection)]
    public class CacheSpecs : IDisposable
    {
        #region Configuration

        [Fact]
        public void Should_throw_when_cache_provider_is_null()
        {
            ISyncCacheProvider cacheProvider = null;
            Action action = () => Policy.Cache(cacheProvider, TimeSpan.MaxValue);
            action.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("cacheProvider");
        }

        [Fact]
        public void Should_throw_when_ttl_strategy_is_null()
        {
            ISyncCacheProvider cacheProvider = new StubCacheProvider();
            ITtlStrategy ttlStrategy = null;
            Action action = () => Policy.Cache(cacheProvider, ttlStrategy);
            action.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("ttlStrategy");
        }

        [Fact]
        public void Should_throw_when_cache_key_strategy_is_null()
        {
            ISyncCacheProvider cacheProvider = new StubCacheProvider();
            Func<Context, string> cacheKeyStrategy = null;
            Action action = () => Policy.Cache(cacheProvider, TimeSpan.MaxValue, cacheKeyStrategy);
            action.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("cacheKeyStrategy");
        }

        #endregion

        #region Caching behaviours

        [Fact]
        public void Should_return_value_from_cache_and_not_execute_delegate_if_cache_holds_value()
        {
            const string valueToReturnFromCache = "valueToReturnFromCache";
            const string valueToReturnFromExecution = "valueToReturnFromExecution";
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue);
            stubCacheProvider.Put(operationKey, valueToReturnFromCache, new Ttl(TimeSpan.MaxValue));

            var delegateExecuted = false;

            cache.Execute(_ =>
            {
                delegateExecuted = true;
                return valueToReturnFromExecution;
            }, new Context(operationKey))
                .Should().Be(valueToReturnFromCache);

            delegateExecuted.Should().BeFalse();
        }

        [Fact]
        public void Should_execute_delegate_and_put_value_in_cache_if_cache_does_not_hold_value()
        {
            const string valueToReturn = "valueToReturn";
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue);

            (var cacheHit1, var fromCache1) = stubCacheProvider.TryGet(operationKey);
            cacheHit1.Should().BeFalse();
            fromCache1.Should().BeNull();

            cache.Execute(_ => valueToReturn, new Context(operationKey)).Should().Be(valueToReturn);

            (var cacheHit2, var fromCache2) = stubCacheProvider.TryGet(operationKey);
            cacheHit2.Should().BeTrue();
            fromCache2.Should().Be(valueToReturn);
        }

        [Fact]
        public void Should_execute_delegate_and_put_value_in_cache_but_when_it_expires_execute_delegate_again()
        {
            const string valueToReturn = "valueToReturn";
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var ttl = TimeSpan.FromMinutes(30);
            var cache = Policy.Cache(stubCacheProvider, ttl);

            (var cacheHit1, var fromCache1) = stubCacheProvider.TryGet(operationKey);
            cacheHit1.Should().BeFalse();
            fromCache1.Should().BeNull();

            var delegateInvocations = 0;
            Func<Context, string> func = _ =>
            {
                delegateInvocations++;
                return valueToReturn;
            };

            var fixedTime = SystemClock.DateTimeOffsetUtcNow();
            SystemClock.DateTimeOffsetUtcNow = () => fixedTime;

            // First execution should execute delegate and put result in the cache.
            cache.Execute(func, new Context(operationKey)).Should().Be(valueToReturn);
            delegateInvocations.Should().Be(1);

            (var cacheHit2, var fromCache2) = stubCacheProvider.TryGet(operationKey);
            cacheHit2.Should().BeTrue();
            fromCache2.Should().Be(valueToReturn);

            // Second execution (before cache expires) should get it from the cache - no further delegate execution.
            // (Manipulate time so just prior cache expiry).
            SystemClock.DateTimeOffsetUtcNow = () => fixedTime.Add(ttl).AddSeconds(-1);
            cache.Execute(func, new Context(operationKey)).Should().Be(valueToReturn);
            delegateInvocations.Should().Be(1);

            // Manipulate time to force cache expiry.
            SystemClock.DateTimeOffsetUtcNow = () => fixedTime.Add(ttl).AddSeconds(1);

            // Third execution (cache expired) should not get it from the cache - should cause further delegate execution.
            cache.Execute(func, new Context(operationKey)).Should().Be(valueToReturn);
            delegateInvocations.Should().Be(2);
        }

        [Fact]
        public void Should_execute_delegate_but_not_put_value_in_cache_if_cache_does_not_hold_value_but_ttl_indicates_not_worth_caching()
        {
            const string valueToReturn = "valueToReturn";
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.Zero);

            (var cacheHit1, var fromCache1) = stubCacheProvider.TryGet(operationKey);
            cacheHit1.Should().BeFalse();
            fromCache1.Should().BeNull();

            cache.Execute(_ => valueToReturn, new Context(operationKey)).Should().Be(valueToReturn);

            (var cacheHit2, var fromCache2) = stubCacheProvider.TryGet(operationKey);
            cacheHit2.Should().BeFalse();
            fromCache2.Should().BeNull();
        }

        [Fact]
        public void Should_return_value_from_cache_and_not_execute_delegate_if_prior_execution_has_cached()
        {
            const string valueToReturn = "valueToReturn";
            const string operationKey = "SomeOperationKey";

            var cache = Policy.Cache(new StubCacheProvider(), TimeSpan.MaxValue);

            var delegateInvocations = 0;
            Func<Context, string> func = _ =>
            {
                delegateInvocations++;
                return valueToReturn;
            };

            cache.Execute(func, new Context(operationKey)).Should().Be(valueToReturn);
            delegateInvocations.Should().Be(1);

            cache.Execute(func, new Context(operationKey)).Should().Be(valueToReturn);
            delegateInvocations.Should().Be(1);

            cache.Execute(func, new Context(operationKey)).Should().Be(valueToReturn);
            delegateInvocations.Should().Be(1);
        }

        [Fact]
        public void Should_allow_custom_FuncCacheKeyStrategy()
        {
            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue, context => context.OperationKey + context["id"]);

            var person1 = new object();
            stubCacheProvider.Put("person1", person1, new Ttl(TimeSpan.MaxValue));
            var person2 = new object();
            stubCacheProvider.Put("person2", person2, new Ttl(TimeSpan.MaxValue));

            var funcExecuted = false;
            Func<Context, object> func = _ => { funcExecuted = true; return new object(); };

            cache.Execute(func, new Context("person", new { id = "1" }.AsDictionary())).Should().BeSameAs(person1);
            funcExecuted.Should().BeFalse();

            cache.Execute(func, new Context("person", new { id = "2" }.AsDictionary())).Should().BeSameAs(person2);
            funcExecuted.Should().BeFalse();
        }

        [Fact]
        public void Should_allow_custom_ICacheKeyStrategy()
        {
            Action<Context, string, Exception> noErrorHandling = (_, _, _) => { };
            Action<Context, string> emptyDelegate = (_, _) => { };

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            ICacheKeyStrategy cacheKeyStrategy = new StubCacheKeyStrategy(context => context.OperationKey + context["id"]);
            var cache = Policy.Cache(stubCacheProvider, new RelativeTtl(TimeSpan.MaxValue), cacheKeyStrategy, emptyDelegate, emptyDelegate, emptyDelegate, noErrorHandling, noErrorHandling);

            var person1 = new object();
            stubCacheProvider.Put("person1", person1, new Ttl(TimeSpan.MaxValue));
            var person2 = new object();
            stubCacheProvider.Put("person2", person2, new Ttl(TimeSpan.MaxValue));

            var funcExecuted = false;
            Func<Context, object> func = _ => { funcExecuted = true; return new object(); };

            cache.Execute(func, new Context("person", new { id = "1" }.AsDictionary())).Should().BeSameAs(person1);
            funcExecuted.Should().BeFalse();

            cache.Execute(func, new Context("person", new { id = "2" }.AsDictionary())).Should().BeSameAs(person2);
            funcExecuted.Should().BeFalse();
        }

        #endregion

        #region Caching behaviours, default(TResult)

        [Fact]
        public void Should_execute_delegate_and_put_value_in_cache_if_cache_does_not_hold_value__default_for_reference_type()
        {
            ResultClass valueToReturn = default;
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue);

            (var cacheHit1, var fromCache1) = stubCacheProvider.TryGet(operationKey);
            cacheHit1.Should().BeFalse();
            fromCache1.Should().BeNull();

            cache.Execute(_ => valueToReturn, new Context(operationKey)).Should().Be(valueToReturn);

            (var cacheHit2, var fromCache2) = stubCacheProvider.TryGet(operationKey);
            cacheHit2.Should().BeTrue();
            fromCache2.Should().Be(valueToReturn);
        }

        [Fact]
        public void Should_return_value_from_cache_and_not_execute_delegate_if_cache_holds_value__default_for_reference_type()
        {
            ResultClass valueToReturnFromCache = default;
            var valueToReturnFromExecution = new ResultClass(ResultPrimitive.Good);
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue);
            stubCacheProvider.Put(operationKey, valueToReturnFromCache, new Ttl(TimeSpan.MaxValue));

            var delegateExecuted = false;

            cache.Execute(_ =>
                {
                    delegateExecuted = true;
                    return valueToReturnFromExecution;
                }, new Context(operationKey))
                .Should().Be(valueToReturnFromCache);

            delegateExecuted.Should().BeFalse();
        }

        [Fact]
        public void Should_execute_delegate_and_put_value_in_cache_if_cache_does_not_hold_value__default_for_value_type()
        {
            ResultPrimitive valueToReturn = default;
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue);

            (var cacheHit1, var fromCache1) = stubCacheProvider.TryGet(operationKey);
            cacheHit1.Should().BeFalse();
            fromCache1.Should().BeNull();

            cache.Execute(_ => valueToReturn, new Context(operationKey)).Should().Be(valueToReturn);

            (var cacheHit2, var fromCache2) = stubCacheProvider.TryGet(operationKey);
            cacheHit2.Should().BeTrue();
            fromCache2.Should().Be(valueToReturn);
        }

        [Fact]
        public void Should_return_value_from_cache_and_not_execute_delegate_if_cache_holds_value__default_for_value_type()
        {
            ResultPrimitive valueToReturnFromCache = default; 
            var valueToReturnFromExecution = ResultPrimitive.Good;
            valueToReturnFromExecution.Should().NotBe(valueToReturnFromCache);
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue);
            stubCacheProvider.Put(operationKey, valueToReturnFromCache, new Ttl(TimeSpan.MaxValue));

            var delegateExecuted = false;

            cache.Execute(_ =>
                {
                    delegateExecuted = true;
                    return valueToReturnFromExecution;
                }, new Context(operationKey))
                .Should().Be(valueToReturnFromCache);

            delegateExecuted.Should().BeFalse();
        }

        #endregion

        #region Non-generic CachePolicy in non-generic PolicyWrap

        [Fact]
        public void Should_return_value_from_cache_and_not_execute_delegate_if_cache_holds_value_when_outermost_in_policywrap()
        {
            const string valueToReturnFromCache = "valueToReturnFromCache";
            const string valueToReturnFromExecution = "valueToReturnFromExecution";
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue);
            Policy noop = Policy.NoOp();
            var wrap = Policy.Wrap(cache, noop);

            stubCacheProvider.Put(operationKey, valueToReturnFromCache, new Ttl(TimeSpan.MaxValue));

            var delegateExecuted = false;

            wrap.Execute(_ =>
            {
                delegateExecuted = true;
                return valueToReturnFromExecution;
            }, new Context(operationKey))
                .Should().Be(valueToReturnFromCache);

            delegateExecuted.Should().BeFalse();
        }

        [Fact]
        public void Should_return_value_from_cache_and_not_execute_delegate_if_cache_holds_value_when_innermost_in_policywrap()
        {
            const string valueToReturnFromCache = "valueToReturnFromCache";
            const string valueToReturnFromExecution = "valueToReturnFromExecution";
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue);
            Policy noop = Policy.NoOp();
            var wrap = Policy.Wrap(noop, cache);

            stubCacheProvider.Put(operationKey, valueToReturnFromCache, new Ttl(TimeSpan.MaxValue));

            var delegateExecuted = false;

            wrap.Execute(_ =>
            {
                delegateExecuted = true;
                return valueToReturnFromExecution;
            }, new Context(operationKey))
                .Should().Be(valueToReturnFromCache);

            delegateExecuted.Should().BeFalse();
        }

        [Fact]
        public void Should_return_value_from_cache_and_not_execute_delegate_if_cache_holds_value_when_mid_policywrap()
        {
            const string valueToReturnFromCache = "valueToReturnFromCache";
            const string valueToReturnFromExecution = "valueToReturnFromExecution";
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue);
            Policy noop = Policy.NoOp();
            var wrap = Policy.Wrap(noop, cache, noop);

            stubCacheProvider.Put(operationKey, valueToReturnFromCache, new Ttl(TimeSpan.MaxValue));

            var delegateExecuted = false;

            wrap.Execute(_ =>
            {
                delegateExecuted = true;
                return valueToReturnFromExecution;
            }, new Context(operationKey))
                .Should().Be(valueToReturnFromCache);

            delegateExecuted.Should().BeFalse();
        }

        #endregion

        #region No-op pass-through behaviour

        [Fact]
        public void Should_always_execute_delegate_if_execution_key_not_set()
        {
            var valueToReturn = Guid.NewGuid().ToString();

            var cache = Policy.Cache(new StubCacheProvider(), TimeSpan.MaxValue);

            var delegateInvocations = 0;
            var func = () =>
            {
                delegateInvocations++;
                return valueToReturn;
            };

            cache.Execute(func /*, no operation key */).Should().Be(valueToReturn);
            delegateInvocations.Should().Be(1);

            cache.Execute(func /*, no operation key */).Should().Be(valueToReturn);
            delegateInvocations.Should().Be(2);
        }

        [Fact]
        public void Should_always_execute_delegate_if_execution_is_void_returning()
        {
            var operationKey = "SomeKey";

            var cache = Policy.Cache(new StubCacheProvider(), TimeSpan.MaxValue);

            var delegateInvocations = 0;
            Action<Context> action = _ => { delegateInvocations++; };

            cache.Execute(action, new Context(operationKey));
            delegateInvocations.Should().Be(1);

            cache.Execute(action, new Context(operationKey));
            delegateInvocations.Should().Be(2);
        }

        #endregion

        #region Cancellation

        [Fact]
        public void Should_honour_cancellation_even_if_prior_execution_has_cached()
        {
            const string valueToReturn = "valueToReturn";
            const string operationKey = "SomeOperationKey";

            var cache = Policy.Cache(new StubCacheProvider(), TimeSpan.MaxValue);

            var tokenSource = new CancellationTokenSource();

            var delegateInvocations = 0;
            Func<Context, CancellationToken, string> func = (_, _) =>
            {
                // delegate does not observe cancellation token; test is whether CacheEngine does.
                delegateInvocations++;
                return valueToReturn;
            };

            cache.Execute(func, new Context(operationKey), tokenSource.Token).Should().Be(valueToReturn);
            delegateInvocations.Should().Be(1);

            tokenSource.Cancel();

            cache.Invoking(policy => policy.Execute(func, new Context(operationKey), tokenSource.Token))
                .Should().Throw<OperationCanceledException>();
            delegateInvocations.Should().Be(1);
        }

        [Fact]
        public void Should_honour_cancellation_during_delegate_execution_and_not_put_to_cache()
        {
            const string valueToReturn = "valueToReturn";
            const string operationKey = "SomeOperationKey";

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue);

            var tokenSource = new CancellationTokenSource();

            Func<Context, CancellationToken, string> func = (_, ct) =>
            {
                tokenSource.Cancel(); // simulate cancellation raised during delegate execution
                ct.ThrowIfCancellationRequested();
                return valueToReturn;
            };

            cache.Invoking(policy => policy.Execute(func, new Context(operationKey), tokenSource.Token))
                .Should().Throw<OperationCanceledException>();

            (var cacheHit, var fromCache) = stubCacheProvider.TryGet(operationKey);
            cacheHit.Should().BeFalse();
            fromCache.Should().BeNull();
        }

        #endregion

        #region Policy hooks

        [Fact]
        public void Should_call_onError_delegate_if_cache_get_errors()
        {
            var ex = new Exception();
            ISyncCacheProvider stubCacheProvider = new StubErroringCacheProvider(getException: ex, putException: null);

            Exception exceptionFromCacheProvider = null;

            const string valueToReturnFromCache = "valueToReturnFromCache";
            const string valueToReturnFromExecution = "valueToReturnFromExecution";
            const string operationKey = "SomeOperationKey";

            Action<Context, string, Exception> onError = (_, _, exc) => { exceptionFromCacheProvider = exc; };

            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue, onError);

            stubCacheProvider.Put(operationKey, valueToReturnFromCache, new Ttl(TimeSpan.MaxValue));

            var delegateExecuted = false;


            // Even though value is in cache, get will error; so value is returned from execution.
            cache.Execute(_ =>
                {
                    delegateExecuted = true;
                    return valueToReturnFromExecution;
                }, new Context(operationKey))
                .Should().Be(valueToReturnFromExecution);
            delegateExecuted.Should().BeTrue();

            // And error should be captured by onError delegate.
            exceptionFromCacheProvider.Should().Be(ex);
        }

        [Fact]
        public void Should_call_onError_delegate_if_cache_put_errors()
        {
            var ex = new Exception();
            ISyncCacheProvider stubCacheProvider = new StubErroringCacheProvider(getException: null, putException: ex);

            Exception exceptionFromCacheProvider = null;

            const string valueToReturn = "valueToReturn";
            const string operationKey = "SomeOperationKey";

            Action<Context, string, Exception> onError = (_, _, exc) => { exceptionFromCacheProvider = exc; };

            var cache = Policy.Cache(stubCacheProvider, TimeSpan.MaxValue, onError);

            (var cacheHit1, var fromCache1) = stubCacheProvider.TryGet(operationKey);
            cacheHit1.Should().BeFalse();
            fromCache1.Should().BeNull();

            cache.Execute(_ => valueToReturn, new Context(operationKey)).Should().Be(valueToReturn);

            //  error should be captured by onError delegate.
            exceptionFromCacheProvider.Should().Be(ex);

            // failed to put it in the cache
            (var cacheHit2, var fromCache2) = stubCacheProvider.TryGet(operationKey);
            cacheHit2.Should().BeFalse();
            fromCache2.Should().BeNull();

        }

        [Fact]
        public void Should_execute_oncacheget_after_got_from_cache()
        {
            const string valueToReturnFromCache = "valueToReturnFromCache";
            const string valueToReturnFromExecution = "valueToReturnFromExecution";

            const string operationKey = "SomeOperationKey";
            string keyPassedToDelegate = null;

            var contextToExecute = new Context(operationKey);
            Context contextPassedToDelegate = null;

            Action<Context, string, Exception> noErrorHandling = (_, _, _) => { };
            Action<Context, string> emptyDelegate = (_, _) => { };
            Action<Context, string> onCacheAction = (ctx, key) => { contextPassedToDelegate = ctx; keyPassedToDelegate = key; };

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, new RelativeTtl(TimeSpan.MaxValue), DefaultCacheKeyStrategy.Instance, onCacheAction, emptyDelegate, emptyDelegate, noErrorHandling, noErrorHandling);
            stubCacheProvider.Put(operationKey, valueToReturnFromCache, new Ttl(TimeSpan.MaxValue));

            var delegateExecuted = false;
            cache.Execute(_ =>
                {
                    delegateExecuted = true;
                    return valueToReturnFromExecution;
                }, contextToExecute)
                .Should().Be(valueToReturnFromCache);
            delegateExecuted.Should().BeFalse();

            contextPassedToDelegate.Should().BeSameAs(contextToExecute);
            keyPassedToDelegate.Should().Be(operationKey);
        }

        [Fact]
        public void Should_execute_oncachemiss_and_oncacheput_if_cache_does_not_hold_value_and_put()
        {
            const string valueToReturn = "valueToReturn";

            const string operationKey = "SomeOperationKey";
            string keyPassedToOnCacheMiss = null;
            string keyPassedToOnCachePut = null;

            var contextToExecute = new Context(operationKey);
            Context contextPassedToOnCacheMiss = null;
            Context contextPassedToOnCachePut = null;

            Action<Context, string, Exception> noErrorHandling = (_, _, _) => { };
            Action<Context, string> emptyDelegate = (_, _) => { };
            Action<Context, string> onCacheMiss = (ctx, key) => { contextPassedToOnCacheMiss = ctx; keyPassedToOnCacheMiss = key; };
            Action<Context, string> onCachePut = (ctx, key) => { contextPassedToOnCachePut = ctx; keyPassedToOnCachePut = key; };

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, new RelativeTtl(TimeSpan.MaxValue), DefaultCacheKeyStrategy.Instance, emptyDelegate, onCacheMiss, onCachePut, noErrorHandling, noErrorHandling);

            (var cacheHit1, var fromCache1) = stubCacheProvider.TryGet(operationKey);
            cacheHit1.Should().BeFalse();
            fromCache1.Should().BeNull();

            cache.Execute(_ => valueToReturn, contextToExecute).Should().Be(valueToReturn);

            (var cacheHit2, var fromCache2) = stubCacheProvider.TryGet(operationKey);
            cacheHit2.Should().BeTrue();
            fromCache2.Should().Be(valueToReturn);

            contextPassedToOnCacheMiss.Should().BeSameAs(contextToExecute);
            keyPassedToOnCacheMiss.Should().Be(operationKey);

            contextPassedToOnCachePut.Should().BeSameAs(contextToExecute);
            keyPassedToOnCachePut.Should().Be(operationKey);
        }

        [Fact]
        public void Should_execute_oncachemiss_but_not_oncacheput_if_cache_does_not_hold_value_and_returned_value_not_worth_caching()
        {
            const string valueToReturn = "valueToReturn";

            const string operationKey = "SomeOperationKey";
            string keyPassedToOnCacheMiss = null;
            string keyPassedToOnCachePut = null;

            var contextToExecute = new Context(operationKey);
            Context contextPassedToOnCacheMiss = null;
            Context contextPassedToOnCachePut = null;

            Action<Context, string, Exception> noErrorHandling = (_, _, _) => { };
            Action<Context, string> emptyDelegate = (_, _) => { };
            Action<Context, string> onCacheMiss = (ctx, key) => { contextPassedToOnCacheMiss = ctx; keyPassedToOnCacheMiss = key; };
            Action<Context, string> onCachePut = (ctx, key) => { contextPassedToOnCachePut = ctx; keyPassedToOnCachePut = key; };

            ISyncCacheProvider stubCacheProvider = new StubCacheProvider();
            var cache = Policy.Cache(stubCacheProvider, new RelativeTtl(TimeSpan.Zero), DefaultCacheKeyStrategy.Instance, emptyDelegate, onCacheMiss, onCachePut, noErrorHandling, noErrorHandling);

            (var cacheHit, var fromCache) = stubCacheProvider.TryGet(operationKey);
            cacheHit.Should().BeFalse();
            fromCache.Should().BeNull();

            cache.Execute(_ => valueToReturn, contextToExecute).Should().Be(valueToReturn);

            contextPassedToOnCacheMiss.Should().BeSameAs(contextToExecute);
            keyPassedToOnCacheMiss.Should().Be(operationKey);

            contextPassedToOnCachePut.Should().BeNull();
            keyPassedToOnCachePut.Should().BeNull();
        }

        [Fact]
        public void Should_not_execute_oncachemiss_if_dont_query_cache_because_cache_key_not_set()
        {
            var valueToReturn = Guid.NewGuid().ToString();

            Action<Context, string, Exception> noErrorHandling = (_, _, _) => { };
            Action<Context, string> emptyDelegate = (_, _) => { };

            var onCacheMissExecuted = false;
            Action<Context, string> onCacheMiss = (_, _) => { onCacheMissExecuted = true; };

            var cache = Policy.Cache(new StubCacheProvider(), new RelativeTtl(TimeSpan.MaxValue), DefaultCacheKeyStrategy.Instance, emptyDelegate, onCacheMiss, emptyDelegate, noErrorHandling, noErrorHandling);

            cache.Execute(() => valueToReturn /*, no operation key */).Should().Be(valueToReturn);

            onCacheMissExecuted.Should().BeFalse();
        }

        #endregion

        public void Dispose()
        {
            SystemClock.Reset();
        }
    }
}
