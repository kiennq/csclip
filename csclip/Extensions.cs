using System;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace csclip
{
    public static class DispatcherExtensions
    {
        public static ResumeAwaitable Resume(this Dispatcher dispatcher)
        {
            return new ResumeAwaitable(dispatcher);
        }

        public struct ResumeAwaitable : INotifyCompletion
        {
            private readonly Dispatcher _dispatcher;

            public ResumeAwaitable(Dispatcher dispatcher)
            {
                _dispatcher = dispatcher;
            }

            public ResumeAwaitable GetAwaiter()
            {
                return this;
            }

            public void GetResult()
            {
            }

            public bool IsCompleted => _dispatcher.CheckAccess();

            public void OnCompleted(Action continuation)
            {
                _ = _dispatcher.InvokeAsync(continuation);
            }
        }
    }

}
