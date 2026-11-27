using System;
using System.Threading;
using NanoBrick.Hardware;
using NanoBrick.Protocols;

namespace NanoBrick.Transmission
{
    /// <summary>
    /// Imitates up to 4 grey JM039TX remotes at once, one per receiver selector position
    /// (4 receivers × 2 outputs = 8 motors), plus the white remote of the 4-output battery box.
    /// A dedicated thread runs one transmission round every 8 ms: each active position gets its frame
    /// on its 4 channels, then the battery box gets its frame on its own channel and address.
    /// Every output has its own job queue, so outputs A and B of a receiver can move at the same time.
    /// </summary>
    internal sealed class Transmitter
    {
        /// <summary>Value of <c>output</c> meaning "every output" in <see cref="StopBatteryBox"/>.</summary>
        public const int AllOutputs = -1;

        private const int KeepAliveMs = 15000;     // extended by every command
        private const int MinimumSleepMs = 2;      // always yield, or the Bluetooth threads starve
        private const int MaxLateMs = 50;          // further behind (garbage collection…): restart the clock

        private readonly Nrf24Radio _radio;
        private readonly object _lock = new object();
        private readonly ReceiverLink[] _receivers = new ReceiverLink[Jm039Protocol.PositionCount];
        private readonly byte[] _frame = new byte[Jm039Protocol.FrameLength];
        private long _keepAliveUntil;

        // Computing a frame (CRC included) is expensive in interpreted code, but the managed heap of the
        // ESP32 BLE firmware is tiny (~29 KB free at startup, and every Bluetooth connection needs a few KB).
        // So the packet cache is small and allocated once: for each (position, toggle), the neutral packet
        // and the last command packet, which is exactly what a notch alternates between.
        private const int Jm039Slots = Jm039Protocol.PositionCount * 2 * 2;
        private readonly byte[][] _jm039Packets = new byte[Jm039Slots][];
        private readonly int[] _jm039SlotCommand = new int[Jm039Slots];   // command encoded in each slot, -1 = none

        // Battery box: button held and frames left per output (A to D), square buttons
        private readonly int[] _boxArrow = new int[BatteryBoxProtocol.OutputCount];
        private readonly int[] _boxFramesLeft = new int[BatteryBoxProtocol.OutputCount];
        private int _boxSquares, _boxSquareFramesLeft, _boxTrailingFrames;
        // Battery box packets, allocated once: "nothing pressed", the last arrow combination, and a scratch one
        private readonly byte[] _boxIdlePacket = NewPacket(BatteryBoxProtocol.FrameLength);
        private readonly byte[] _boxArrowsPacket = NewPacket(BatteryBoxProtocol.FrameLength);
        private readonly byte[] _boxScratchPacket = NewPacket(BatteryBoxProtocol.FrameLength);
        private readonly byte[] _boxFrame = new byte[BatteryBoxProtocol.FrameLength];
        private int _boxArrowsCached = -1;

        public Transmitter(Nrf24Radio radio)
        {
            _radio = radio;
            for (int i = 0; i < _receivers.Length; i++) _receivers[i] = new ReceiverLink();
            for (int i = 0; i < Jm039Slots; i++)
            {
                _jm039Packets[i] = NewPacket(Jm039Protocol.FrameLength);
                _jm039SlotCommand[i] = -1;
            }
            EncodeBatteryBoxPacket(0, 0, _boxIdlePacket);
        }

        /// <summary>Positions kept alive and stopped by <see cref="StopAll"/> (bit 0 = position 1).</summary>
        public int ServedPositions { get; set; } = 0x0F;

        /// <summary>Longest transmission round since the last read, in milliseconds (diagnostics).</summary>
        public int MaxRoundMs { get; private set; }

        public void Start()
        {
            new Thread(Run).Start();
        }

        /// <summary>Queues <paramref name="steps"/> wheel notches. Returns false when the queue is full.</summary>
        /// <param name="position">Receiver selector position, 1 to 4.</param>
        /// <param name="output">0 = A (left wheel), 1 = B (right wheel).</param>
        public bool QueueSteps(int position, int output, Jm039Command command, int steps)
        {
            lock (_lock)
            {
                MotorQueue queue = _receivers[position - 1].Outputs[output];
                for (int i = 0; i < steps; i++)
                {
                    if (!queue.TryEnqueue(command, Jm039Protocol.ActiveFramesPerStep, Jm039Protocol.NeutralFramesBetweenSteps))
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>Holds the orange stop button for <paramref name="tenths"/> × 100 ms; cancels the waiting steps.</summary>
        public bool Stop(int position, int output, int tenths)
        {
            lock (_lock)
            {
                MotorQueue queue = _receivers[position - 1].Outputs[output];
                queue.Clear();
                Jm039Command stop = output == 0 ? Jm039Command.StopLeft : Jm039Command.StopRight;
                return queue.TryEnqueue(stop, TenthsToFrames(tenths), 0);
            }
        }

        /// <summary>Sends neutral frames to a position for <paramref name="tenths"/> × 100 ms.</summary>
        public bool Wait(int position, int tenths)
        {
            lock (_lock)
            {
                return _receivers[position - 1].Outputs[0].TryEnqueue(Jm039Command.None, 0, TenthsToFrames(tenths));
            }
        }

        /// <summary>Stops every output of the served positions, and the battery box.</summary>
        public void StopAll()
        {
            for (int position = 1; position <= Jm039Protocol.PositionCount; position++)
            {
                if ((ServedPositions & (1 << (position - 1))) == 0) continue;
                Stop(position, 0, 3);
                Stop(position, 1, 3);
            }
            StopBatteryBox(AllOutputs);
        }

        /// <summary>Keep-alive: neutral frames on every slot so that the served receivers stay locked.</summary>
        public void SetKeepAlive(bool enabled)
        {
            lock (_lock) _keepAliveUntil = enabled ? Now() + KeepAliveMs : 0;
        }

        /// <summary>Extends the keep-alive if it is on (called on every command).</summary>
        public void ExtendKeepAlive()
        {
            lock (_lock)
            {
                if (_keepAliveUntil > Now()) _keepAliveUntil = Now() + KeepAliveMs;
            }
        }

        /// <summary>
        /// Battery box: runs an output (0 = A … 3 = D) in one direction for <paramref name="tenths"/> × 100 ms,
        /// at full speed (the box has no speed control). Several outputs can run at the same time.
        /// </summary>
        public void RunBatteryBoxOutput(int output, bool forward, int tenths)
        {
            lock (_lock)
            {
                _boxArrow[output] = BatteryBoxProtocol.ArrowButton(output, forward);
                _boxFramesLeft[output] = TenthsToFrames(tenths);
            }
        }

        /// <summary>Battery box: holds a square button (1 = left, 2 = right) for <paramref name="tenths"/> × 100 ms.</summary>
        public void PressBatteryBoxSquare(int square, int tenths)
        {
            lock (_lock)
            {
                _boxSquares = square == 1 ? BatteryBoxProtocol.LeftSquare : BatteryBoxProtocol.RightSquare;
                _boxSquareFramesLeft = TenthsToFrames(tenths);
            }
        }

        /// <summary>Battery box: stops one output (0 to 3), or all of them (<see cref="AllOutputs"/>).</summary>
        public void StopBatteryBox(int output)
        {
            lock (_lock)
            {
                for (int i = 0; i < BatteryBoxProtocol.OutputCount; i++)
                {
                    if (output == AllOutputs || i == output) _boxFramesLeft[i] = 0;
                }
                if (output == AllOutputs) _boxSquareFramesLeft = 0;
                _boxTrailingFrames = BatteryBoxProtocol.TrailingIdleFrames;
            }
        }

        public int ReadAndResetMaxRoundMs()
        {
            int value = MaxRoundMs;
            MaxRoundMs = 0;
            return value;
        }

        private void Run()
        {
            long nextRound = Now();
            var send = new bool[Jm039Protocol.PositionCount];
            var commands = new Jm039Command[Jm039Protocol.PositionCount];

            while (true)
            {
                long wait = nextRound - Now();
                Thread.Sleep(wait > MinimumSleepMs ? (int)wait : MinimumSleepMs);
                nextRound += Jm039Protocol.FramePeriodMs;
                if (Now() - nextRound > MaxLateMs) nextRound = Now();

                long start = Now();
                byte[] boxPacket;
                lock (_lock)
                {
                    bool keepAlive = _keepAliveUntil > Now();
                    for (int i = 0; i < _receivers.Length; i++)
                    {
                        bool served = keepAlive && (ServedPositions & (1 << i)) != 0;
                        send[i] = _receivers[i].NextFrame(served, out commands[i]);
                    }
                    boxPacket = NextBatteryBoxPacket();
                }

                // Radio work happens outside the lock, so Bluetooth commands are never blocked by it
                for (int i = 0; i < _receivers.Length; i++)
                {
                    if (send[i]) SendJm039Frame(i, commands[i]);
                }
                if (boxPacket != null)
                {
                    _radio.SetAddress(BatteryBoxProtocol.AddressRegister);
                    _radio.SendPacket(BatteryBoxProtocol.Channel, boxPacket);
                }

                int duration = (int)(Now() - start);
                if (duration > MaxRoundMs) MaxRoundMs = duration;
            }
        }

        private void SendJm039Frame(int index, Jm039Command command)
        {
            ReceiverLink receiver = _receivers[index];
            byte[] packet = GetJm039Packet(index, command, receiver.Toggle);
            receiver.Toggle = !receiver.Toggle;

            _radio.SetAddress(Jm039Protocol.AddressRegister);
            byte[] channels = Jm039Protocol.GetChannels(index + 1);
            for (int i = 0; i < channels.Length; i++)
            {
                _radio.SendPacket(channels[i], packet);
            }
        }

        private byte[] GetJm039Packet(int index, Jm039Command command, bool toggle)
        {
            int slot = ((index * 2) + (toggle ? 1 : 0)) * 2 + (command == Jm039Command.None ? 0 : 1);
            byte[] packet = _jm039Packets[slot];
            if (_jm039SlotCommand[slot] != (int)command)
            {
                Jm039Protocol.Encode(command, index + 1, toggle, _frame);
                Array.Copy(_frame, 0, packet, 1, _frame.Length);
                _jm039SlotCommand[slot] = (int)command;
            }
            return packet;
        }

        // Battery box frame for this round (called under the lock), or null to stay silent. Like the original
        // remote: one frame while a button is held, then 20 "nothing pressed" frames, then silence.
        private byte[] NextBatteryBoxPacket()
        {
            int arrows = 0, squares = 0;
            bool pressed = false;
            for (int i = 0; i < BatteryBoxProtocol.OutputCount; i++)
            {
                if (_boxFramesLeft[i] == 0) continue;
                _boxFramesLeft[i]--;
                arrows |= _boxArrow[i];
                pressed = true;
            }
            if (_boxSquareFramesLeft > 0)
            {
                _boxSquareFramesLeft--;
                squares = _boxSquares;
                pressed = true;
            }

            if (pressed) _boxTrailingFrames = BatteryBoxProtocol.TrailingIdleFrames;
            else if (_boxTrailingFrames > 0) _boxTrailingFrames--;
            else return null;

            if (squares != 0)   // rare: not cached
            {
                EncodeBatteryBoxPacket(arrows, squares, _boxScratchPacket);
                return _boxScratchPacket;
            }
            if (arrows == 0) return _boxIdlePacket;

            if (arrows != _boxArrowsCached)
            {
                EncodeBatteryBoxPacket(arrows, 0, _boxArrowsPacket);
                _boxArrowsCached = arrows;
            }
            return _boxArrowsPacket;
        }

        private void EncodeBatteryBoxPacket(int arrows, int squares, byte[] packet)
        {
            BatteryBoxProtocol.Encode(arrows, squares, _boxFrame);
            Array.Copy(_boxFrame, 0, packet, 1, _boxFrame.Length);
        }

        // Packet buffer for a frame, prefixed with the nRF24 W_TX_PAYLOAD command
        private static byte[] NewPacket(int frameLength)
        {
            var packet = new byte[frameLength + 1];
            packet[0] = Nrf24Radio.TxPayloadCommand;
            return packet;
        }

        private static int TenthsToFrames(int tenths)
        {
            return tenths * 100 / Jm039Protocol.FramePeriodMs;
        }

        private static long Now()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }
    }
}
