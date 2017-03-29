using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.IO.Pipelines.Tests
{
    public class FlushAsyncCancellationTests: PipeTest
    {

        [Fact]
        public void GetResultThrowsIfFlushAsyncCancelledAfterOnCompleted()
        {
            var onCompletedCalled = false;
            var cancellationTokenSource = new CancellationTokenSource();
            var buffer = Pipe.Writer.Alloc(PipeTest.MaximumSizeHigh);
            buffer.Advance(PipeTest.MaximumSizeHigh);

            var awaiter = buffer.FlushAsync(cancellationTokenSource.Token);

            awaiter.OnCompleted(() =>
            {
                onCompletedCalled = true;
                Assert.Throws<OperationCanceledException>(() => awaiter.GetResult());
            });

            var awaiterIsCompleted = awaiter.IsCompleted;

            cancellationTokenSource.Cancel();

            Assert.False(awaiterIsCompleted);
            Assert.True(onCompletedCalled);
        }

        [Fact]
        public void GetResultThrowsIfFlushAsyncCancelledBeforeOnCompleted()
        {
            var onCompletedCalled = false;
            var cancellationTokenSource = new CancellationTokenSource();
            var buffer = Pipe.Writer.Alloc(PipeTest.MaximumSizeHigh);
            buffer.Advance(MaximumSizeHigh);

            var awaiter = buffer.FlushAsync(cancellationTokenSource.Token);
            var awaiterIsCompleted = awaiter.IsCompleted;

            cancellationTokenSource.Cancel();

            awaiter.OnCompleted(() =>
            {
                onCompletedCalled = true;
                Assert.Throws<OperationCanceledException>(() => awaiter.GetResult());
            });

            Assert.False(awaiterIsCompleted);
            Assert.True(onCompletedCalled);
        }

        [Fact]
        public void GetResultThrowsIfFlushAsyncTokenFiredAfterCancelPending()
        {
            var onCompletedCalled = false;
            var cancellationTokenSource = new CancellationTokenSource();
            var buffer = Pipe.Writer.Alloc(PipeTest.MaximumSizeHigh);
            buffer.Advance(MaximumSizeHigh);

            var awaiter = buffer.FlushAsync(cancellationTokenSource.Token);
            var awaiterIsCompleted = awaiter.IsCompleted;

            Pipe.Writer.CancelPendingFlush();
            cancellationTokenSource.Cancel();

            awaiter.OnCompleted(() =>
            {
                onCompletedCalled = true;
                Assert.Throws<OperationCanceledException>(() => awaiter.GetResult());
            });

            Assert.False(awaiterIsCompleted);
            Assert.True(onCompletedCalled);
        }

        [Fact]
        public void FlushAsyncThrowsIfPassedCancelledCancellationToken()
        {
            var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            var buffer = Pipe.Writer.Alloc();

            Assert.Throws<OperationCanceledException>(() => buffer.FlushAsync(cancellationTokenSource.Token));
        }

        [Fact]
        public async Task FlushAsyncWithNewCancellationTokenNotAffectedByPrevious()
        {
            var cancellationTokenSource1 = new CancellationTokenSource();
            var buffer = Pipe.Writer.Alloc(10);
            buffer.Advance(10);
            await buffer.FlushAsync(cancellationTokenSource1.Token);

            cancellationTokenSource1.Cancel();

            var cancellationTokenSource2 = new CancellationTokenSource();
            buffer = Pipe.Writer.Alloc(10);
            buffer.Advance(10);
            // Verifying that ReadAsync does not throw
            await buffer.FlushAsync(cancellationTokenSource2.Token);
        }

        [Fact]
        public void FlushAsyncReturnsCanceledIfFlushCancelled()
        {
            var writableBuffer = Pipe.Writer.Alloc(MaximumSizeHigh);
            writableBuffer.Advance(MaximumSizeHigh);
            var flushAsync = writableBuffer.FlushAsync();

            Assert.False(flushAsync.IsCompleted);

            Pipe.Writer.CancelPendingFlush();

            Assert.True(flushAsync.IsCompleted);
            var flushResult = flushAsync.GetResult();
            Assert.True(flushResult.IsCancelled);
        }

        [Fact]
        public void FlushAsyncReturnsCanceledIfCancelledBeforeFlush()
        {
            var writableBuffer = Pipe.Writer.Alloc(MaximumSizeHigh);
            writableBuffer.Advance(MaximumSizeHigh);

            Pipe.Writer.CancelPendingFlush();

            var flushAsync = writableBuffer.FlushAsync();

            Assert.True(flushAsync.IsCompleted);
            var flushResult = flushAsync.GetResult();
            Assert.True(flushResult.IsCancelled);
        }
    }
}