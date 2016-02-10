using ScpControl.Shared.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ScpControl.Bluetooth {
    public class BthDongleSerial : BthDongle {
        private const int HCI_COMMAND_PKT = 0x01;
        private const int HCI_ACLDATA_PKT = 0x02;
        private const int HCI_EVENT_PKT = 0x04;

        private SerialPort _serialPort = null;
        private BlockingCollection<byte[]> _evtPackets = new BlockingCollection<byte[]>();
        private BlockingCollection<byte[]> _aclPackets = new BlockingCollection<byte[]>();
        private CancellationTokenSource _cancel;

        public override bool Open(string devicePath) {
            if (_serialPort == null) {
                try {
                    _serialPort = new SerialPort(devicePath);
                    _serialPort.DataBits = 8;
                    _serialPort.Parity = Parity.None;
                    _serialPort.Open();
                    Log.DebugFormat("Opened serial port [{0}]", devicePath);
                } catch (Exception) {
                    _serialPort = null;
                    return false;
                }

                _cancel = new CancellationTokenSource();
                new Task(SerialReadTask).Start();

                IsActive = true;
            }
            return true;
        }

        public override bool Close() {
            if (_serialPort != null) {
                _cancel.Cancel();
                _cancel = null;

                _serialPort.Close();
                _serialPort = null;
            }
            return true;
        }

        private void SerialReadTask() {
            Log.Debug("++ SerialReadTask start");
            Stream stream = _serialPort.BaseStream;
            CancellationToken token = _cancel.Token;
            while (!token.IsCancellationRequested) {
                byte[] packet = ReadHCIPacket(stream);
                if (packet == null) break;
                byte packetType = packet[0];
                Array.Copy(packet, 1, packet, 0, packet.Length - 1);
                Array.Resize(ref packet, packet.Length - 1);
                switch (packetType) {
                    case HCI_EVENT_PKT:
                        _evtPackets.Add(packet);
                        break;
                    case HCI_ACLDATA_PKT:
                        _aclPackets.Add(packet);
                        break;
                }
            }
            Log.Debug("-- SerialReadTask end");
        }

        protected override bool ReadIntPipe(byte[] buffer, int length, ref int transfered) {
            byte[] packet = _evtPackets.Take();
            transfered = Math.Min(length, packet.Length);
            Array.Copy(packet, buffer, transfered);
            return true;
        }

        protected override bool ReadBulkPipe(byte[] buffer, int length, ref int transfered) {
            byte[] packet = _aclPackets.Take();
            transfered = Math.Min(length, packet.Length);
            Array.Copy(packet, buffer, transfered);
            return true;
        }

        protected override bool WriteBulkPipe(byte[] buffer, int length, ref int transfered) {
            byte[] header = { HCI_ACLDATA_PKT };
            lock (_serialPort) {
                _serialPort.Write(header, 0, 1);
                _serialPort.Write(buffer, 0, length);
            }
            transfered = length;
            return true;
        }

        protected override bool SendTransfer(byte requestType, byte request, ushort value, byte[] buffer, ref int transfered) {
            byte[] header = { HCI_COMMAND_PKT };
            lock (_serialPort) {
                _serialPort.Write(header, 0, 1);
                _serialPort.Write(buffer, 0, buffer.Length);
            }
            transfered = buffer.Length;
            return true;
        }

        private static byte[] ReadHCIPacket(Stream stream) {
            int bytesBeforeLength = 2;
            int lengthBytes = 1;

            int packetType = stream.ReadByte();

            switch (packetType) {
                case HCI_ACLDATA_PKT:
                    lengthBytes = 2;
                    break;
                case HCI_EVENT_PKT:
                    bytesBeforeLength = 1;
                    break;
                case -1:
                    return null;
            }

            byte[] packet = new byte[1 + bytesBeforeLength + lengthBytes];
            packet[0] = (byte)packetType;
            int packetLength = 0;

            for (int i = 0; i < bytesBeforeLength + lengthBytes; i++) {
                int b = stream.ReadByte();
                if (b == -1) return null;
                packet[i + 1] = (byte)b;
                if (i >= bytesBeforeLength)
                    packetLength |= b << ((i - bytesBeforeLength) * 8);
            }

            int headerLength = packet.Length;
            Array.Resize(ref packet, headerLength + packetLength);

            int completed = 0;
            while (completed < packetLength) {
                int read = stream.Read(packet, headerLength + completed, packetLength - completed);
                if (read == 0) return null;
                completed += read;
            }

            return packet;
        }
    }
}
