using System;
using NanoBrick.Protocols;
using Xunit;

namespace NanoBrick.Tests
{
    /// <summary>
    /// The encoder must reproduce, byte for byte, raw XN297 frames captured with the sniffer on the real
    /// white remote (whitened address + data + CRC).
    /// </summary>
    public class BatteryBoxProtocolTests
    {
        [Theory]
        [InlineData(BatteryBoxProtocol.AUp, 0, "2675882844BCE5660D2E0C885269F65846")]
        [InlineData(BatteryBoxProtocol.ADown, 0, "2675882844BCE5660D2E0C8892690691AE")]
        [InlineData(BatteryBoxProtocol.BRight, 0, "2675882844BCE5660D2E0C88166982A738")]
        [InlineData(BatteryBoxProtocol.BLeft, 0, "2675882844BCE5660D2E0C881A698A5351")]
        [InlineData(BatteryBoxProtocol.CUp, 0, "2675882844BCE5660D2E0C883269B68BE9")]
        [InlineData(BatteryBoxProtocol.CDown, 0, "2675882844BCE5660D2E0C880269966A2E")]
        [InlineData(BatteryBoxProtocol.DRight, 0, "2675882844BCE5660D2E0C881369871C6D")]
        [InlineData(BatteryBoxProtocol.DLeft, 0, "2675882844BCE5660D2E0C88106984755E")]
        [InlineData(0, BatteryBoxProtocol.LeftSquare, "2675882844BCE5660D2E0C8812E906B16C")]
        [InlineData(0, BatteryBoxProtocol.RightSquare, "2675882844BCE5660D2E0C081229760497")]
        public void Encode_MatchesCapturedFrame(int arrows, int squares, string expected)
        {
            var frame = new byte[BatteryBoxProtocol.FrameLength];

            BatteryBoxProtocol.Encode(arrows, squares, frame);

            Assert.Equal(expected, Convert.ToHexString(frame));
        }

        [Theory]
        [InlineData(0, true, BatteryBoxProtocol.AUp)]
        [InlineData(0, false, BatteryBoxProtocol.ADown)]
        [InlineData(1, true, BatteryBoxProtocol.BRight)]
        [InlineData(1, false, BatteryBoxProtocol.BLeft)]
        [InlineData(2, true, BatteryBoxProtocol.CUp)]
        [InlineData(2, false, BatteryBoxProtocol.CDown)]
        [InlineData(3, true, BatteryBoxProtocol.DRight)]
        [InlineData(3, false, BatteryBoxProtocol.DLeft)]
        public void ArrowButton_MapsOutputAndDirection(int output, bool forward, byte expected)
        {
            Assert.Equal(expected, BatteryBoxProtocol.ArrowButton(output, forward));
        }
    }
}
