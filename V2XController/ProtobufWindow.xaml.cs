using Google.Protobuf.Reflection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Windows.UI.Xaml.Hosting;
using static V2XController.ProtobufParser;
using System.Windows.Documents;
using System.Threading.Tasks;

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
        private OneofOption _selectedOneofOption = null;

        private int _currentSearchIndex = -1;
        private List<int> _searchMatches = new List<int>();
        private Brush _originalBackground;
        private Brush _highlightBrush = new SolidColorBrush(Color.FromRgb(255, 255, 0)); // Yellow

        private System.Windows.Threading.DispatcherTimer _searchDebounceTimer;


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
                            fileToRemove.FileName
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
        // Replace TestDecode_Click method (around line 1700)
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

            // Check if input contains multiple lines
            var inputLines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Where(line => !string.IsNullOrWhiteSpace(line.Trim()))
                                  .ToList();

            if (inputLines.Count > 1)
            {
                // Multiple messages - switch to "All"
                if (AllRadio != null && AllRadio.IsChecked != true)
                {
                    AllRadio.IsChecked = true;
                    StatusLabel.Content = "Switched to 'All' - multiple messages detected";
                    StatusLabel.Foreground = Brushes.Blue;
                }
            }

            // Determine which message type to force (based on radio selection)
            string forceMessageType = null;
            if (RsuToControllerRadio?.IsChecked == true)
            {
                forceMessageType = "RsuToControllerMessageData";
            }
            else if (ControllerToRsuRadio?.IsChecked == true)
            {
                forceMessageType = "ControllerToRsuMessageData";
            }
            // If AllRadio is checked, leave forceMessageType as null (auto-detect)

            // DECODE: Always uses the forceMessageType to decode
            // This respects the ComboBox selection (RsuToController vs ControllerToRsu)
            if (ProtobufParser.TryDecodeProtobufFromHex(input, out string decoded, forceMessageType))
            {
                // Check if result is empty
                string trimmedResult = decoded.Trim();
                bool isEmpty = trimmedResult == "{}" || trimmedResult == "{ }" || string.IsNullOrWhiteSpace(trimmedResult);

                if (isEmpty && inputLines.Count == 1 && forceMessageType != null)
                {
                    // Empty with forced type - try opposite direction
                    string oppositeType = forceMessageType == "RsuToControllerMessageData"
                        ? "ControllerToRsuMessageData"
                        : "RsuToControllerMessageData";

                    if (ProtobufParser.TryDecodeProtobufFromHex(input, out string retryDecoded, oppositeType))
                    {
                        string retryTrimmed = retryDecoded.Trim();
                        bool retryIsEmpty = retryTrimmed == "{}" || retryTrimmed == "{ }" || string.IsNullOrWhiteSpace(retryTrimmed);

                        if (!retryIsEmpty)
                        {
                            // Success with opposite direction - switch radio
                            if (oppositeType == "RsuToControllerMessageData")
                            {
                                RsuToControllerRadio.IsChecked = true;
                                StatusLabel.Content = "Auto-switched to RSU -> Controller";
                            }
                            else
                            {
                                ControllerToRsuRadio.IsChecked = true;
                                StatusLabel.Content = "Auto-switched to Controller -> RSU";
                            }
                            StatusLabel.Foreground = Brushes.Green;
                            TestResultTextBox.Text = retryDecoded;
                            return;
                        }
                    }
                }

                // Show result
                TestResultTextBox.Text = decoded;

                string directionLabel = forceMessageType == "RsuToControllerMessageData" ? "RSU->Controller" :
                                       forceMessageType == "ControllerToRsuMessageData" ? "Controller->RSU" :
                                       "Auto-detect";

                StatusLabel.Content = isEmpty ? $"Decode returned empty result ({directionLabel})" : $"Decode successful ({directionLabel})";
                StatusLabel.Foreground = isEmpty ? Brushes.Orange : Brushes.Green;
            }
            else
            {
                TestResultTextBox.Text = decoded;
                StatusLabel.Content = "Decode failed";
                StatusLabel.Foreground = Brushes.Red;
            }
        }

        private void GenerateDefaultMessage_Click(object sender, RoutedEventArgs e)
        {
            GenerateAndShowDefaultMessage();
        }
        private void MessageDirection_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.IsChecked == true)
            {
                // Add null checks for radio buttons
                if (RsuToControllerRadio != null && rb.Name == "RsuToControllerRadio")
                {
                    _currentDirection = MessageDirection.RsuToController;
                    PopulateMessageTypeDropdown("RsuToControllerMessageData");
                }
                else if (ControllerToRsuRadio != null && rb.Name == "ControllerToRsuRadio")
                {
                    _currentDirection = MessageDirection.ControllerToRsu;
                    PopulateMessageTypeDropdown("ControllerToRsuMessageData");
                }
                else if (AllRadio != null && rb.Name == "AllRadio")
                {
                    // "All" mode - clear dropdown
                    if (MessageTypeComboBox != null)
                    {
                        MessageTypeComboBox.ItemsSource = null;
                        MessageTypeComboBox.IsEnabled = false;
                    }

                    if (TestResultTextBox != null)
                    {
                        TestResultTextBox.Text = "Auto-detect mode: Paste protobuf data to decode";
                    }
                    if (StatusLabel != null)
                    {
                        StatusLabel.Content = "Auto-detect mode active";
                        StatusLabel.Foreground = Brushes.Blue;
                    }
                    return;
                }

                GenerateAndShowDefaultMessage();
            }
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
                    field.Type.EndsWith("." + m.Name));

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
                if (field.Type.Contains("Enum"))
                {
                    string defaultEnumValue = GetDefaultEnumValue(field.Type);
                    return $"[\"{defaultEnumValue}\"]";
                }

                // Check if it's a repeated message - generate array with one default message
                var repeatedMessage = allMessages.FirstOrDefault(m =>
                    m.Name == field.Type || field.Type.EndsWith("." + m.Name));

                if (repeatedMessage != null)
                {
                    return "[\n" +
                           new string(' ', (indentLevel + 1) * 2) + GenerateDefaultMessageJson(repeatedMessage, allMessages, indentLevel + 1).Replace("\n", "\n" + new string(' ', (indentLevel + 1) * 2)) + "\n" +
                           new string(' ', indentLevel * 2) + "]";
                }

                return "[]";
            }

            // Handle google.protobuf types FIRST
            if (field.Type.Contains("google.protobuf"))
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

            // Handle different field types
            switch (field.Type.ToLower())
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
                    if (field.Type.Contains("Enum"))
                    {
                        string defaultEnumValue = GetDefaultEnumValue(field.Type);
                        return $"\"{defaultEnumValue}\"";
                    }

                    // Check if it's a nested message type
                    var nestedMessage = allMessages.FirstOrDefault(m =>
                        m.Name == field.Type ||
                        field.Type.EndsWith("." + m.Name));

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

        

        private void SearchNext_Click(object sender, RoutedEventArgs e)
        {
            if (_searchMatches.Count == 0)
                return;

            _currentSearchIndex = (_currentSearchIndex + 1) % _searchMatches.Count;
            HighlightCurrentMatch();
        }

        private void SearchPrevious_Click(object sender, RoutedEventArgs e)
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

        private async void SearchDebounceTimer_Tick(object sender, EventArgs e)
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
        public string FileName { get; set; }
        public string Content { get; set; }
        public string FilePath { get; set; }
        public string MessageSummary { get; set; }
        public string Status { get; set; }
        public Brush StatusColor { get; set; }
    }

    public class OneofOption
    {
        public int FieldNumber { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
    }
}