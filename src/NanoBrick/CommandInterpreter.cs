using System;
using NanoBrick.Hardware;
using NanoBrick.Protocols;
using NanoBrick.Transmission;

namespace NanoBrick
{
    /// <summary>
    /// Turns one text command (one line, see Program.cs for the list) into transmitter calls,
    /// and returns the answer to send back: "OK …", "FULL …" when a queue overflows, "? …" when unknown.
    /// </summary>
    internal sealed class CommandInterpreter
    {
        private const int DefaultSteps = 1;
        private const int DefaultButtonTenths = 3;

        private const string Help =
            "? commands: [1-4]a+ a- b+ b- [n] | [1-4]sa sb [n] | [1-4]w <n> | x | k 1|0 | m <1234> | c 1-4 | p 0-3 | f | h... (battery box)";
        private const string BatteryBoxHelp =
            "? battery box: ha+ ha- hb+ hb- hc+ hc- hd+ hd- [n] | hs1 hs2 [n] | hx [a-d]";

        private readonly Transmitter _transmitter;
        private readonly Nrf24Radio _radio;
        private int _currentPosition = 1;

        public CommandInterpreter(Transmitter transmitter, Nrf24Radio radio)
        {
            _transmitter = transmitter;
            _radio = radio;
        }

        /// <summary>Runs a command and returns the answer.</summary>
        public string Execute(string command)
        {
            // A leading digit (1 to 4) selects the receiver; without it, the current position (command c)
            int position = _currentPosition;
            if (command.Length > 1 && command[0] >= '1' && command[0] <= '4' && command[1] >= 'a' && command[1] <= 'z')
            {
                position = command[0] - '0';
                command = command.Substring(1);
            }

            if (command[0] == 'h') return ExecuteBatteryBox(command);

            string[] words = command.Split(' ');
            string name = words[0];
            bool isButton = name == "sa" || name == "sb";
            int count = words.Length > 1 ? ParseInt(words[1]) : (isButton ? DefaultButtonTenths : DefaultSteps);
            if (count < 0) count = 0;

            bool accepted = true;
            switch (name)
            {
                case "a+": accepted = _transmitter.QueueSteps(position, 0, Jm039Command.LeftForward, count); break;
                case "a-": accepted = _transmitter.QueueSteps(position, 0, Jm039Command.LeftBackward, count); break;
                case "b+": accepted = _transmitter.QueueSteps(position, 1, Jm039Command.RightForward, count); break;
                case "b-": accepted = _transmitter.QueueSteps(position, 1, Jm039Command.RightBackward, count); break;
                case "sa": accepted = _transmitter.Stop(position, 0, count); break;
                case "sb": accepted = _transmitter.Stop(position, 1, count); break;
                case "w": accepted = _transmitter.Wait(position, count); break;
                case "x": _transmitter.StopAll(); break;
                case "k":
                    _transmitter.SetKeepAlive(count != 0);
                    return "OK k " + count;
                case "m": _transmitter.ServedPositions = ParsePositionMask(words.Length > 1 ? words[1] : string.Empty); break;
                case "c": _currentPosition = count >= 1 && count <= Jm039Protocol.PositionCount ? count : 1; break;
                case "p": _radio.SetPowerLevel(count); break;
                case "f":
                    return "STATUS position " + _currentPosition
                           + ", max round " + _transmitter.ReadAndResetMaxRoundMs()
                           + " ms, packets sent " + _radio.PacketsSent + ", failed " + _radio.PacketsFailed;
                default:
                    return Help;
            }

            _transmitter.ExtendKeepAlive();
            return (accepted ? "OK " : "FULL ") + position + command;
        }

        // Battery box commands: "ha+ 5", "hs1", "hx", "hx b"
        private string ExecuteBatteryBox(string command)
        {
            string[] words = command.Split(' ');
            string name = words[0];
            int tenths = words.Length > 1 ? ParseInt(words[1]) : DefaultButtonTenths;
            if (tenths < 1) tenths = 1;

            if (name.Length == 3 && IsOutputLetter(name[1]) && (name[2] == '+' || name[2] == '-'))
            {
                _transmitter.RunBatteryBoxOutput(name[1] - 'a', name[2] == '+', tenths);
            }
            else if (name == "hs1" || name == "hs2")
            {
                _transmitter.PressBatteryBoxSquare(name[2] - '0', tenths);
            }
            else if (name == "hx")
            {
                string output = words.Length > 1 ? words[1].Trim() : string.Empty;
                _transmitter.StopBatteryBox(output.Length == 1 && IsOutputLetter(output[0]) ? output[0] - 'a' : Transmitter.AllOutputs);
            }
            else
            {
                return BatteryBoxHelp;
            }

            return "OK " + command;
        }

        private static bool IsOutputLetter(char c)
        {
            return c >= 'a' && c <= 'd';
        }

        // "13" -> positions 1 and 3 (bit 0 = position 1)
        private static int ParsePositionMask(string text)
        {
            int mask = 0;
            foreach (char c in text)
            {
                if (c >= '1' && c <= '4') mask |= 1 << (c - '1');
            }
            return mask;
        }

        private static int ParseInt(string text)
        {
            try
            {
                return int.Parse(text.Trim());
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
