using System;
using System.Device.Gpio;
using System.Device.Spi;

namespace NanoBrick.Hardware
{
    /// <summary>
    /// Minimal transmit-only driver for the nRF24L01+, set up to imitate toy remotes:
    /// 1 Mbit/s, 5-byte address, no auto-ack, nRF24 CRC disabled (the protocols compute their own).
    /// </summary>
    internal sealed class Nrf24Radio : IDisposable
    {
        /// <summary>First byte of every packet given to <see cref="SendPacket"/> (W_TX_PAYLOAD command).</summary>
        public const byte TxPayloadCommand = 0xA0;

        // Registers, commands and bits used
        private const byte ConfigRegister = 0x00, EnAaRegister = 0x01, SetupAwRegister = 0x03,
                           SetupRetrRegister = 0x04, RfChRegister = 0x05, RfSetupRegister = 0x06,
                           StatusRegister = 0x07, TxAddrRegister = 0x10, DynpdRegister = 0x1C, FeatureRegister = 0x1D;
        private const byte WriteRegisterCommand = 0x20, FlushTxCommand = 0xE1;
        private const byte TxDataSentFlag = 0x20;
        private const int MaxStatusPolls = 40;

        private static readonly byte[] ClearStatusFlags = { WriteRegisterCommand | StatusRegister, 0x70 };

        private readonly SpiDevice _spi;
        private readonly GpioPin _ce;
        private readonly byte[] _txBuffer = new byte[33];
        private readonly byte[] _rxBuffer = new byte[33];
        private readonly byte[] _singleByte = new byte[1];
        private readonly byte[] _setChannel = { WriteRegisterCommand | RfChRegister, 0 };
        private byte[] _currentAddress;

        /// <summary>Packets acknowledged as sent by the chip (diagnostics).</summary>
        public int PacketsSent { get; private set; }

        /// <summary>Packets that did not leave the FIFO in time (diagnostics).</summary>
        public int PacketsFailed { get; private set; }

        public Nrf24Radio(int spiBus, int csnPin, GpioPin ce)
        {
            _ce = ce;
            _ce.Write(PinValue.Low);
            _spi = SpiDevice.Create(new SpiConnectionSettings(spiBus, csnPin)
            {
                ClockFrequency = 4_000_000,
                Mode = SpiMode.Mode0,
                DataFlow = DataFlow.MsbFirst,
            });
        }

        /// <summary>Configures the chip. Returns false if it does not answer (wiring, power supply).</summary>
        /// <param name="addressRegister">TX address, in register order.</param>
        /// <param name="powerLevel">See <see cref="SetPowerLevel"/>.</param>
        public bool Initialize(byte[] addressRegister, int powerLevel)
        {
            WriteRegister(SetupAwRegister, 0x03);          // 5-byte addresses
            if (ReadRegister(SetupAwRegister) != 0x03) return false;
            WriteRegister(EnAaRegister, 0x00);             // no auto-ack
            WriteRegister(SetupRetrRegister, 0x00);        // no retransmission
            WriteRegister(DynpdRegister, 0x00);
            WriteRegister(FeatureRegister, 0x00);          // static payload, no ESB control field
            SetPowerLevel(powerLevel);
            _currentAddress = null;
            SetAddress(addressRegister);
            WriteRegister(ConfigRegister, 0x02);           // powered up, TX mode, nRF24 CRC disabled
            return true;
        }

        /// <summary>
        /// 0 to 3 = -18 / -12 / -6 / 0 dBm, at 1 Mbit/s. Keep 0 (-18 dBm) with a PA+LNA module:
        /// louder saturates the receivers at short range.
        /// </summary>
        public void SetPowerLevel(int level)
        {
            if (level < 0) level = 0;
            if (level > 3) level = 3;
            WriteRegister(RfSetupRegister, (byte)(0x01 | (level << 1)));
        }

        /// <summary>
        /// Sets the TX address (5 bytes, register order) to switch from one protocol to another.
        /// Does nothing if this very array is already set. Pass literal arrays (see
        /// <see cref="Protocols.Jm039Protocol.AddressRegister"/>): a null array would write FF FF FF FF FF.
        /// </summary>
        public void SetAddress(byte[] addressRegister)
        {
            if (addressRegister == null || addressRegister == _currentAddress) return;
            Transfer(WriteRegisterCommand | TxAddrRegister, addressRegister, 5);
            _currentAddress = addressRegister;
        }

        /// <summary>
        /// Sends a ready-made packet on a channel and waits until it has left.
        /// <paramref name="packet"/>[0] must be <see cref="TxPayloadCommand"/>, followed by the frame bytes.
        /// Tuned for nanoFramework (interpreted code, where every native call is expensive):
        /// 3 SPI writes, 2 GPIO writes and a few single-byte reads.
        /// </summary>
        public void SendPacket(byte channel, byte[] packet)
        {
            _setChannel[1] = channel;
            _spi.Write(new SpanByte(_setChannel));
            _spi.Write(new SpanByte(ClearStatusFlags));   // the FIFO is empty: the previous frame has left
            _spi.Write(new SpanByte(packet));

            _ce.Write(PinValue.High);                     // a pulse (> 10 µs, always true here) starts sending
            _ce.Write(PinValue.Low);

            // Every SPI command returns STATUS as its first byte: a single-byte read is enough
            for (int i = 0; i < MaxStatusPolls; i++)
            {
                if ((_spi.ReadByte() & TxDataSentFlag) != 0)
                {
                    PacketsSent++;
                    return;
                }
            }

            PacketsFailed++;
            Transfer(FlushTxCommand, null, 0);            // drop the stuck frame
        }

        public byte ReadRegister(byte register)
        {
            Transfer(register, null, 1);
            return _rxBuffer[1];
        }

        public void Dispose()
        {
            _spi.Dispose();
        }

        private void WriteRegister(byte register, byte value)
        {
            _singleByte[0] = value;
            Transfer((byte)(WriteRegisterCommand | register), _singleByte, 1);
        }

        // Sends a command followed by count bytes (0xFF when data is null); the answer lands in _rxBuffer[1..count]
        private void Transfer(int command, byte[] data, int count)
        {
            _txBuffer[0] = (byte)command;
            for (int i = 0; i < count; i++) _txBuffer[i + 1] = data == null ? (byte)0xFF : data[i];
            _spi.TransferFullDuplex(new SpanByte(_txBuffer, 0, count + 1), new SpanByte(_rxBuffer, 0, count + 1));
        }
    }
}
