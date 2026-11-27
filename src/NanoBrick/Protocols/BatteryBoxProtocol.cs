// The XN297 coding (whitening + CRC) is ported from DIY-Multiprotocol-TX-Module (XN297_EMU.ino, GPLv3):
// https://github.com/pascallanger/DIY-Multiprotocol-TX-Module

namespace NanoBrick.Protocols
{
    /// <summary>
    /// Frame encoder for the 4-output battery box and its white remote (XN297 chip, emulated on the nRF24).
    /// On / off only: an output runs at full speed while frames with its button arrive, and stops at the
    /// first frame without it. The box pairs with the first remote it hears: frames carry no ID.
    /// Pure logic, tested on the PC (NanoBrick.Tests). Protocol description: docs/protocol-battery-box.md.
    /// </summary>
    public static class BatteryBoxProtocol
    {
        /// <summary>Fixed radio channel.</summary>
        public const byte Channel = 2;

        /// <summary>"Nothing pressed" frames sent after a release, like the original remote.</summary>
        public const int TrailingIdleFrames = 20;

        /// <summary>Bytes pushed to the nRF24: 5 whitened address bytes, 10 data bytes, 2 CRC bytes.</summary>
        public const int FrameLength = 17;

        /// <summary>Number of outputs (marked 1 to 4 on the box, a to d in the commands).</summary>
        public const int OutputCount = 4;

        /// <summary>XN297 address of the remote.</summary>
        public static readonly byte[] Address = { 0xC1, 0xC2, 0xC3, 0xC4, 0xC5 };

        /// <summary>nRF24 TX_ADDR register (register order): XN297 preamble 71 0F 55, then 0C 00.</summary>
        public static readonly byte[] AddressRegister = { 0x55, 0x0F, 0x71, 0x0C, 0x00 };

        // Arrow buttons (data byte 7), one bit each
        public const byte AUp = 0x02, ADown = 0x01;
        public const byte BRight = 0x20, BLeft = 0x10;
        public const byte CUp = 0x04, CDown = 0x08;
        public const byte DRight = 0x80, DLeft = 0x40;

        // Square buttons (data byte 8)
        public const byte LeftSquare = 0x01, RightSquare = 0x02;

        private const int DataLength = 10;
        private const uint WhiteningSeed = 0xE3B14BE0;   // XN297 LFSR x^7 + x^3 + 1, seed 0x71
        private const int ChecksumOffset = 0x14;

        /// <summary>Arrow button that drives an output (0 = A … 3 = D) in one direction.</summary>
        public static byte ArrowButton(int output, bool forward)
        {
            switch (output)
            {
                case 0: return forward ? AUp : ADown;
                case 1: return forward ? BRight : BLeft;
                case 2: return forward ? CUp : CDown;
                default: return forward ? DRight : DLeft;
            }
        }

        /// <summary>
        /// Encodes a frame ready for the nRF24 TX FIFO (nRF24 CRC disabled).
        /// Data: <c>00 00 00 00 01 01 S6 ARROWS SQUARES SUM</c>, then XN297 coding.
        /// </summary>
        /// <param name="arrows">Arrow button bits (see <see cref="AUp"/> …).</param>
        /// <param name="squares">Square button bits (<see cref="LeftSquare"/>, <see cref="RightSquare"/>).</param>
        /// <param name="frame">Output buffer of at least <see cref="FrameLength"/> bytes.</param>
        public static void Encode(int arrows, int squares, byte[] frame)
        {
            var data = new byte[DataLength];
            data[4] = 0x01;
            data[5] = 0x01;
            data[6] = (byte)((squares & RightSquare) != 0 ? 0x01 : 0x00);
            data[7] = (byte)arrows;
            data[8] = (byte)squares;
            int sum = ChecksumOffset;
            for (int i = 0; i < DataLength - 1; i++) sum += data[i];
            data[DataLength - 1] = (byte)sum;

            uint lfsr = WhiteningSeed;
            ushort crc = 0xFFFF;
            int n = 0;
            for (int i = Address.Length - 1; i >= 0; i--) frame[n++] = Whiten(Address[i], ref lfsr, ref crc);
            for (int i = 0; i < DataLength; i++) frame[n++] = Whiten(ReverseBits(data[i]), ref lfsr, ref crc);

            ushort checksum = (ushort)(~crc ^ (lfsr >> 16));
            frame[n++] = (byte)(checksum >> 8);
            frame[n] = (byte)checksum;
        }

        // Updates the CRC16-CCITT with the clear byte, then XORs it with the next whitening byte
        private static byte Whiten(byte value, ref uint lfsr, ref ushort crc)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }

            byte whitening = (byte)(lfsr >> 24);
            lfsr = (lfsr << 8) | ((uint)(byte)(whitening ^ (lfsr >> 8)) << 4);
            return (byte)(value ^ whitening);
        }

        // The XN297 sends data bytes least significant bit first
        private static byte ReverseBits(byte value)
        {
            int result = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                result = (result << 1) | (value & 1);
                value >>= 1;
            }
            return (byte)result;
        }
    }
}
