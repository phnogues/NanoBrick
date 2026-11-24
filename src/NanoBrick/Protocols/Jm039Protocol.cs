using System;

namespace NanoBrick.Protocols
{
    /// <summary>Buttons of the grey JM039TX remote. Values can be combined.</summary>
    [Flags]
    public enum Jm039Command
    {
        /// <summary>Nothing pressed: neutral frame.</summary>
        None = 0,

        /// <summary>Left wheel (output A), first direction.</summary>
        LeftForward = 1,

        /// <summary>Left wheel (output A), second direction.</summary>
        LeftBackward = 2,

        /// <summary>Right wheel (output B), first direction.</summary>
        RightForward = 4,

        /// <summary>Right wheel (output B), second direction.</summary>
        RightBackward = 8,

        /// <summary>Left orange button: stops output A.</summary>
        StopLeft = 16,

        /// <summary>Right orange button: stops output B.</summary>
        StopRight = 32,
    }

    /// <summary>
    /// Frame encoder for the grey JM039TX remote (thumb wheels), reverse-engineered with the sniffer.
    /// Pure logic without any hardware access, so it is also compiled and tested on the PC
    /// (NanoBrick.Tests). Protocol description: docs/protocol-jm039.md.
    /// </summary>
    public static class Jm039Protocol
    {
        /// <summary>Remote address, in on-air order.</summary>
        public static readonly byte[] Address = { 0x88, 0xB4, 0x87, 0x2E, 0xCB };

        /// <summary>
        /// Same address in nRF24 TX_ADDR register order (reversed).
        /// Written as a literal on purpose: under nanoFramework, a static field initialised from another
        /// class's static field can end up null.
        /// </summary>
        public static readonly byte[] AddressRegister = { 0xCB, 0x2E, 0x87, 0xB4, 0x88 };

        /// <summary>Number of selector positions (one receiver each).</summary>
        public const int PositionCount = 4;

        /// <summary>The remote sends one frame every 8 ms.</summary>
        public const int FramePeriodMs = 8;

        /// <summary>One wheel notch = 5 active frames...</summary>
        public const int ActiveFramesPerStep = 5;

        /// <summary>... followed by at least 6 neutral frames.</summary>
        public const int NeutralFramesBetweenSteps = 6;

        /// <summary>Neutral frames sent after the last command, before going silent.</summary>
        public const int TrailingNeutralFrames = 19;

        /// <summary>Bytes following the address: 11 data bytes and a 2-byte CRC.</summary>
        public const int FrameLength = 13;

        private const int DataLength = 11;
        private const ushort CrcSeed = 0x8ABC;   // not the 0xFFFF seed of the nRF24's own CRC

        // Channels of each selector position (a receiver only listens to its own)
        private static readonly byte[][] Channels =
        {
            new byte[] { 12, 13, 57, 58 },
            new byte[] { 9, 10, 52, 53 },
            new byte[] { 7, 8, 41, 42 },
            new byte[] { 19, 20, 67, 68 },
        };

        // Selector byte S and check byte D2 depend on the position (XOR against position 1)
        private static readonly byte[] SelectorXor = { 0x00, 0x01, 0x02, 0x07 };
        private static readonly byte[] CheckXor = { 0x00, 0x03, 0x02, 0x01 };

        // Neutral frame for position 1: 34 CA T 92 D6 7E 6A B S D1 D2
        private static readonly byte[] NeutralFrame = { 0x34, 0xCA, 0x42, 0x92, 0xD6, 0x7E, 0x6A, 0xED, 0x29, 0xF9, 0x1A };

        private const int ToggleByte = 2, ButtonsByte = 7, SelectorByte = 8, WheelsByte = 9, CheckByte = 10;

        /// <summary>Radio channels used by a selector position (1 to 4).</summary>
        public static byte[] GetChannels(int position)
        {
            return Channels[ClampPosition(position) - 1];
        }

        /// <summary>
        /// Encodes the <see cref="FrameLength"/> bytes that follow the address: data, then CRC.
        /// Command bits are active low; the check byte D2 follows a linear relation measured with the
        /// sniffer (no simpler formula was found).
        /// </summary>
        /// <param name="commands">Buttons pressed in this frame.</param>
        /// <param name="position">Selector position, 1 to 4.</param>
        /// <param name="toggle">Bit that flips on every frame.</param>
        /// <param name="frame">Output buffer of at least <see cref="FrameLength"/> bytes.</param>
        public static void Encode(Jm039Command commands, int position, bool toggle, byte[] frame)
        {
            int index = ClampPosition(position) - 1;
            Array.Copy(NeutralFrame, 0, frame, 0, DataLength);

            if (toggle) frame[ToggleByte] |= 0x04;
            frame[SelectorByte] ^= SelectorXor[index];
            frame[CheckByte] ^= CheckXor[index];

            if ((commands & Jm039Command.LeftForward) != 0) Apply(frame, WheelsByte, 0x80, 0x80);
            if ((commands & Jm039Command.LeftBackward) != 0) Apply(frame, WheelsByte, 0x40, 0x40);
            if ((commands & Jm039Command.RightForward) != 0) Apply(frame, WheelsByte, 0x20, 0x60);
            if ((commands & Jm039Command.RightBackward) != 0) Apply(frame, WheelsByte, 0x10, 0x10);
            if ((commands & Jm039Command.StopLeft) != 0) Apply(frame, ButtonsByte, 0x04, 0x0C);
            if ((commands & Jm039Command.StopRight) != 0) Apply(frame, ButtonsByte, 0x08, 0x08);

            // CRC16-CCITT over the address, then the data
            ushort crc = Crc16(Address, 0, Address.Length, CrcSeed);
            crc = Crc16(frame, 0, DataLength, crc);
            frame[DataLength] = (byte)(crc >> 8);
            frame[DataLength + 1] = (byte)crc;
        }

        /// <summary>CRC16-CCITT (polynomial 0x1021, not reflected), continuing from <paramref name="crc"/>.</summary>
        public static ushort Crc16(byte[] data, int offset, int count, ushort crc)
        {
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= (ushort)(data[i] << 8);
                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
                }
            }
            return crc;
        }

        // Clears an active-low command bit and updates the check byte accordingly
        private static void Apply(byte[] frame, int commandByte, byte commandBit, byte checkMask)
        {
            frame[commandByte] ^= commandBit;
            frame[CheckByte] ^= checkMask;
        }

        private static int ClampPosition(int position)
        {
            return position < 1 || position > PositionCount ? 1 : position;
        }
    }
}
