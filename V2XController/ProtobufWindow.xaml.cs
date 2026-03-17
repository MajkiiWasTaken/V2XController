using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace V2XController
{
    public partial class ProtobufWindow : Window
    {
        private ObservableCollection<ProtoFileInfo> _loadedFiles = new ObservableCollection<ProtoFileInfo>();

        public ProtobufWindow()
        {
            InitializeComponent();
            LoadedFilesPanel.ItemsSource = _loadedFiles;
            LoadSavedProtoFiles();
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

            // Use ProtobufParser.TryDecodeProtobufFromHex which handles timestamp cleaning
            if (ProtobufParser.TryDecodeProtobufFromHex(input, out string decoded))
            {
                TestResultTextBox.Text = decoded;
                StatusLabel.Content = "Decode successful";
                StatusLabel.Foreground = Brushes.Green;
            }
            else
            {
                TestResultTextBox.Text = decoded; // Contains error message
                StatusLabel.Content = "Decode failed";
                StatusLabel.Foreground = Brushes.Red;
            }
        }

        private void ClearTest_Click(object sender, RoutedEventArgs e)
        {
            TestHexTextBox.Clear();
            TestResultTextBox.Clear();
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
}