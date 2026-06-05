using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static V2XController.ProtobufParser;

/**********************************************************************************************************
 * V2X Controller - ProtobufWindow.xaml.cs
 * Author: Michal Švrček
 * Version: 2.4.7
 * Description: Protobuf window logic of the V2X Controller application. Translator for protobuf definitions 
 *              and messages, allowing users to load .proto files, view combined definitions. Generate default 
 *              message structures, and decode protobuf messages from hex or base64 input.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/



namespace V2XController
{
    // placeholder proto message: CJyFAhIGCP3QlskGUiUKBgj90JbJBhIDCIAFogECCBWiAQIIF/IBBAgVUCjyAQQIF1Ao

    public partial class ProtobufWindow : Window
    {
        private ObservableCollection<ProtoFileInfo> _loadedFiles = new ObservableCollection<ProtoFileInfo>();
        private string _cachedDefaultMessage = string.Empty;
        private MessageDirection _currentDirection = MessageDirection.RsuToController;
        private Dictionary<string, List<OneofOption>> _oneofOptions = new Dictionary<string, List<OneofOption>>
        {
            ["RsuToControllerMessageData"] = new List<OneofOption>
    {
        new OneofOption { FieldNumber = 10, Name = "nearby_vehicle_detection", DisplayName = "Nearby Vehicle Detection (field 10)" },
        new OneofOption { FieldNumber = 20, Name = "intersection_request", DisplayName = "Intersection Pass Request (field 20)" },
        new OneofOption { FieldNumber = 30, Name = "heartbeat", DisplayName = "Heartbeat (field 30)" },
        new OneofOption { FieldNumber = 40, Name = "poll_request", DisplayName = "Poll Request (field 40)" }
    },
            ["ControllerToRsuMessageData"] = new List<OneofOption>
    {
        new OneofOption { FieldNumber = 10, Name = "intersection_status", DisplayName = "Intersection Status (field 10)" },
        new OneofOption { FieldNumber = 20, Name = "intersection_pass_request_status", DisplayName = "Pass Request Status (field 20)" },
        new OneofOption { FieldNumber = 30, Name = "empty_response", DisplayName = "Empty Response (field 30)" },
    }
        };
        private OneofOption? _selectedOneofOption = null;

        private List<string> _lastRawInputLines = new();

        private int _currentSearchIndex = -1;
        private List<int> _searchMatches = new List<int>();
        //private Brush _originalBackground;
        private Brush _highlightBrush = new SolidColorBrush(Color.FromRgb(255, 255, 0)); // Yellow

        private System.Windows.Threading.DispatcherTimer _searchDebounceTimer;
        private bool _suppressRadioChange = false;

        public ProtobufWindow()
        {
            InitializeComponent();
            LoadedFilesPanel.ItemsSource = _loadedFiles;

            // Initialize debounce timer for search
            _searchDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(700) // Wait 700ms after last keystroke
            };
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;

            // Subscribe to Loaded event to ensure XAML is fully initialized
            Loaded += ProtobufWindow_Loaded;
        }

        private void ProtobufWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSavedProtoFiles();

            if (AllRadio?.IsChecked == true && TestResultTextBox != null)
            {
                TestResultTextBox.Text = "Auto-detect mode: Paste protobuf data to decode";
                StatusLabel.Content = "Auto-detect mode active";
                StatusLabel.Foreground = Brushes.Blue;
            }
            else
            {
                GenerateAndShowDefaultMessage();
            }
        }

        private void LoadSavedProtoFiles()
        {
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "V2XController",
                    "ProtoFiles"
                );

                if (!Directory.Exists(appDataPath))
                    return;

                var files = Directory.GetFiles(appDataPath, "*.proto");
                foreach (var file in files)
                {
                    try
                    {
                        string content = File.ReadAllText(file, Encoding.UTF8);
                        AddProtoFile(Path.GetFileName(file), content, file);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to load saved proto file {file}: {ex.Message}");
                    }
                }

                UpdateCombinedProtoView();

                if (_loadedFiles.Count > 0)
                {
                    StatusLabel.Content = $"Loaded {_loadedFiles.Count} proto file(s) from saved location";
                    StatusLabel.Foreground = Brushes.Green;
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Failed to load saved protos: {ex.Message}";
                StatusLabel.Foreground = Brushes.Red;
            }
        }

        private static bool TryReadProtoVarint(byte[] bytes, ref int pos, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (pos < bytes.Length)
            {
                byte b = bytes[pos++];
                value |= ((ulong)(b & 0x7F)) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
                if (shift >= 64) return false;
            }
            return false;
        }

        // Parses outer protobuf fields to distinguish RsuToControllerMessageData from ControllerToRsuMessageData.
        // Returns the message type name, or null if undeterminable.
        private static string? DetectOuterMessageTypeFromRawBytes(byte[] bytes)
        {
            int pos = 0;
            int field10ContentOffset = -1;
            int field10ContentLength = -1;

            while (pos < bytes.Length)
            {
                if (!TryReadProtoVarint(bytes, ref pos, out ulong tag)) break;

                int fieldNumber = (int)(tag >> 3);
                int wireType = (int)(tag & 0x7);

                switch (wireType)
                {
                    case 0: // varint
                        if (!TryReadProtoVarint(bytes, ref pos, out _)) goto done;
                        // field 6 = has_more_data (bool) — exclusive to ControllerToRsuMessageData
                        if (fieldNumber == 6) return "ControllerToRsuMessageData";
                        break;

                    case 2: // length-delimited
                        if (!TryReadProtoVarint(bytes, ref pos, out ulong len)) goto done;
                        int contentStart = pos;
                        int contentLen = (int)len;

                        if (fieldNumber == 10)
                        {
                            field10ContentOffset = contentStart;
                            field10ContentLength = contentLen;
                        }
                        else if (fieldNumber == 20)
                        {
                            // intersection_request (RSU→CTRL) or intersection_pass_request_status (CTRL→RSU)
                            // Cannot distinguish here without inner inspection — leave for field10 heuristic
                        }
                        else if (fieldNumber == 30)
                        {
                            // empty_response (CTRL→RSU) is google.protobuf.Empty → length 0
                            // Heartbeat (RSU→CTRL) has content → length > 0
                            return contentLen == 0 ? "ControllerToRsuMessageData" : "RsuToControllerMessageData";
                        }
                        else if (fieldNumber == 40)
                        {
                            // poll_request (RSU→CTRL) is google.protobuf.Empty → always wire 2 len 0
                            return "RsuToControllerMessageData";
                        }

                        if (pos + contentLen > bytes.Length) goto done;
                        pos += contentLen;
                        break;

                    case 1: // 64-bit
                        if (pos + 8 > bytes.Length) goto done;
                        pos += 8;
                        break;

                    case 5: // 32-bit
                        if (pos + 4 > bytes.Length) goto done;
                        pos += 4;
                        break;

                    default:
                        goto done;
                }
            }

        done:
            if (field10ContentOffset < 0 || field10ContentLength <= 0)
                return null;

            // Examine field 10 content to distinguish IntersectionStatus vs NearbyVehicleDetectionInfo
            int innerEnd = field10ContentOffset + field10ContentLength;
            int innerPos = field10ContentOffset;

            while (innerPos < innerEnd)
            {
                if (!TryReadProtoVarint(bytes, ref innerPos, out ulong innerTag)) break;

                int innerField = (int)(innerTag >> 3);
                int innerWire = (int)(innerTag & 0x7);

                switch (innerWire)
                {
                    case 0:
                        if (!TryReadProtoVarint(bytes, ref innerPos, out _)) goto innerDone;
                        break;

                    case 2:
                        if (!TryReadProtoVarint(bytes, ref innerPos, out ulong innerLen)) goto innerDone;

                        // IntersectionStatus field 20 = intersection_lanes (wire 2) — not present in NearbyVehicleDetectionInfo
                        if (innerField == 20) return "ControllerToRsuMessageData";

                        // field 2 content: IntersectionId starts with 0x08 (int32 field 1),
                        //                  VehicleInfo starts with 0x0A (string field 1)
                        if (innerField == 2 && innerLen > 0 && innerPos < bytes.Length)
                        {
                            if (bytes[innerPos] == 0x0A) return "RsuToControllerMessageData"; // string vehicle_id
                            if (bytes[innerPos] == 0x08) return "ControllerToRsuMessageData"; // int32 intersection_id
                        }

                        if (innerPos + (int)innerLen > bytes.Length) goto innerDone;
                        innerPos += (int)innerLen;
                        break;

                    case 5: // 32-bit float
                        if (innerPos + 4 > bytes.Length) goto innerDone;
                        // NearbyVehicleDetectionInfo field 11 = speed, field 12 = heading (both float)
                        if (innerField == 11 || innerField == 12) return "RsuToControllerMessageData";
                        innerPos += 4;
                        break;

                    default:
                        goto innerDone;
                }
            }

        innerDone:
            return null;
        }

        // Checks raw protobuf binary for the message type. Handles both single and empty-payload messages.
        

        private void GenerateAndShowDefaultMessage()
        {
            if (TestResultTextBox == null)
                return;

            if (_loadedFiles.Count == 0)
            {
                TestResultTextBox.Text = "Please load proto files to see default message structure";
                _cachedDefaultMessage = string.Empty;
                StatusLabel.Content = "No proto files loaded";
                StatusLabel.Foreground = Brushes.Orange;
                return;
            }

            try
            {
                var combinedContent = string.Join("\n\n", _loadedFiles.Select(f => f.Content));
                var messages = ProtobufParser.ParseProtoDefinition(combinedContent);

                if (messages.Count == 0)
                {
                    TestResultTextBox.Text = "No messages found in proto files";
                    _cachedDefaultMessage = string.Empty;
                    StatusLabel.Content = "No messages to generate";
                    StatusLabel.Foreground = Brushes.Orange;
                    return;
                }

                string rootMessageName = _currentDirection == MessageDirection.RsuToController
                    ? "RsuToControllerMessageData"
                    : "ControllerToRsuMessageData";

                var rootMessage = messages.FirstOrDefault(m => m.Name == rootMessageName);

                if (rootMessage == null)
                {
                    TestResultTextBox.Text = $"Root message '{rootMessageName}' not found in proto files";
                    _cachedDefaultMessage = string.Empty;
                    StatusLabel.Content = "Root message not found";
                    StatusLabel.Foreground = Brushes.Orange;
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("// ========================================");
                sb.AppendLine($"// Direction: {_currentDirection}");
                sb.AppendLine($"// Root Message: {rootMessage.Name}");

                if (_selectedOneofOption != null)
                {
                    sb.AppendLine($"// Selected: {_selectedOneofOption.DisplayName}");
                }

                sb.AppendLine("// Ready to encode");
                sb.AppendLine("// ========================================");
                sb.AppendLine();
                sb.AppendLine(GenerateDefaultMessageJsonWithOneof(rootMessage, messages, 0));

                var output = sb.ToString();
                _cachedDefaultMessage = output;
                TestResultTextBox.Text = output;

                string statusMsg = _selectedOneofOption != null
                    ? $"{_currentDirection} - {_selectedOneofOption.Name}"
                    : $"{_currentDirection}: Generated {rootMessage.Name}";

                StatusLabel.Content = statusMsg;
                StatusLabel.Foreground = Brushes.Green;
            }
            catch (Exception ex)
            {
                TestResultTextBox.Text = $"Error generating default message:\n{ex.Message}\n\n{ex.StackTrace}";
                _cachedDefaultMessage = string.Empty;
                StatusLabel.Content = "Generation failed";
                StatusLabel.Foreground = Brushes.Red;
            }
        }

        private string GenerateDefaultMessageJsonWithOneof(ProtoMessage message, List<ProtoMessage> allMessages, int indentLevel)
        {
            const int spacesPerIndent = 2;
            string indent = new string(' ', indentLevel * spacesPerIndent);
            string fieldIndent = new string(' ', (indentLevel + 1) * spacesPerIndent);

            var sb = new StringBuilder();
            sb.AppendLine($"{indent}{{");

            foreach (var field in message.Fields)
            {
                // Skip oneof fields that are not selected
                if (_selectedOneofOption != null &&
                    (field.Number == 10 || field.Number == 20 || field.Number == 30 || field.Number == 40))
                {
                    if (field.Number != _selectedOneofOption.FieldNumber)
                    {
                        continue; // Skip this field - it's not the selected oneof option
                    }
                }

                string defaultValue = GetDefaultValueForField(field, allMessages, indentLevel + 1);
                sb.AppendLine($"{fieldIndent}\"{field.Name}\": {defaultValue},");
            }

            if (message.Fields.Count > 0)
            {
                sb.Length -= 3; // Remove trailing ",\n"
                sb.AppendLine();
            }

            sb.Append($"{indent}}}");

            return sb.ToString();
        }

        private void AddProtoFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                DefaultExt = ".proto",
                Filter = "Proto files (*.proto)|*.proto|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                Title = "Load Proto Definition File(s)",
                Multiselect = true
            };

            if (dlg.ShowDialog() == true)
            {
                int successCount = 0;
                int failCount = 0;

                foreach (var fileName in dlg.FileNames)
                {
                    try
                    {
                        string content = File.ReadAllText(fileName, Encoding.UTF8);
                        string shortName = Path.GetFileName(fileName);

                        // Check if already loaded
                        if (_loadedFiles.Any(f => f.FileName == shortName))
                        {
                            var result = MessageBox.Show(
                                $"File '{shortName}' is already loaded. Replace it?",
                                "File Already Loaded",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);

                            if (result == MessageBoxResult.Yes)
                            {
                                var existing = _loadedFiles.First(f => f.FileName == shortName);
                                _loadedFiles.Remove(existing);
                            }
                            else
                            {
                                continue;
                            }
                        }

                        AddProtoFile(shortName, content, fileName);

                        // Save to app data
                        SaveProtoFileToAppData(shortName, content);

                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        failCount++;
                        MessageBox.Show(
                            $"Failed to load {Path.GetFileName(fileName)}:\n{ex.Message}",
                            "Load Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }

                UpdateCombinedProtoView();
                RecompileAllProtos();

                // Regenerate default message with new proto files
                GenerateAndShowDefaultMessage();

                StatusLabel.Content = $"Loaded {successCount} file(s)" + (failCount > 0 ? $", {failCount} failed" : "");
                StatusLabel.Foreground = failCount > 0 ? Brushes.Orange : Brushes.Green;
            }
        }

        private void AddProtoFile(string fileName, string content, string fullPath)
        {
            var fileInfo = new ProtoFileInfo
            {
                FileName = fileName,
                Content = content,
                FilePath = fullPath
            };

            // Parse to get message summary
            var messages = ProtobufParser.ParseProtoDefinition(content);
            if (messages.Count > 0)
            {
                fileInfo.MessageSummary = $"{messages.Count} message(s): {string.Join(", ", messages.Select(m => m.Name).Take(3))}" +
                                         (messages.Count > 3 ? "..." : "");
                fileInfo.Status = "✓ Valid";
                fileInfo.StatusColor = Brushes.Green;
            }
            else
            {
                fileInfo.MessageSummary = "No messages found";
                fileInfo.Status = "⚠ Warning";
                fileInfo.StatusColor = Brushes.Orange;
            }

            _loadedFiles.Add(fileInfo);
        }

        private void SaveProtoFileToAppData(string fileName, string content)
        {
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "V2XController",
                    "ProtoFiles"
                );

                if (!Directory.Exists(appDataPath))
                    Directory.CreateDirectory(appDataPath);

                string filePath = Path.Combine(appDataPath, fileName);
                File.WriteAllText(filePath, content, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to save proto to app data: {ex.Message}");
            }
        }

        private void RemoveFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string filePath)
            {
                var fileToRemove = _loadedFiles.FirstOrDefault(f => f.FilePath == filePath);
                if (fileToRemove != null)
                {
                    _loadedFiles.Remove(fileToRemove);

                    // Remove from app data
                    try
                    {
                        string appDataPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                            "V2XController",
                            "ProtoFiles",
                            fileToRemove.FileName ?? string.Empty
                        );

                        if (File.Exists(appDataPath))
                            File.Delete(appDataPath);
                    }
                    catch { }

                    UpdateCombinedProtoView();
                    RecompileAllProtos();

                    // Regenerate default message
                    GenerateAndShowDefaultMessage();

                    StatusLabel.Content = $"Removed {fileToRemove.FileName}";
                    StatusLabel.Foreground = Brushes.Gray;
                }
            }
        }

        private void ClearAllFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_loadedFiles.Count == 0)
                return;

            var result = MessageBox.Show(
                "Are you sure you want to remove all proto files?",
                "Clear All Files",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _loadedFiles.Clear();
                CombinedProtoTextBox.Clear();

                // Clear app data
                try
                {
                    string appDataPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "V2XController",
                        "ProtoFiles"
                    );

                    if (Directory.Exists(appDataPath))
                    {
                        foreach (var file in Directory.GetFiles(appDataPath))
                            File.Delete(file);
                    }
                }
                catch { }

                ProtobufParser.ClearAllDefinitions();

                _cachedDefaultMessage = string.Empty;
                TestResultTextBox.Clear();

                StatusLabel.Content = "All proto files cleared";
                StatusLabel.Foreground = Brushes.Gray;
            }
        }

        private void UpdateCombinedProtoView()
        {
            var sb = new StringBuilder();

            foreach (var file in _loadedFiles)
            {
                sb.AppendLine($"// ========================================");
                sb.AppendLine($"// File: {file.FileName}");
                sb.AppendLine($"// ========================================");
                sb.AppendLine();
                sb.AppendLine(file.Content);
                sb.AppendLine();
                sb.AppendLine();
            }

            CombinedProtoTextBox.Text = sb.ToString();
        }


        private void RecompileAllProtos()
        {
            var combinedContent = string.Join("\n\n", _loadedFiles.Select(f => f.Content));

            if (!string.IsNullOrWhiteSpace(combinedContent))
            {
                if (ProtobufParser.CompileProtoDefinition(combinedContent, out string error))
                {
                    StatusLabel.Content = $"Successfully compiled {_loadedFiles.Count} proto file(s)";
                    StatusLabel.Foreground = Brushes.Green;
                }
                else
                {
                    StatusLabel.Content = $"Compilation warning: {error}";
                    StatusLabel.Foreground = Brushes.Orange;
                }
            }
        }

        private static bool IsAmbiguousOrEmptyLabel(string label) =>
    string.IsNullOrEmpty(label) || label.Contains('/');

        private static string ExtractTimestampPrefix(string line)
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"^(\d{2}:\d{2}:\d{2}\.\d+)");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static Dictionary<string, Queue<string>> BuildTimestampRawQueue(List<string> rawLines)
        {
            var map = new Dictionary<string, Queue<string>>();
            foreach (var line in rawLines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                string timestamp = string.Empty;
                string rawData = trimmed;

                var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(\d{2}:\d{2}:\d{2}\.\d+)[,\s]+(.+)$");
                if (match.Success)
                {
                    timestamp = match.Groups[1].Value;
                    rawData = match.Groups[2].Value.Trim();
                }

                if (!map.ContainsKey(timestamp))
                    map[timestamp] = new Queue<string>();
                map[timestamp].Enqueue(rawData);
            }
            return map;
        }

        // Checks raw protobuf binary for field 30 (empty_response) or field 40 (poll_request).
        // Field 30 wire type 2 → tag varint: 0xF2 0x01
        // Field 40 wire type 2 → tag varint: 0xC2 0x02
        private static bool TryDetectMessageTypeFromRawBytes(string rawLine, out string label)
        {
            label = string.Empty;
            if (string.IsNullOrWhiteSpace(rawLine))
                return false;

            string data = System.Text.RegularExpressions.Regex.Replace(rawLine.Trim(), @"^<\d+>", "").Trim();

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch
            {
                try
                {
                    if (data.Length % 2 == 0 &&
                        System.Text.RegularExpressions.Regex.IsMatch(data, @"^[0-9A-Fa-f]+$"))
                        bytes = Convert.FromHexString(data);
                    else
                        return false;
                }
                catch { return false; }
            }

            string? detectedType = DetectOuterMessageTypeFromRawBytes(bytes);

            if (detectedType == "ControllerToRsuMessageData")
            {
                bool hasField10 = false, hasField20 = false, hasField30 = false;
                int pos = 0;
                while (pos < bytes.Length)
                {
                    if (!TryReadProtoVarint(bytes, ref pos, out ulong t)) break;
                    int fn = (int)(t >> 3);
                    int wt = (int)(t & 7);
                    if (wt == 2)
                    {
                        if (!TryReadProtoVarint(bytes, ref pos, out ulong l)) break;
                        if (fn == 10) hasField10 = true;
                        else if (fn == 20) hasField20 = true;
                        else if (fn == 30) { hasField30 = true; }
                        if (pos + (int)l > bytes.Length) break;
                        pos += (int)l;
                    }
                    else if (wt == 0) { if (!TryReadProtoVarint(bytes, ref pos, out _)) break; }
                    else if (wt == 1) { if (pos + 8 > bytes.Length) break; pos += 8; }
                    else if (wt == 5) { if (pos + 4 > bytes.Length) break; pos += 4; }
                    else break;
                }

                if (!hasField10 && !hasField20 && !hasField30)
                    return false; // metadata-only, cannot determine payload type

                label = hasField10 ? "CTRL -> RSU (Intersection Status)" :
                        hasField20 ? "CTRL -> RSU (Pass Req Status)" :
                                     "CTRL -> RSU (Empty Response)";
                return true;
            }

            if (detectedType == "RsuToControllerMessageData")
            {
                bool hasField10 = false, hasField20 = false, hasField30 = false, hasField40 = false;
                int pos = 0;
                while (pos < bytes.Length)
                {
                    if (!TryReadProtoVarint(bytes, ref pos, out ulong t)) break;
                    int fn = (int)(t >> 3);
                    int wt = (int)(t & 7);
                    if (wt == 2)
                    {
                        if (!TryReadProtoVarint(bytes, ref pos, out ulong l)) break;
                        if (fn == 10) hasField10 = true;
                        else if (fn == 20) hasField20 = true;
                        else if (fn == 30) hasField30 = true;
                        else if (fn == 40) hasField40 = true;
                        if (pos + (int)l > bytes.Length) break;
                        pos += (int)l;
                    }
                    else if (wt == 0) { if (!TryReadProtoVarint(bytes, ref pos, out _)) break; }
                    else if (wt == 1) { if (pos + 8 > bytes.Length) break; pos += 8; }
                    else if (wt == 5) { if (pos + 4 > bytes.Length) break; pos += 4; }
                    else break;
                }

                if (!hasField10 && !hasField20 && !hasField30 && !hasField40)
                    return false; // metadata-only, cannot determine payload type

                label = hasField10 ? "RSU -> CTRL (Nearby Vehicle)" :
                        hasField20 ? "RSU -> CTRL (Intersection Req)" :
                        hasField30 ? "RSU -> CTRL (Heartbeat)" :
                                     "RSU -> CTRL (Poll Request)";
                return true;
            }

   

            return false;
        }

        private static bool IsMetadataOnlyJson(string json) =>
            !json.Contains("\"nearby_vehicle_detection\"") &&
            !json.Contains("\"intersection_request\"") &&
            !json.Contains("\"heartbeat\"") &&
            !json.Contains("\"poll_request\"") &&
            !json.Contains("\"intersection_status\"") &&
            !json.Contains("\"intersection_pass_request_status\"") &&
            !json.Contains("\"empty_response\"") &&
            !json.Contains("\"has_more_data\"");

        private void TestDecode_Click(object sender, RoutedEventArgs e)
        {
            string input = TestHexTextBox.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(input))
            {
                TestResultTextBox.Text = "Please enter data to test (Hex or Base64)";
                return;
            }

            if (_loadedFiles.Count == 0)
            {
                TestResultTextBox.Text = "Please load proto files first";
                return;
            }

            var inputLines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Where(line => !string.IsNullOrWhiteSpace(line.Trim()))
                                  .ToList();
            _lastRawInputLines = inputLines;


            if (inputLines.Count > 1)
            {
                if (AllRadio != null && AllRadio.IsChecked != true)
                {
                    AllRadio.IsChecked = true;
                    StatusLabel.Content = "Switched to 'All' - multiple messages detected";
                    StatusLabel.Foreground = Brushes.Blue;
                }
            }

            string? forceMessageType = null;
            if (RsuToControllerRadio?.IsChecked == true)
            {
                forceMessageType = "RsuToControllerMessageData";
                Console.WriteLine("[DECODE UI] Forcing RSU->Controller");
            }
            else if (ControllerToRsuRadio?.IsChecked == true)
            {
                forceMessageType = "ControllerToRsuMessageData";
                Console.WriteLine("[DECODE UI] Forcing Controller->RSU");
            }
            else if (AllRadio?.IsChecked == true)
            {
                Console.WriteLine("[DECODE UI] Auto-detect mode");
            }

                        // Pre-decode: detect message type from raw bytes in auto-detect mode for accuracy
            if (forceMessageType == null)
            {
                foreach (var rawLine in inputLines)
                {
                    string strippedLine = rawLine.Trim();

                    var tokenMatch = System.Text.RegularExpressions.Regex.Match(
                        strippedLine,
                        @"^(?:\d{2}:\d{2}:\d{2}\.\d+[,\s]+)?(?:<\d+>)?(\S+)$"
                    );

                    string token = tokenMatch.Success ? tokenMatch.Groups[1].Value : strippedLine;

                    byte[] raw;
                    try
                    {
                        raw = Convert.FromBase64String(token);
                    }
                    catch
                    {
                        try
                        {
                            if (token.Length % 2 == 0 &&
                                System.Text.RegularExpressions.Regex.IsMatch(token, @"^[0-9A-Fa-f]+$"))
                                raw = Convert.FromHexString(token);
                            else
                        continue;
                    }
                        catch { continue; }
                    }

                    string? detectedFromBytes = DetectOuterMessageTypeFromRawBytes(raw);
                    if (detectedFromBytes != null)
                    {
                        forceMessageType = detectedFromBytes;
                        Console.WriteLine($"[DECODE UI] Pre-decode type from bytes: {forceMessageType}");
                        break;
                    }
                    }
                }

            if (ProtobufParser.TryDecodeProtobufFromHex(input, out string decoded, forceMessageType))
            {
                Console.WriteLine($"[DECODE UI] Decode successful, result length: {decoded.Length}");

                decoded = SanitizeDecodedJson(decoded);

                string trimmedResult = decoded.Trim();
                bool isEmpty = trimmedResult == "{}" || trimmedResult == "{ }" || string.IsNullOrWhiteSpace(trimmedResult);

                if (isEmpty && inputLines.Count == 1 && forceMessageType != null)
                {
                    Console.WriteLine("[DECODE UI] Empty result, trying opposite direction");

                    string oppositeType = forceMessageType == "RsuToControllerMessageData"
                        ? "ControllerToRsuMessageData"
                        : "RsuToControllerMessageData";

                    if (ProtobufParser.TryDecodeProtobufFromHex(input, out string retryDecoded, oppositeType))
                    {
                        retryDecoded = SanitizeDecodedJson(retryDecoded);
                        string retryTrimmed = retryDecoded.Trim();
                        bool retryIsEmpty = retryTrimmed == "{}" || retryTrimmed == "{ }" || string.IsNullOrWhiteSpace(retryTrimmed);

                        if (!retryIsEmpty)
                        {
                            Console.WriteLine($"[DECODE UI] Opposite direction successful: {oppositeType}");
                            SwitchToDetectedMessageType(oppositeType);

                            if (TestResultTextBox != null)
                                TestResultTextBox.Text = AnnotateDecodedResult(retryDecoded);

                            StatusLabel.Content = "Decode successful (opposite direction)";
                            StatusLabel.Foreground = Brushes.Green;
                            return;
                        }
                    }
                }

                var (detectedType, _) = DetectDecodedMessageType(decoded);
                Console.WriteLine($"[DECODE UI] Detected type: '{detectedType}'");

                if (AllRadio?.IsChecked != true && !isEmpty && !string.IsNullOrEmpty(detectedType))
                {
                    Console.WriteLine($"[DECODE UI] Switching to detected type: {detectedType}");
                    SwitchToDetectedMessageType(detectedType);
                }

                TestResultTextBox.Text = AnnotateDecodedResult(decoded);

                string directionLabel = forceMessageType == "RsuToControllerMessageData" ? "RSU->Controller" :
                       forceMessageType == "ControllerToRsuMessageData" ? "Controller->RSU" :
                       "Auto-detect";

                StatusLabel.Content = isEmpty
                    ? $"Decode returned empty result ({directionLabel})"
                    : $"Decode successful ({directionLabel})";
                StatusLabel.Foreground = isEmpty ? Brushes.Orange : Brushes.Green;
            }
            else
            {
                Console.WriteLine("[DECODE UI] Decode failed");
                TestResultTextBox.Text = decoded;
                StatusLabel.Content = "Decode failed";
                StatusLabel.Foreground = Brushes.Red;
            }
        }


        private static bool IsLabelCompatibleWithJson(string label, string jsonPart)
        {
            if (string.IsNullOrEmpty(label))
                return true;

            if (IsMetadataOnlyJson(jsonPart))
            {
                // Metadata-only decoded JSON can only have empty-payload labels
                return label.Contains("Poll Request") || label.Contains("Empty Response");
            }

            // For payload-containing JSON, verify the label actually matches the JSON content
            if (jsonPart.Contains("\"nearby_vehicle_detection\"") && !label.Contains("Nearby Vehicle"))
                return false;
            if (jsonPart.Contains("\"heartbeat\"") && !label.Contains("Heartbeat"))
                return false;
            if (jsonPart.Contains("\"intersection_request\"") && !label.Contains("Intersection Req"))
                return false;
            if (jsonPart.Contains("\"intersection_status\"") && !label.Contains("Intersection Status"))
                return false;
            if (jsonPart.Contains("\"intersection_pass_request_status\"") && !label.Contains("Pass Req"))
                return false;
            if (jsonPart.Contains("\"empty_response\"") && !label.Contains("Empty Response"))
                return false;

            return true;
        }

        // Fixes string-valued JSON fields that actually contain raw IEEE 754 float/double bytes
        private string SanitizeDecodedJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return json;

            // Try whole string first (single pretty-printed message)
            try { return SanitizeJsonDocument(json); }
            catch { }

            // Line-by-line — handles "timestamp {...}" and plain "{...}" lines
            var sb = new StringBuilder();
            foreach (var line in json.Split('\n'))
            {
                string jsonPart = ExtractJsonFromLine(line);

                if (jsonPart != null)
                {
                    string prefix = line.Substring(0, line.IndexOf(jsonPart, StringComparison.Ordinal));
                    try { sb.AppendLine(prefix + SanitizeJsonDocument(jsonPart)); }
                    catch { sb.AppendLine(line); }
                }
                else
                {
                    sb.AppendLine(line);
                }
            }
            return sb.ToString().TrimEnd();
        }

        // Returns the JSON substring from a line, or null if none found.
        // Handles both "{...}" lines and "12:56:47.982 {...}" lines.
        private static string? ExtractJsonFromLine(string line)
        {
            int idx = line.IndexOf('{');
            if (idx < 0)
                return null;

            // Everything before { must be whitespace or a timestamp-like prefix (digits, colons, dots, spaces)
            string prefix = line.Substring(0, idx);
            if (!string.IsNullOrWhiteSpace(prefix) &&
                !System.Text.RegularExpressions.Regex.IsMatch(prefix, @"^[\d\s:.\-]+$"))
                return null;

            return line.Substring(idx).Trim();
        }

        // Returns (messageType, shortLabel) — label is e.g. "RSU -> CTRL (Heartbeat)"
        

        private string SanitizeJsonDocument(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var options = new System.Text.Json.JsonWriterOptions { Indented = false };
            using var stream = new MemoryStream();
            using var writer = new System.Text.Json.Utf8JsonWriter(stream, options);
            WriteSanitizedElement(writer, doc.RootElement);
            writer.Flush();
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private void WriteSanitizedElement(System.Text.Json.Utf8JsonWriter writer, System.Text.Json.JsonElement element)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        writer.WritePropertyName(prop.Name);
                        WriteSanitizedElement(writer, prop.Value);
                    }
                    writer.WriteEndObject();
                    break;

                case System.Text.Json.JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        WriteSanitizedElement(writer, item);
                    writer.WriteEndArray();
                    break;

                case System.Text.Json.JsonValueKind.String:
                    var strVal = element.GetString() ?? string.Empty;

                    // If string contains non-printable control characters, it's likely raw binary bytes
                    if (strVal.Any(c => c < 0x20))
                    {
                        var bytes = strVal.Select(c => (byte)(c & 0xFF)).ToArray();

                        if (bytes.Length == 4)
                        {
                            float f = BitConverter.ToSingle(bytes, 0);
                            if (float.IsFinite(f)) { writer.WriteNumberValue(f); break; }
                        }
                        else if (bytes.Length == 8)
                        {
                            double d = BitConverter.ToDouble(bytes, 0);
                            if (double.IsFinite(d)) { writer.WriteNumberValue(d); break; }
                        }
                        // Protobuf tag-prefixed 32-bit float: field 1, wire type 5 → tag byte 0x0D
                        else if (bytes.Length == 5 && bytes[0] == 0x0D)
                        {
                            float f = BitConverter.ToSingle(bytes, 1);
                            if (float.IsFinite(f)) { writer.WriteNumberValue(f); break; }
                        }
                        // Protobuf tag-prefixed 64-bit double: field 1, wire type 1 → tag byte 0x09
                        else if (bytes.Length == 9 && bytes[0] == 0x09)
                        {
                            double d = BitConverter.ToDouble(bytes, 1);
                            if (double.IsFinite(d)) { writer.WriteNumberValue(d); break; }
                        }
                    }

                    writer.WriteStringValue(strVal);
                    break;

                case System.Text.Json.JsonValueKind.Number:
                    writer.WriteRawValue(element.GetRawText());
                    break;

                case System.Text.Json.JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;

                case System.Text.Json.JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;

                case System.Text.Json.JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;
            }
        }


        // Prepends a "// direction (type)" comment before each decoded JSON line.
        // For a single pretty-printed message, annotates the whole block once.
        private string AnnotateDecodedResult(string decoded)
        {
            if (string.IsNullOrWhiteSpace(decoded))
                return decoded;

            var allLines = decoded.Split('\n');
            int jsonLineCount = allLines.Count(l => ExtractJsonFromLine(l.TrimEnd()) != null);

            // Single message (possibly pretty-printed) — annotate once
            if (jsonLineCount <= 1)
            {
                var (_, label) = DetectDecodedMessageType(decoded);

                if (IsAmbiguousOrEmptyLabel(label) && _lastRawInputLines.Count > 0)
                    TryDetectMessageTypeFromRawBytes(_lastRawInputLines[0], out label);

                return string.IsNullOrEmpty(label) ? decoded : $"// {label}\n{decoded}";
            }

            // Multiple one-liner messages — annotate each JSON line individually
            var timestampMap = BuildTimestampRawQueue(_lastRawInputLines);
            int rawLineIndex = 0;

            var sb = new StringBuilder();
            foreach (var line in allLines)
            {
                string trimmed = line.TrimEnd();

                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    sb.AppendLine();
                    continue;
                }

                string? jsonPart = ExtractJsonFromLine(trimmed);
                if (jsonPart != null && jsonPart.Length > 2 && jsonPart != "{}" && jsonPart != "{ }")
                {
                    string label = string.Empty;

                    // Raw bytes detection always takes priority — most accurate source of truth
                    string ts = ExtractTimestampPrefix(trimmed);
                    string? rawData = null;

                    if (!string.IsNullOrEmpty(ts) &&
                        timestampMap.TryGetValue(ts, out var queue) &&
                        queue.Count > 0)
                    {
                        rawData = queue.Dequeue();
                    }
                    else if (rawLineIndex < _lastRawInputLines.Count)
                    {
                        rawData = _lastRawInputLines[rawLineIndex];
                    }

                    if (rawData != null)
                    {
                        TryDetectMessageTypeFromRawBytes(rawData, out label);

                        // Discard label if it's incompatible with the decoded JSON content
                        // (raw bytes and decoded line belong to different messages at same timestamp)
                        if (!IsLabelCompatibleWithJson(label, jsonPart))
                            label = string.Empty;
                    }

                    // Fall back to JSON content detection only when raw bytes gave no result
                    if (string.IsNullOrEmpty(label))
                    {
                        var (_, jsonLabel) = TryDetectSingleJson(jsonPart);
                        label = jsonLabel;
                    }

                    rawLineIndex++;

                    if (!string.IsNullOrEmpty(label))
                        sb.AppendLine($"// {label}");
                }

                sb.AppendLine(trimmed);
            }

            return sb.ToString().TrimEnd();
        }

        private void SwitchToDetectedMessageType(string messageType)
        {
            _suppressRadioChange = true;

            try
            {
                if (messageType == "RsuToControllerMessageData")
                {
                    _currentDirection = MessageDirection.RsuToController;
                    if (RsuToControllerRadio != null)
                        RsuToControllerRadio.IsChecked = true;
                    PopulateMessageTypeDropdown("RsuToControllerMessageData");
                }
                else if (messageType == "ControllerToRsuMessageData")
                {
                    _currentDirection = MessageDirection.ControllerToRsu;
                    if (ControllerToRsuRadio != null)
                        ControllerToRsuRadio.IsChecked = true;
                    PopulateMessageTypeDropdown("ControllerToRsuMessageData");
                }
            }
            finally
            {
                _suppressRadioChange = false;
            }
        }



        private (string messageType, string label) DetectDecodedMessageType(string decodedJson)
        {
            if (string.IsNullOrWhiteSpace(decodedJson))
                return (string.Empty, string.Empty);

            var lines = decodedJson.Split('\n');

            // Count lines that contain a JSON object (with possible timestamp prefix)
            int jsonLines = lines.Count(l => ExtractJsonFromLine(l) != null);

            if (jsonLines <= 1)
            {
                // Single message — try whole string (handles pretty-printed JSON)
                var wholeResult = TryDetectSingleJson(decodedJson.Trim());
                if (!string.IsNullOrEmpty(wholeResult.messageType))
                {
                    Console.WriteLine($"[DETECT TYPE] Matched whole document: {wholeResult.label}");
                    return wholeResult;
                }
            }
            else
            {
                // Multi-message — process line by line, return first conclusive match
                foreach (var line in lines)
                {
                    string? jsonPart = ExtractJsonFromLine(line);
                    if (jsonPart == null)
                        continue;

                    var result = TryDetectSingleJson(jsonPart);
                    if (!string.IsNullOrEmpty(result.messageType))
                    {
                        Console.WriteLine($"[DETECT TYPE] Multi-line match: {result.label}");
                        return result;
                    }
                }
            }

            Console.WriteLine("[DETECT TYPE] No conclusive match found");
            return (string.Empty, string.Empty);
        }

        private (string messageType, string label) TryDetectSingleJson(string json)
        {
            int rsuScore = 0;
            int ctrlScore = 0;
            string rsuLabel = string.Empty;
            string ctrlLabel = string.Empty;

            // RSU -> Controller
            if (json.Contains("\"nearby_vehicle_detection\"")) { rsuScore += 100; if (rsuLabel == string.Empty) rsuLabel = "RSU -> CTRL (Nearby Vehicle)"; }
            if (json.Contains("\"intersection_request\"")) { rsuScore += 100; if (rsuLabel == string.Empty) rsuLabel = "RSU -> CTRL (Intersection Req)"; }
            if (json.Contains("\"heartbeat\"")) { rsuScore += 100; if (rsuLabel == string.Empty) rsuLabel = "RSU -> CTRL (Heartbeat)"; }
            if (json.Contains("\"poll_request\"")) { rsuScore += 100; if (rsuLabel == string.Empty) rsuLabel = "RSU -> CTRL (Poll Request)"; }

            // Controller -> RSU
            if (json.Contains("\"intersection_status\"")) { ctrlScore += 100; if (ctrlLabel == string.Empty) ctrlLabel = "CTRL -> RSU (Intersection Status)"; }
            if (json.Contains("\"intersection_pass_request_status\"")) { ctrlScore += 100; if (ctrlLabel == string.Empty) ctrlLabel = "CTRL -> RSU (Pass Req Status)"; }
            if (json.Contains("\"empty_response\"")) { ctrlScore += 100; if (ctrlLabel == string.Empty) ctrlLabel = "CTRL -> RSU (Empty Response)"; }

            // has_more_data is exclusive to ControllerToRsuMessageData — weak but reliable signal
            if (json.Contains("\"has_more_data\"")) { ctrlScore += 10; if (ctrlLabel == string.Empty) ctrlLabel = "CTRL -> RSU (Empty Response)"; }

            // Metadata-only (crc/timestamp/device_id with no payload field): indeterminate from JSON alone.
            // Raw bytes fallback in AnnotateDecodedResult will resolve these.

            Console.WriteLine($"[DETECT TYPE] Scores — RSU: {rsuScore}, CTRL: {ctrlScore}");

            if (rsuScore > ctrlScore) return ("RsuToControllerMessageData", rsuLabel);
            if (ctrlScore > rsuScore) return ("ControllerToRsuMessageData", ctrlLabel);

            return (string.Empty, string.Empty);
        }

        private void GenerateDefaultMessage_Click(object sender, RoutedEventArgs e)
        {
            GenerateAndShowDefaultMessage();
        }
        private void MessageDirection_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressRadioChange)
                return;

            if (sender is not RadioButton rb || rb.IsChecked != true)
                return;

            // Ensure all radio buttons are initialized
            if (RsuToControllerRadio == null || ControllerToRsuRadio == null || AllRadio == null)
                return;

            // Clear any existing selection/state
            _selectedOneofOption = null;

            if (rb == RsuToControllerRadio)
            {
                _currentDirection = MessageDirection.RsuToController;
                PopulateMessageTypeDropdown("RsuToControllerMessageData");
                StatusLabel.Content = "Direction: RSU → Controller";
                StatusLabel.Foreground = Brushes.Blue;
            }
            else if (rb == ControllerToRsuRadio)
            {
                _currentDirection = MessageDirection.ControllerToRsu;
                PopulateMessageTypeDropdown("ControllerToRsuMessageData");
                StatusLabel.Content = "Direction: Controller → RSU";
                StatusLabel.Foreground = Brushes.Blue;
            }
            else if (rb == AllRadio)
            {
                // "All" mode - disable dropdown and show auto-detect message
                if (MessageTypeComboBox != null)
                {
                    MessageTypeComboBox.ItemsSource = null;
                    MessageTypeComboBox.SelectedItem = null;
                    MessageTypeComboBox.IsEnabled = false;
                }

                if (TestResultTextBox != null)
                {
                    TestResultTextBox.Text = "Auto-detect mode: Paste protobuf data to decode";
                }

                StatusLabel.Content = "Auto-detect mode active";
                StatusLabel.Foreground = Brushes.Blue;
                return; // Don't generate default message in auto-detect mode
            }

            // Generate default message for the selected direction
            GenerateAndShowDefaultMessage();
        }




        private void PopulateMessageTypeDropdown(string messageType)
        {
            if (MessageTypeComboBox == null)
                return;

            MessageTypeComboBox.IsEnabled = true;

            if (_oneofOptions.TryGetValue(messageType, out var options))
            {
                MessageTypeComboBox.ItemsSource = options;
                MessageTypeComboBox.DisplayMemberPath = "DisplayName";
                MessageTypeComboBox.SelectedIndex = 0; // Select first option by default
            }
        }

        private void MessageType_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (MessageTypeComboBox?.SelectedItem is OneofOption selected)
            {
                _selectedOneofOption = selected;
                GenerateAndShowDefaultMessage();
            }
        }


        private List<ProtoMessage> FilterMessagesByDirection(List<ProtoMessage> allMessages, MessageDirection direction)
        {
            var filtered = new List<ProtoMessage>();

            if (direction == MessageDirection.RsuToController)
            {
                // Find RsuToControllerMessageData and all its nested messages
                var rootMessage = allMessages.FirstOrDefault(m => m.Name == "RsuToControllerMessageData");
                if (rootMessage != null)
                {
                    filtered.Add(rootMessage);
                    AddNestedMessages(rootMessage, allMessages, filtered);
                }
            }
            else if (direction == MessageDirection.ControllerToRsu)
            {
                // Find ControllerToRsuMessageData and all its nested messages
                var rootMessage = allMessages.FirstOrDefault(m => m.Name == "ControllerToRsuMessageData");
                if (rootMessage != null)
                {
                    filtered.Add(rootMessage);
                    AddNestedMessages(rootMessage, allMessages, filtered);
                }
            }

            return filtered.Distinct().ToList();
        }

        private void AddNestedMessages(ProtoMessage message, List<ProtoMessage> allMessages, List<ProtoMessage> result)
        {
            foreach (var field in message.Fields)
            {
                var nestedMessage = allMessages.FirstOrDefault(m =>
                    m.Name == field.Type ||
                    (field.Type != null && field.Type.EndsWith("." + m.Name)));

                if (nestedMessage != null && !result.Contains(nestedMessage))
                {
                    result.Add(nestedMessage);
                    AddNestedMessages(nestedMessage, allMessages, result);
                }
            }
        }

        private string GenerateDefaultMessageJson(ProtoMessage message, List<ProtoMessage> allMessages, int indentLevel)
        {
            const int spacesPerIndent = 2;
            string indent = new string(' ', indentLevel * spacesPerIndent);
            string fieldIndent = new string(' ', (indentLevel + 1) * spacesPerIndent);

            var sb = new StringBuilder();
            sb.AppendLine($"{indent}{{");

            foreach (var field in message.Fields)
            {
                string defaultValue = GetDefaultValueForField(field, allMessages, indentLevel + 1);
                sb.AppendLine($"{fieldIndent}\"{field.Name}\": {defaultValue},");
            }

            if (message.Fields.Count > 0)
            {
                sb.Length -= 3; // Remove trailing ",\n"
                sb.AppendLine();
            }

            sb.Append($"{indent}}}");

            return sb.ToString();
        }

        private string GetDefaultValueForField(ProtoField field, List<ProtoMessage> allMessages, int indentLevel)
        {
            // Handle repeated fields
            if (field.IsRepeated)
            {
                // Check if it's a repeated enum - generate array with one default enum value
                if (field != null && field.Type != null && field.Type.Contains("Enum"))
                {
                    string defaultEnumValue = GetDefaultEnumValue(field.Type);
                    return $"[\"{defaultEnumValue}\"]";
                }

                // Check if it's a repeated message - generate array with one default message
                var repeatedMessage = allMessages.FirstOrDefault(m =>
                  field != null && field.Type != null && (m.Name == field.Type || field.Type.EndsWith("." + m.Name)));

                if (repeatedMessage != null)
                {
                    return "[\n" +
                           new string(' ', (indentLevel + 1) * 2) + GenerateDefaultMessageJson(repeatedMessage, allMessages, indentLevel + 1).Replace("\n", "\n" + new string(' ', (indentLevel + 1) * 2)) + "\n" +
                           new string(' ', indentLevel * 2) + "]";
                }

                return "[]";
            }

            // Handle google.protobuf types FIRST
            if (field != null && field.Type != null && field.Type.Contains("google.protobuf"))
            {
                if (field.Type.Contains("Timestamp"))
                {
                    return "{\n" +
                           new string(' ', (indentLevel + 1) * 2) + "\"seconds\": 0,\n" +
                           new string(' ', (indentLevel + 1) * 2) + "\"nanos\": 0\n" +
                           new string(' ', indentLevel * 2) + "}";
                }
                else if (field.Type.Contains("Duration"))
                {
                    return "{\n" +
                           new string(' ', (indentLevel + 1) * 2) + "\"seconds\": 0,\n" +
                           new string(' ', (indentLevel + 1) * 2) + "\"nanos\": 0\n" +
                           new string(' ', indentLevel * 2) + "}";
                }
                else if (field.Type.Contains("StringValue") ||
                         field.Type.Contains("Int32Value") ||
                         field.Type.Contains("Int64Value") ||
                         field.Type.Contains("UInt32Value") ||
                         field.Type.Contains("UInt64Value") ||
                         field.Type.Contains("BoolValue") ||
                         field.Type.Contains("FloatValue") ||
                         field.Type.Contains("DoubleValue") ||
                         field.Type.Contains("BytesValue"))
                {
                    string defaultValue = field.Type.Contains("String") ? "\"\"" :
                                         field.Type.Contains("Bool") ? "false" :
                                         field.Type.Contains("Float") || field.Type.Contains("Double") ? "0.0" : "0";

                    return "{\n" +
                           new string(' ', (indentLevel + 1) * 2) + $"\"value\": {defaultValue}\n" +
                           new string(' ', indentLevel * 2) + "}";
                }
            }

            if (field?.Type == "EmptyResponse" || field?.Name == "empty_response")
            {
                // EmptyResponse is an empty message {}
                return "{}";
            }


            // Handle different field types
            switch (field != null && field.Type != null ? field.Type.ToLower() : string.Empty)
            {
                case "int32":
                case "int64":
                case "uint32":
                case "uint64":
                case "sint32":
                case "sint64":
                case "fixed32":
                case "fixed64":
                case "sfixed32":
                case "sfixed64":
                    return "0";

                case "float":
                case "double":
                    return "0.0";

                case "bool":
                    return "false";

                case "string":
                    return "\"\"";

                case "bytes":
                    return "\"\"";

                default:
                    // Check if it's an enum type
                    if (field != null && field.Type != null && field.Type.Contains("Enum"))
                    {
                        string defaultEnumValue = GetDefaultEnumValue(field.Type);
                        return $"\"{defaultEnumValue}\"";
                    }

                    // Check if it's a nested message type
                    var nestedMessage = allMessages.FirstOrDefault(m =>
                        field != null && field.Type != null && (m.Name == field.Type || field.Type.EndsWith("." + m.Name)));

                    if (nestedMessage != null)
                    {
                        return GenerateDefaultMessageJson(nestedMessage, allMessages, indentLevel);
                    }
                    else
                    {
                        // Unknown type, default to 0
                        return "0";
                    }
            }
        }

        private string GetDefaultEnumValue(string enumType)
        {
            // Extract enum name without package prefix
            string enumName = enumType.Contains(".") ? enumType.Split('.').Last() : enumType;

            // Normalize: remove spaces and convert to uppercase
            string normalizedName = enumName.Replace(" ", "").ToUpper();

            // Return INVALID value for each known enum type
            return normalizedName switch
            {
                "VEHICLETYPEENUM" => "VEHICLE_TYPE_ENUM_INVALID",
                "VEHICLEROLEENUM" => "VEHICLE_ROLE_ENUM_INVALID",
                "PUBLICTRANSPORTVEHICLETYPE" => "PUBLIC_TRANSPORT_VEHICLE_TYPE_INVALID",
                "PUBLICTRANSPORTVEHICLETYPEENUM" => "PUBLIC_TRANSPORT_VEHICLE_TYPE_INVALID",
                "ACCURACYLEVELENUM" => "ACCURACY_LEVEL_ENUM_INVALID",
                "INTERSECTIONPASSREQUESTTYPEENUM" => "INTERSECTION_PASS_REQUEST_TYPE_ENUM_INVALID",
                "INTERSECTIONPASSREQUESTORROLEENUM" => "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_INVALID",
                "INTERSECTIONPASSREQUESTORSUBROLEENUM" => "INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_INVALID",
                "INTERSECTIONPASSREQUESTIMPORTANCEENUM" => "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_INVALID",
                "TRANSITVEHICLEOCCUPANCYENUM" => "TRANSIT_VEHICLE_OCCUPANCY_ENUM_INVALID",
                "INTERSECTIONCONTROLLERSTATUSENUM" => "INTERSECTION_CONTROLLER_STATUS_ENUM_INVALID",
                "TRAFFICLIGHTSIGNALSTATEENUM" => "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_INVALID",
                "INTERSECTIONPASSREQUESTSTATUSENUM" => "INTERSECTION_PASS_REQUEST_STATUS_ENUM_INVALID",

                // Fallback pattern
                _ => normalizedName.EndsWith("ENUM")
                    ? normalizedName.Replace("ENUM", "") + "_INVALID"
                    : normalizedName + "_INVALID"
            };
        }

        private void ClearTest_Click(object sender, RoutedEventArgs e)
        {
            TestHexTextBox.Clear();

            // When clearing decoded message, restore default message
            if (!string.IsNullOrEmpty(_cachedDefaultMessage))
            {
                TestResultTextBox.Text = _cachedDefaultMessage;
                StatusLabel.Content = "Showing default message structures";
                StatusLabel.Foreground = Brushes.Gray;
            }
            else
            {
                TestResultTextBox.Clear();
            }
        }

        public static string GetCombinedProtoDefinition()
        {
            try
            {
                string appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "V2XController",
                    "ProtoFiles"
                );

                if (!Directory.Exists(appDataPath))
                    return string.Empty;

                var files = Directory.GetFiles(appDataPath, "*.proto");
                var sb = new StringBuilder();

                foreach (var file in files)
                {
                    try
                    {
                        sb.AppendLine(File.ReadAllText(file, Encoding.UTF8));
                        sb.AppendLine();
                    }
                    catch { }
                }

                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private void EncodeToHex_Click(object sender, RoutedEventArgs e)
        {
            string jsonInput = TestResultTextBox.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(jsonInput))
            {
                MessageBox.Show("Please enter JSON data to encode", "No Input",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_loadedFiles.Count == 0)
            {
                MessageBox.Show("Please load proto files first", "No Proto Files",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check if "All (Auto-detect)" is selected
            if (AllRadio?.IsChecked == true)
            {
                MessageBox.Show(
                    "Cannot encode with 'All (Auto-detect)' mode.\n\n" +
                    "Please select specific message direction:\n" +
                    "RSU -> Controller\n" +
                    "Controller -> RSU",
                    "Encoding Not Supported",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            try
            {
                // Ensure proto definitions are compiled
                var combinedContent = string.Join("\n\n", _loadedFiles.Select(f => f.Content));
                if (!ProtobufParser.CompileProtoDefinition(combinedContent, out string compileError))
                {
                    MessageBox.Show($"Failed to compile proto definitions:\n{compileError}",
                        "Compilation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Remove comment lines that start with //
                var lines = jsonInput.Split('\n');
                var jsonLines = lines.Where(line => !line.TrimStart().StartsWith("//"));
                string cleanJson = string.Join("\n", jsonLines).Trim();

                if (string.IsNullOrWhiteSpace(cleanJson))
                {
                    MessageBox.Show("No valid JSON found after removing comments", "No JSON",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Determine message type based on current direction
                string messageTypeName = _currentDirection == MessageDirection.RsuToController
                    ? "RsuToControllerMessageData"
                    : "ControllerToRsuMessageData";

                // Get selected format from ComboBox
                bool useBase64 = OutputFormatComboBox?.SelectedIndex == 1; // 0=Hex, 1=Base64

                // Try to encode the JSON to protobuf with explicit message type
                if (ProtobufParser.TryEncodeJsonToProtobufWithType(cleanJson, messageTypeName, useBase64, out string output, out string errorMessage))
                {
                    // Success - put the output in the input field
                    TestHexTextBox.Text = output;

                    string formatName = useBase64 ? "Base64" : "Hex";
                    int byteCount = useBase64 ? Convert.FromBase64String(output).Length : output.Length / 2;

                    StatusLabel.Content = $"Encoding successful ({byteCount} bytes, {formatName} format)";
                    StatusLabel.Foreground = Brushes.Green;
                }
                else
                {
                    // Failed - show reason in status bar and message box
                    StatusLabel.Content = $"Encoding failed: {errorMessage}";
                    StatusLabel.Foreground = Brushes.Red;

                    MessageBox.Show(
                        $"Encoding failed:\n\n{errorMessage}",
                        "Encoding Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Content = $"Encoding error: {ex.Message}";
                StatusLabel.Foreground = Brushes.Red;
            }
        }

        private void CopyResult_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TestResultTextBox.Text))
            {
                try
                {
                    Clipboard.SetText(TestResultTextBox.Text);
                    StatusLabel.Content = "Copied to clipboard";
                    StatusLabel.Foreground = Brushes.Green;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to copy to clipboard:\n{ex.Message}",
                        "Copy Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Stop existing timer
            _searchDebounceTimer.Stop();

            if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                CombinedProtoTextBox.SelectionStart = 0;
                CombinedProtoTextBox.SelectionLength = 0;
                SearchResultLabel.Text = "";
                _searchMatches.Clear();
                _currentSearchIndex = -1;
                return;
            }

            SearchResultLabel.Text = "Searching... (Ctrl+F to edit search)";
            SearchResultLabel.Foreground = Brushes.Gray;

            // Start debounce timer - will trigger search after 300ms
            _searchDebounceTimer.Start();
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SearchNext_Click(null, null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                SearchTextBox.Clear();
                CombinedProtoTextBox.Focus();
                e.Handled = true;
            }
        }

        private void PerformSearchInternal(string searchTerm)
        {
            _searchMatches.Clear();
            _currentSearchIndex = -1;

            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                SearchResultLabel.Text = "";
                return;
            }

            string text = CombinedProtoTextBox.Text;
            int index = 0;

            // Simple string search
            while ((index = text.IndexOf(searchTerm, index, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                _searchMatches.Add(index);
                index += searchTerm.Length;
            }

            if (_searchMatches.Count > 0)
            {
                _currentSearchIndex = 0;
                HighlightCurrentMatch();
            }
            else
            {
                SearchResultLabel.Text = "No matches";
                SearchResultLabel.Foreground = Brushes.Gray;
            }
        }



        private void SearchNext_Click(object? sender, RoutedEventArgs? e)
        {
            if (_searchMatches.Count == 0)
                return;

            _currentSearchIndex = (_currentSearchIndex + 1) % _searchMatches.Count;
            HighlightCurrentMatch();
        }

        private void SearchPrevious_Click(object? sender, RoutedEventArgs? e)
        {
            if (_searchMatches.Count == 0)
                return;

            _currentSearchIndex = (_currentSearchIndex - 1 + _searchMatches.Count) % _searchMatches.Count;
            HighlightCurrentMatch();
        }


        private void HighlightCurrentMatch()
        {
            if (_searchMatches.Count == 0 || _currentSearchIndex < 0 || string.IsNullOrEmpty(SearchTextBox.Text))
                return;

            int matchIndex = _searchMatches[_currentSearchIndex];
            int searchLength = SearchTextBox.Text.Length;

            // Take focus for F3/button navigation
            CombinedProtoTextBox.Focus();
            CombinedProtoTextBox.SelectionStart = matchIndex;
            CombinedProtoTextBox.SelectionLength = searchLength;

            // Scroll
            int lineNumber = CombinedProtoTextBox.GetLineIndexFromCharacterIndex(matchIndex);
            CombinedProtoTextBox.ScrollToLine(Math.Max(0, lineNumber - 5));

            SearchResultLabel.Text = $"{_currentSearchIndex + 1} of {_searchMatches.Count}";
            SearchResultLabel.Foreground = Brushes.Green;
        }

        /*private void HighlightMatch()
        {
            if (_searchMatches.Count == 0 || _currentSearchIndex < 0)
                return;

            int matchIndex = _searchMatches[_currentSearchIndex];

            // Set selection
            CombinedProtoTextBox.SelectionStart = matchIndex;
            CombinedProtoTextBox.SelectionLength = SearchTextBox.Text.Length;

            // Scroll to line
            int lineNumber = CombinedProtoTextBox.GetLineIndexFromCharacterIndex(matchIndex);
            CombinedProtoTextBox.ScrollToLine(Math.Max(0, lineNumber - 5));

            SearchResultLabel.Text = $"{_currentSearchIndex + 1} of {_searchMatches.Count}";
            SearchResultLabel.Foreground = Brushes.Green;
        }*/

        private void CombinedProtoTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+F to focus search
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchTextBox.Focus();
                SearchTextBox.SelectAll();
                e.Handled = true;
            }
            // F3 to find next
            else if (e.Key == Key.F3 && Keyboard.Modifiers == ModifierKeys.None)
            {
                SearchNext_Click(null, null);
                e.Handled = true;
            }

            else if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None)
            {
                SearchPrevious_Click(null, null);
                e.Handled = true;
            }
        }

        private async void SearchDebounceTimer_Tick(object? sender, EventArgs e)
        {
            _searchDebounceTimer.Stop();

            if (!string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                await PerformSearchAsync(SearchTextBox.Text);
            }
        }

        private async Task PerformSearchAsync(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return;

            // READ TEXT ON UI THREAD FIRST!
            string textToSearch = CombinedProtoTextBox.Text;

            // Run search on background thread
            var matches = await Task.Run(() =>
            {
                var results = new List<int>();
                int index = 0;

                while ((index = textToSearch.IndexOf(searchTerm, index, StringComparison.OrdinalIgnoreCase)) != -1)
                {
                    results.Add(index);
                    index += searchTerm.Length;
                }

                return results;
            });

            // Update UI on main thread (we're back on UI thread after await)
            _searchMatches = matches;

            if (_searchMatches.Count > 0)
            {
                _currentSearchIndex = 0;
                HighlightCurrentMatchWithoutFocus();
            }
            else
            {
                SearchResultLabel.Text = "No matches";
                SearchResultLabel.Foreground = Brushes.Gray;
            }
        }

        private void HighlightCurrentMatchWithoutFocus()
        {
            if (_searchMatches.Count == 0 || _currentSearchIndex < 0 || string.IsNullOrEmpty(SearchTextBox.Text))
                return;

            int matchIndex = _searchMatches[_currentSearchIndex];
            int searchLength = SearchTextBox.Text.Length;

            // Keep focus in TextBox - selection stays visible!
            CombinedProtoTextBox.Focus();
            CombinedProtoTextBox.SelectionStart = matchIndex;
            CombinedProtoTextBox.SelectionLength = searchLength;

            // Scroll
            int lineNumber = CombinedProtoTextBox.GetLineIndexFromCharacterIndex(matchIndex);
            CombinedProtoTextBox.ScrollToLine(Math.Max(0, lineNumber - 5));

            SearchResultLabel.Text = $"{_currentSearchIndex + 1} of {_searchMatches.Count}";
            SearchResultLabel.Foreground = Brushes.Green;

            // DON'T return focus - user can press Ctrl+F to continue typing
        }
    }

    public enum MessageDirection
    {
        RsuToController,
        ControllerToRsu
    }

    public class ProtoFileInfo
    {
        public string? FileName { get; set; }
        public string? Content { get; set; }
        public string? FilePath { get; set; }
        public string? MessageSummary { get; set; }
        public string? Status { get; set; }
        public Brush? StatusColor { get; set; }
    }

    public class OneofOption
    {
        public int FieldNumber { get; set; }
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
    }
}