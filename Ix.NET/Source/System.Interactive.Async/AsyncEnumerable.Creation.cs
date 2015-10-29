﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
#if SPREADS
using Spreads;
#endif

namespace System.Linq
{
    public static partial class AsyncEnumerable
    {
        static IAsyncEnumerable<T> Create<T>(Func<IAsyncEnumerator<T>> getEnumerator)
        {
            return new AnonymousAsyncEnumerable<T>(getEnumerator);
        }

        class AnonymousAsyncEnumerable<T> : IAsyncEnumerable<T>
        {
            Func<IAsyncEnumerator<T>> getEnumerator;

            public AnonymousAsyncEnumerable(Func<IAsyncEnumerator<T>> getEnumerator)
            {
                this.getEnumerator = getEnumerator;
            }

            public IAsyncEnumerator<T> GetEnumerator()
            {
                return getEnumerator();
            }

#if SPREADS
            IEnumerator IEnumerable.GetEnumerator() {
                return getEnumerator();
            }

            IEnumerator<T> IEnumerable<T>.GetEnumerator() {
                return getEnumerator();
            }

            IAsyncEnumerator<T> IAsyncEnumerable<T>.GetEnumerator() {
                return getEnumerator();
            }
#endif
        }

        static IAsyncEnumerator<T> Create<T>(Func<CancellationToken, Task<bool>> moveNext, Func<T> current, Action dispose)
        {
            return new AnonymousAsyncEnumerator<T>(moveNext, current, dispose);
        }

        static IAsyncEnumerator<T> Create<T>(Func<CancellationToken, TaskCompletionSource<bool>, Task<bool>> moveNext, Func<T> current, Action dispose)
        {
            var self = default(IAsyncEnumerator<T>);
            self = new AnonymousAsyncEnumerator<T>(
                ct =>
                {
                    var tcs = new TaskCompletionSource<bool>();

                    var stop = new Action(() =>
                    {
                        self.Dispose();
                        tcs.TrySetCanceled();
                    });

                    var ctr = ct.Register(stop);

                    var res = moveNext(ct, tcs).Finally(ctr.Dispose);
                    return res;
                },
                current,
                dispose
            );
            return self;
        }

        class AnonymousAsyncEnumerator<T> : IAsyncEnumerator<T>
        {
            private readonly Func<CancellationToken, Task<bool>> _moveNext;
            private readonly Func<T> _current;
            private readonly Action _dispose;
            
            private bool _disposed;

            public AnonymousAsyncEnumerator(Func<CancellationToken, Task<bool>> moveNext, Func<T> current, Action dispose, Action reset = null)
            {
                _moveNext = moveNext;
                _current = current;
                _dispose = dispose;
#if SPREADS
                _reset = reset;
#endif
            }

            public Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                if (_disposed)
                    return TaskExt.False;

                return _moveNext(cancellationToken);
            }

            public T Current
            {
                get
                {
                    return _current();
                }
            }


            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _dispose();
                }
            }
#if SPREADS
            private readonly Action _reset;
            object IEnumerator.Current {
                get {
                    return Current;
                }
            }
            public bool MoveNext() {
                // TODO (!) this is blocking, must follow spreads contract
                return MoveNext(CancellationToken.None).Result;
            }

            public void Reset() {
                _reset();
            }
#endif
        }

        public static IAsyncEnumerable<TValue> Return<TValue>(TValue value)
        {
            return new[] { value }.ToAsyncEnumerable();
        }

        public static IAsyncEnumerable<TValue> Throw<TValue>(Exception exception)
        {
            if (exception == null)
                throw new ArgumentNullException("exception");

            return Create(() => Create<TValue>(
                ct => TaskExt.Throw<bool>(exception),
                () => { throw new InvalidOperationException(); },
                () => { })
            );
        }

        public static IAsyncEnumerable<TValue> Never<TValue>()
        {
            return Create(() => Create<TValue>(
                (ct, tcs) => tcs.Task,
                () => { throw new InvalidOperationException(); },
                () => { })
            );
        }

        public static IAsyncEnumerable<TValue> Empty<TValue>()
        {
            return Create(() => Create<TValue>(
                ct => TaskExt.False,
                () => { throw new InvalidOperationException(); },
                () => { })
            );
        }

        public static IAsyncEnumerable<int> Range(int start, int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");

            return Enumerable.Range(start, count).ToAsyncEnumerable();
        }

        public static IAsyncEnumerable<TResult> Repeat<TResult>(TResult element, int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException("count");

            return Enumerable.Repeat(element, count).ToAsyncEnumerable();
        }

        public static IAsyncEnumerable<TResult> Repeat<TResult>(TResult element)
        {
            return Create(() =>
            {
                return Create(
                    ct => TaskExt.True,
                    () => element,
                    () => { }
                );
            });
        }

        public static IAsyncEnumerable<TSource> Defer<TSource>(Func<IAsyncEnumerable<TSource>> factory)
        {
            if (factory == null)
                throw new ArgumentNullException("factory");

            return Create(() => factory().GetEnumerator());
        }

        public static IAsyncEnumerable<TResult> Generate<TState, TResult>(TState initialState, Func<TState, bool> condition, Func<TState, TState> iterate, Func<TState, TResult> resultSelector)
        {
            if (condition == null)
                throw new ArgumentNullException("condition");
            if (iterate == null)
                throw new ArgumentNullException("iterate");
            if (resultSelector == null)
                throw new ArgumentNullException("resultSelector");

            return Create(() =>
            {
                var i = initialState;
                var started = false;
                var current = default(TResult);

                return Create(
                    ct =>
                    {
                        var b = false;
                        try
                        {
                            if (started)
                                i = iterate(i);

                            b = condition(i);

                            if (b)
                                current = resultSelector(i);
                        }
                        catch (Exception ex)
                        {
                            return TaskExt.Throw<bool>(ex);
                        }

                        if (!b)
                            return TaskExt.False;

                        if (!started)
                            started = true;

                        return TaskExt.True;
                    },
                    () => current,
                    () => { }
                );
            });
        }

        public static IAsyncEnumerable<TSource> Using<TSource, TResource>(Func<TResource> resourceFactory, Func<TResource, IAsyncEnumerable<TSource>> enumerableFactory) where TResource : IDisposable
        {
            if (resourceFactory == null)
                throw new ArgumentNullException("resourceFactory");
            if (enumerableFactory == null)
                throw new ArgumentNullException("enumerableFactory");

            return Create(() =>
            {
                var resource = resourceFactory();
                var e = default(IAsyncEnumerator<TSource>);

                try
                {
                    e = enumerableFactory(resource).GetEnumerator();
                }
                catch (Exception)
                {
                    resource.Dispose();
                    throw;
                }

                var cts = new CancellationTokenDisposable();
                var d = Disposable.Create(cts, resource, e);

                var current = default(TSource);

                return Create(
                    (ct, tcs) =>
                    {
                        e.MoveNext(cts.Token).Then(t =>
                        {
                            t.Handle(tcs,
                                res =>
                                {
                                    if (res)
                                    {
                                        current = e.Current;
                                        tcs.TrySetResult(true);
                                    }
                                    else
                                    {
                                        d.Dispose();
                                        tcs.TrySetResult(false);
                                    }
                                },
                                ex =>
                                {
                                    d.Dispose();
                                    tcs.TrySetException(ex);
                                }
                            );
                        });

                        return tcs.Task;
                    },
                    () => current,
                    d.Dispose
                );
            });
        }
    }
}
