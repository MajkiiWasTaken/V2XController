using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;

namespace V2XController
{
    // Serial/TCP transport and incoming line routing
    public partial class MainWindow
    {
        // ===== V2X MESSAGE METHODS =====

        private Task StartSerialConnectionAsync(
    string portName,
    int baudRate,
    CancellationToken cancellationToken)
        {
            serialPort = new SerialPort(portName, baudRate)
            {
                NewLine = "\r\n",
                Encoding = Encoding.ASCII,
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };

            serialPort.Open();
            Console.WriteLine(
                $"[SERIAL] Opened {portName}, baud={baudRate}, " +
                $"newline={BitConverter.ToString(Encoding.ASCII.GetBytes(serialPort.NewLine))}");

            _ = Task.Run(
                () => SerialReceiveLoopAsync(cancellationToken),
                cancellationToken);

            StartAutomaticSrvIfRequired();

            return Task.CompletedTask;
        }

        private async Task SerialReceiveLoopAsync(
            CancellationToken cancellationToken)
        {
            if (serialPort == null)
                return;

            while (!cancellationToken.IsCancellationRequested &&
                   serialPort.IsOpen)
            {
                try
                {
                    string line = await ReadLineAsync(serialPort)
                        .ConfigureAwait(false);

                    Console.WriteLine($"[SERIAL RX RAW] len={line.Length}: {line}");

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        await ProcessReceivedLineAsync(line)
                            .ConfigureAwait(false);
                    }
                }
                catch (TimeoutException)
                {
                    // Pouze pokračujeme a zkontrolujeme cancellation token.
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SERIAL RX ERR] {ex.Message}");
                    break;
                }
            }
        }

        private async Task StartEthernetConnectionAsync(
    string host,
    int port,
    CancellationToken cancellationToken)
        {
            _tcpClient = new TcpClient
            {
                NoDelay = true
            };

            await _tcpClient.ConnectAsync(
                host,
                port,
                cancellationToken);

            Console.WriteLine($"[TCP] Connected to {host}:{port}");

            _tcpStream = _tcpClient.GetStream();

            _tcpReader = new StreamReader(
                _tcpStream,
                Encoding.ASCII,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 4096,
                leaveOpen: true);

            _tcpWriter = new StreamWriter(
                _tcpStream,
                Encoding.ASCII,
                bufferSize: 4096,
                leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true
            };

            _ = Task.Run(
                () => EthernetReceiveLoopAsync(cancellationToken),
                cancellationToken);

            StartAutomaticSrvIfRequired();
        }

        private async Task EthernetReceiveLoopAsync(
            CancellationToken cancellationToken)
        {
            if (_tcpReader == null)
                return;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line = await _tcpReader.ReadLineAsync(
                        cancellationToken);

                    Console.WriteLine(
                        $"[TCP RX RAW] len={line?.Length ?? 0}: {line ?? "<null>"}");

                    if (line == null)
                    {
                        Console.WriteLine("[TCP] Remote device closed connection.");
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        await ProcessReceivedLineAsync(line)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normální ukončení.
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[TCP RX ERR] {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TCP RX ERR] {ex}");
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_isConnected &&
                    _connectionType == ConnectionType.Ethernet)
                {
                    _isConnected = false;
                    UpdateUiEnabledState();

                    MessageBox.Show(
                        "Ethernet connection was closed by the remote device.");
                }
            });
        }

        private void StartAutomaticSrvIfRequired()
        {
            Dispatcher.Invoke(() =>
            {
                if (SrvCheckBox?.IsChecked == true)
                {
                    SendSrvMessage();
                    StartSrvAutoTimerIfEnabled();
                }
            });
        }

        private Task ProcessReceivedLineAsync(string rawLine)
        {
            Console.WriteLine(
        $"[PROCESS RX] len={rawLine?.Length ?? 0}: {rawLine}");

            if (string.IsNullOrWhiteSpace(rawLine))
                return Task.CompletedTask;

            // =====================================================================
            // PROTOBUF MESSAGE DETECTION AND HANDLING
            // =====================================================================
            if (IsProtobufMessage(rawLine))
            {
                if (_timeshiftEnabled)
                    AddRecordedCamMessage(rawLine.Trim());

                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (ProtobufParser.TryDecodeProtobufFromHex(
                                rawLine.Trim(),
                                out string decoded))
                        {
                            HandleProtobufMessage(decoded);

                            Console.WriteLine(
                                $"[PROTO] Received and decoded Protobuf message " +
                                $"({rawLine.Length} chars)",
                                Brushes.Cyan);
                        }
                        else
                        {
                            Console.WriteLine(
                                "[PROTO] Failed to decode Protobuf message",
                                Brushes.Orange);

                            IncrementCamErrorCount();
                        }
                    }
                    catch (Exception protoEx)
                    {
                        Console.WriteLine(
                            $"[PROTO] Error processing Protobuf: {protoEx.Message}",
                            Brushes.Red);

                        IncrementCamErrorCount();
                    }
                });

                return Task.CompletedTask;
            }

            // =====================================================================
            // XML MESSAGE HANDLING (CAM/SRV)
            // =====================================================================

            if (_isPlaybackSessionActive || _isTimeshiftPlaybackActive)
                return Task.CompletedTask;

            int xmlStart = rawLine.IndexOf('<');

            if (xmlStart < 0)
                return Task.CompletedTask;

            string rawXml = rawLine.Substring(xmlStart);

            bool wasLocalEcho = false;

            lock (_recentLocalWritesLock)
            {
                int idx = _recentLocalWrites.FindIndex(s => s == rawXml);

                if (idx >= 0)
                {
                    _recentLocalWrites.RemoveAt(idx);
                    wasLocalEcho = true;
                }
            }

            if (wasLocalEcho)
                return Task.CompletedTask;

            try
            {
                var msg = V2XMessageParser.ParseV2XMessage(rawXml);

                if (msg.MessageType == "CAM")
                {
                    bool valid = IsValidCamMessage(rawXml);

                    if (_timeshiftEnabled &&
                        valid &&
                        !(msg.VehicleID?.StartsWith("000000") ?? false))
                    {
                        AddCamToBuffer(rawXml);
                    }

                    Dispatcher.Invoke(() =>
                    {
                        var shortId = string.IsNullOrEmpty(msg.VehicleID)
                            ? "-"
                            : msg.VehicleID.Length > 4
                                ? msg.VehicleID[^4..]
                                : msg.VehicleID;

                        var crcTxt = valid ? "CRC OK" : "CRC ERR";

                        if (valid)
                        {
                            IncrementCamOkCount();
                        }
                        else
                        {
                            IncrementCamErrorCount();
                        }

                        Console.WriteLine(
                            $"[RX][CAM] ID={shortId}, {crcTxt}");
                    });
                }
                else if (msg.MessageType == "SRV")
                {
                    if (_timeshiftEnabled)
                        AddSrvToBuffer(rawXml);
                }

                if (_timeshiftEnabled && _timeshiftPaused)
                    return Task.CompletedTask;

                Dispatcher.Invoke(() =>
                {
                    HandleV2XMessage(msg, rawXml);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    Console.WriteLine(
                        $"[RX PARSE ERR] {ex.Message}",
                        Brushes.Red);

                    IncrementCamErrorCount();
                });
            }

            return Task.CompletedTask;
        }

        private bool IsTransportConnected()
        {
            return _connectionType switch
            {
                ConnectionType.Serial =>
                    serialPort?.IsOpen == true,

                ConnectionType.Ethernet =>
                    _tcpClient?.Connected == true &&
                    _tcpStream != null &&
                    _tcpWriter != null,

                _ => false
            };
        }

        private void SendTransportLine(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            lock (_connectionWriteLock)
            {
                switch (_connectionType)
                {
                    case ConnectionType.Serial:
                        {
                            if (serialPort?.IsOpen != true)
                                throw new InvalidOperationException(
                                    "Serial port is not open.");

                            serialPort.Write(message);
                            serialPort.Write(serialPort.NewLine);
                            break;
                        }

                    case ConnectionType.Ethernet:
                        {
                            if (_tcpWriter == null ||
                                _tcpClient?.Connected != true)
                            {
                                throw new InvalidOperationException(
                                    "Ethernet connection is not open.");
                            }

                            _tcpWriter.WriteLine(message);
                            _tcpWriter.Flush();
                            break;
                        }

                    default:
                        throw new InvalidOperationException(
                            "Unknown connection type.");
                }
            }
        }

        ////V2X Listener !!!!
        ///// <summary>
        ///// Starts the V2X listener on the specified port and baud rate.
        ///// </summary>
        ///// <param name="portName">The name of the serial port.</param>
        ///// <param name="baudRate">The baud rate for the serial port.</param>
        ///// <returns>A task representing the asynchronous operation.</returns>
        //private Task StartV2XListenerAsync(string portName, int baudRate)
        //{
        //    serialPort = new SerialPort(portName, baudRate)
        //    {
        //        NewLine = "\r\n",
        //        Encoding = Encoding.ASCII
        //    };
        //    serialPort.Open();

        //    Dispatcher.Invoke(() =>
        //    {
        //        if (SrvCheckBox?.IsChecked == true)
        //        {
        //            SendSrvMessage();
        //            StartSrvAutoTimerIfEnabled();
        //        }
        //    });

        //    _ = Task.Run(async () =>
        //    {
        //        try
        //        {
        //            while (serialPort.IsOpen)
        //            {
        //                string rawLine;
        //                try { rawLine = await ReadLineAsync(serialPort).ConfigureAwait(false); }
        //                catch { break; }

        //            }
        //        }
        //        catch (Exception loopEx)
        //        {
        //            Dispatcher.Invoke(() => Console.WriteLine($"[SERIAL] Serial listen loop error: {loopEx.Message}"));
        //        }
        //    });

        //    return Task.CompletedTask;
        //}

        /// <summary>
        /// Asynchronously reads a line from the specified serial port.
        /// </summary>
        /// <param name="port">The serial port to read from.</param>
        /// <returns>A task representing the asynchronous operation, with the read line as the result.</returns>
        private Task<string> ReadLineAsync(SerialPort port)
        {
            return Task.Run(() => port.ReadLine());
        }

        /// <summary>
        /// Handling logic for V2X messages, including filtering based on UI settings, parsing accuracy, and updating the map display.
        /// </summary>
        /// <param name="msg">The V2X message to handle.</param>
        /// <param name="rawXml">The raw XML representation of the V2X message.</param>
    }
}
