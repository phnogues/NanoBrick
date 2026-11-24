using NanoBrick.Protocols;

namespace NanoBrick.Transmission
{
    /// <summary>
    /// The link with one receiver (one selector position): its two outputs and when to talk or stay silent.
    /// Like the original remote, nothing is sent at rest; after a silence, neutral frames are sent first so
    /// that the receiver locks on before the commands arrive.
    /// </summary>
    internal sealed class ReceiverLink
    {
        private const int LockInMs = 2000;

        /// <summary>Output A (left wheel) and output B (right wheel).</summary>
        public readonly MotorQueue[] Outputs = { new MotorQueue(), new MotorQueue() };

        /// <summary>Bit that flips on every frame sent.</summary>
        public bool Toggle { get; set; }

        private bool _silent = true;
        private int _lockInFrames, _trailingFrames;

        /// <summary>Decides what to send in this frame slot.</summary>
        /// <param name="keepAlive">Keep sending neutral frames so that the receiver stays locked.</param>
        /// <param name="command">Command to send.</param>
        /// <returns>False when the link must stay silent.</returns>
        public bool NextFrame(bool keepAlive, out Jm039Command command)
        {
            command = Jm039Command.None;
            bool hasWork = Outputs[0].IsBusy || Outputs[1].IsBusy;

            if (hasWork && _silent)
            {
                _lockInFrames = LockInMs / Jm039Protocol.FramePeriodMs;
                _silent = false;
            }

            if (_lockInFrames > 0)
            {
                _lockInFrames--;
                return true;
            }

            if (hasWork)
            {
                command = Outputs[0].NextCommand() | Outputs[1].NextCommand();
                if (command != Jm039Command.None) _trailingFrames = Jm039Protocol.TrailingNeutralFrames;
                return true;
            }

            // Keep-alive needs a neutral frame on every slot: one slot out of three is not enough
            // (the receiver light starts blinking).
            if (keepAlive)
            {
                _silent = false;
                return true;
            }

            if (_trailingFrames > 0)
            {
                _trailingFrames--;
                return true;
            }

            _silent = true;
            return false;
        }
    }
}
