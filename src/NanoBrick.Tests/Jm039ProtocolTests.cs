using System;
using NanoBrick.Protocols;
using Xunit;

namespace NanoBrick.Tests
{
    /// <summary>
    /// The encoder must reproduce, byte for byte, frames captured with the sniffer on a real JM039TX remote
    /// (data + CRC, after the address).
    /// </summary>
    public class Jm039ProtocolTests
    {
        [Theory]
        [InlineData(Jm039Command.None, 1, false, "34CA4292D67E6AED29F91A3F06")]
        [InlineData(Jm039Command.None, 1, true, "34CA4692D67E6AED29F91AA3E9")]
        [InlineData(Jm039Command.LeftForward, 1, false, "34CA4292D67E6AED29799AB516")]
        [InlineData(Jm039Command.LeftBackward, 1, false, "34CA4292D67E6AED29B95A7A0E")]
        [InlineData(Jm039Command.RightForward, 1, true, "34CA4692D67E6AED29D97AC9A9")]
        [InlineData(Jm039Command.RightBackward, 1, false, "34CA4292D67E6AED29E90A2E44")]
        [InlineData(Jm039Command.LeftForward | Jm039Command.RightForward, 1, true, "34CA4692D67E6AED2959FA43B9")]
        [InlineData(Jm039Command.LeftBackward | Jm039Command.RightBackward, 1, false, "34CA4292D67E6AED29A94A6B4C")]
        [InlineData(Jm039Command.StopLeft, 1, false, "34CA4292D67E6AE929F916347B")]
        [InlineData(Jm039Command.StopRight, 1, false, "34CA4292D67E6AE529F9123BCD")]
        [InlineData(Jm039Command.None, 2, false, "34CA4292D67E6AED28F9193855")]
        [InlineData(Jm039Command.LeftForward, 2, false, "34CA4292D67E6AED287999B245")]
        [InlineData(Jm039Command.None, 3, false, "34CA4292D67E6AED2BF9187124")]
        [InlineData(Jm039Command.LeftBackward, 3, false, "34CA4292D67E6AED2BB958342C")]
        [InlineData(Jm039Command.None, 4, false, "34CA4292D67E6AED2EF91BAAB7")]
        [InlineData(Jm039Command.LeftForward, 4, false, "34CA4292D67E6AED2E799B20A7")]
        [InlineData(Jm039Command.LeftBackward, 4, false, "34CA4292D67E6AED2EB95BEFBF")]
        public void Encode_MatchesCapturedFrame(Jm039Command commands, int position, bool toggle, string expected)
        {
            var frame = new byte[Jm039Protocol.FrameLength];

            Jm039Protocol.Encode(commands, position, toggle, frame);

            Assert.Equal(expected, Convert.ToHexString(frame));
        }

        [Theory]
        [InlineData(1, new byte[] { 12, 13, 57, 58 })]
        [InlineData(2, new byte[] { 9, 10, 52, 53 })]
        [InlineData(3, new byte[] { 7, 8, 41, 42 })]
        [InlineData(4, new byte[] { 19, 20, 67, 68 })]
        public void GetChannels_ReturnsMeasuredChannels(int position, byte[] expected)
        {
            Assert.Equal(expected, Jm039Protocol.GetChannels(position));
        }

        [Fact]
        public void AddressRegister_IsAddressReversed()
        {
            var reversed = (byte[])Jm039Protocol.Address.Clone();
            Array.Reverse(reversed);

            Assert.Equal(reversed, Jm039Protocol.AddressRegister);
        }
    }
}
