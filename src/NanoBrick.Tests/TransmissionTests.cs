using NanoBrick.Protocols;
using NanoBrick.Transmission;
using Xunit;

namespace NanoBrick.Tests
{
    /// <summary>Scheduling of the frames sent to one receiver, like the original remote does it.</summary>
    public class TransmissionTests
    {
        private const int LockInFrames = 2000 / Jm039Protocol.FramePeriodMs;

        [Fact]
        public void MotorQueue_PlaysActiveThenNeutralFrames()
        {
            var queue = new MotorQueue();
            queue.TryEnqueue(Jm039Command.LeftForward, 2, 1);

            Assert.Equal(Jm039Command.LeftForward, queue.NextCommand());
            Assert.Equal(Jm039Command.LeftForward, queue.NextCommand());
            Assert.Equal(Jm039Command.None, queue.NextCommand());
            Assert.False(queue.IsBusy);
        }

        [Fact]
        public void MotorQueue_RefusesJobsWhenFull()
        {
            var queue = new MotorQueue();
            int accepted = 0;
            while (queue.TryEnqueue(Jm039Command.LeftForward, 1, 1) && accepted < 100) accepted++;

            Assert.Equal(31, accepted);   // ring buffer of 32 slots, one kept free
        }

        [Fact]
        public void MotorQueue_ClearDropsTheJobInProgress()
        {
            var queue = new MotorQueue();
            queue.TryEnqueue(Jm039Command.RightForward, 5, 6);
            queue.NextCommand();

            queue.Clear();

            Assert.False(queue.IsBusy);
            Assert.Equal(Jm039Command.None, queue.NextCommand());
        }

        [Fact]
        public void ReceiverLink_IsSilentAtRest()
        {
            var link = new ReceiverLink();

            Assert.False(link.NextFrame(false, out _));
        }

        [Fact]
        public void ReceiverLink_SendsLockInFramesBeforeTheFirstCommand()
        {
            var link = new ReceiverLink();
            link.Outputs[0].TryEnqueue(Jm039Command.LeftForward, 1, 0);

            for (int i = 0; i < LockInFrames; i++)
            {
                Assert.True(link.NextFrame(false, out Jm039Command neutral));
                Assert.Equal(Jm039Command.None, neutral);
            }

            Assert.True(link.NextFrame(false, out Jm039Command command));
            Assert.Equal(Jm039Command.LeftForward, command);
        }

        [Fact]
        public void ReceiverLink_CombinesBothOutputsInOneFrame()
        {
            var link = new ReceiverLink();
            link.NextFrame(true, out _);   // keep-alive: already locked, no lock-in delay
            link.Outputs[0].TryEnqueue(Jm039Command.LeftForward, 1, 0);
            link.Outputs[1].TryEnqueue(Jm039Command.RightBackward, 1, 0);

            link.NextFrame(true, out Jm039Command command);

            Assert.Equal(Jm039Command.LeftForward | Jm039Command.RightBackward, command);
        }

        [Fact]
        public void ReceiverLink_SendsTrailingNeutralFramesThenGoesSilent()
        {
            var link = new ReceiverLink();
            link.NextFrame(true, out _);
            link.Outputs[0].TryEnqueue(Jm039Command.LeftForward, 1, 0);
            link.NextFrame(false, out _);

            for (int i = 0; i < Jm039Protocol.TrailingNeutralFrames; i++)
            {
                Assert.True(link.NextFrame(false, out _));
            }
            Assert.False(link.NextFrame(false, out _));
        }
    }
}
