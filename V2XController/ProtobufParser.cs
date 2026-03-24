using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace V2XController
{
    public static class ProtobufParser
    {
        private static Dictionary<string, ProtoMessage> _compiledMessages = new();
        private static string _currentProtoDefinition = string.Empty;

        public static List<ProtoMessage> ParseProtoDefinition(string protoDefinition)
        {
            var messages = new List<ProtoMessage>();

            try
            {
                if (string.IsNullOrWhiteSpace(protoDefinition))
                    return messages;

                var lines = protoDefinition.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                ProtoMessage currentMessage = null;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    // Skip comments and empty lines
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//") || trimmed.StartsWith("*"))
                        continue;

                    // Parse message definition
                    if (trimmed.StartsWith("message "))
                    {
                        var messageName = trimmed.Split(' ')[1].Replace("{", "").Trim();
                        currentMessage = new ProtoMessage { Name = messageName };
                        messages.Add(currentMessage);
                    }
                    else if (trimmed == "}")
                    {
                        currentMessage = null;
                    }
                    else if (currentMessage != null && !trimmed.StartsWith("syntax") &&
                             !trimmed.StartsWith("package") && !trimmed.StartsWith("import") &&
                             !trimmed.StartsWith("enum") && !trimmed.StartsWith("oneof"))
                    {
                        // Parse field definition: [optional/repeated] type name = number;
                        var parts = trimmed.Replace(";", "").Split('=');
                        if (parts.Length == 2)
                        {
                            var fieldDef = parts[0].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (fieldDef.Length >= 2)
                            {
                                bool isOptional = fieldDef[0] == "optional";
                                bool isRepeated = fieldDef[0] == "repeated";

                                int typeIndex = (isOptional || isRepeated) ? 1 : 0;
                                int nameIndex = (isOptional || isRepeated) ? 2 : 1;

                                if (nameIndex < fieldDef.Length)
                                {
                                    var fieldType = fieldDef[typeIndex].Trim();
                                    var fieldName = fieldDef[nameIndex].Trim();

                                    if (int.TryParse(parts[1].Trim(), out int fieldNumber))
                                    {
                                        currentMessage.Fields.Add(new ProtoField
                                        {
                                            Name = fieldName,
                                            Type = fieldType,
                                            Number = fieldNumber,
                                            IsRepeated = isRepeated,
                                            IsOptional = isOptional
                                        });
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing proto definition: {ex.Message}");
            }

            return messages;
        }

        public static bool CompileProtoDefinition(string protoDefinition, out string error)
        {
            error = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(protoDefinition))
                {
                    error = "Proto definition is empty";
                    return false;
                }

                _compiledMessages.Clear();
                _currentProtoDefinition = protoDefinition;

                var messages = ParseProtoDefinition(protoDefinition);

                foreach (var message in messages)
                {
                    _compiledMessages[message.Name] = message;
                }

                if (_compiledMessages.Count == 0)
                {
                    error = "No valid messages found in proto definition";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void ClearAllDefinitions()
        {
            _compiledMessages.Clear();
            _currentProtoDefinition = string.Empty;
        }

        private static string ExtractTimestamp(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // Extract timestamp (format: HH:MM:SS.mmm)
            var timestampPattern = @"^(\d{1,2}:\d{2}:\d{2}\.\d{1,3})";
            var match = System.Text.RegularExpressions.Regex.Match(input.Trim(), timestampPattern);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }

            return string.Empty;
        }

        private static string CleanInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            input = input.Trim();

            // Remove timestamp prefix with optional special tags (format: HH:MM:SS.mmm, <tag> or HH:MM:SS.mmm, )
            // Pattern: digits, colon, digits, colon, digits, dot, digits, comma, optional <anything>, space
            var timestampPattern = @"^\d{1,2}:\d{2}:\d{2}\.\d{1,3},\s*(<[^>]*>)?\s*";
            var match = System.Text.RegularExpressions.Regex.Match(input, timestampPattern);
            if (match.Success)
            {
                input = input.Substring(match.Length).Trim();
            }

            return input;
        }

        private static ProtoMessage DetectMessageType(byte[] data)
        {
            // Analyzuj field numbers přímo z binárních dat
            var fieldNumbers = new HashSet<int>();
            var field10Data = new List<byte[]>();
            var field10InnerFieldNumbers = new HashSet<int>(); // Field numbers UVNITŘ field 10
            int pos = 0;

            try
            {
                while (pos < data.Length)
                {
                    if (!TryReadVarint(data, ref pos, out ulong tag))
                        break;

                    int fieldNumber = (int)(tag >> 3);
                    int wireType = (int)(tag & 0x7);

                    fieldNumbers.Add(fieldNumber);

                    switch (wireType)
                    {
                        case 0: // Varint
                            if (!TryReadVarint(data, ref pos, out _))
                                return _compiledMessages.Values.FirstOrDefault();
                            break;
                        case 1: // 64-bit
                            pos += 8;
                            break;
                        case 2: // Length-delimited
                            if (TryReadVarint(data, ref pos, out ulong length))
                            {
                                if (fieldNumber == 10 && pos + (int)length <= data.Length)
                                {
                                    // Zkopíruj data z field 10
                                    var f10data = new byte[length];
                                    Array.Copy(data, pos, f10data, 0, (int)length);
                                    field10Data.Add(f10data);

                                    // Analyzuj field numbers UVNITŘ field 10
                                    int innerPos = 0;
                                    try
                                    {
                                        while (innerPos < f10data.Length)
                                        {
                                            if (!TryReadVarint(f10data, ref innerPos, out ulong innerTag))
                                                break;

                                            int innerFieldNumber = (int)(innerTag >> 3);
                                            int innerWireType = (int)(innerTag & 0x7);
                                            field10InnerFieldNumbers.Add(innerFieldNumber);

                                            // Skip inner field data
                                            switch (innerWireType)
                                            {
                                                case 0: TryReadVarint(f10data, ref innerPos, out _); break;
                                                case 1: innerPos += 8; break;
                                                case 2:
                                                    if (TryReadVarint(f10data, ref innerPos, out ulong innerLen))
                                                        innerPos += (int)innerLen;
                                                    break;
                                                case 5: innerPos += 4; break;
                                                default: innerPos = f10data.Length; break;
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                pos += (int)length;
                            }
                            break;
                        case 5: // 32-bit
                            pos += 4;
                            break;
                        default:
                            return _compiledMessages.Values.FirstOrDefault();
                    }
                }
            }
            catch
            {
                // If parsing fails, continue with what we got
            }

            Console.WriteLine($"Detected field numbers: {string.Join(", ", fieldNumbers.OrderBy(x => x))}");
            if (field10InnerFieldNumbers.Count > 0)
            {
                Console.WriteLine($"Field 10 inner field numbers: {string.Join(", ", field10InnerFieldNumbers.OrderBy(x => x))}");
            }

            // **ROZHODNUTÍ podle field 10 inner structure**
            if (fieldNumbers.Contains(10) && field10InnerFieldNumbers.Count > 0)
            {
                // NearbyVehicleDetectionInfo očekáváme:
                //   field 1 = timestamp
                //   field 2 = vehicle_info
                //   field 10 = coordinates
                //   field 11 = speed
                //   field 12 = heading
                //   field 20 = distance
                //   field 30 = public_transport_vehicle_data

                // IntersectionStatus očekáváme:
                //   field 1 = timestamp
                //   field 2 = intersection_id
                //   field 10 = controller_status (repeated enum - wire type 0)
                //   field 20 = intersection_lanes (repeated)
                //   field 30 = movement_states (repeated)

                bool hasField2 = field10InnerFieldNumbers.Contains(2);
                bool hasField10Inner = field10InnerFieldNumbers.Contains(10);
                bool hasField11 = field10InnerFieldNumbers.Contains(11);
                bool hasField12 = field10InnerFieldNumbers.Contains(12);
                bool hasField20Inner = field10InnerFieldNumbers.Contains(20);
                bool hasField30Inner = field10InnerFieldNumbers.Contains(30);

                // NearbyVehicleDetectionInfo má typicky: 1, 2, 10, 11, 12, 20, 30
                // IntersectionStatus má typicky: 1, 2, 10, 20, 30

                // Klíčový rozdíl: NearbyVehicleDetectionInfo má field 11 (speed) a 12 (heading)
                if (hasField11 && hasField12)
                {
                    if (_compiledMessages.TryGetValue("RsuToControllerMessageData", out var rsuMsg))
                    {
                        Console.WriteLine("→ Selected RsuToControllerMessageData (field 10 has speed/heading)");
                        return rsuMsg;
                    }
                }

                // Pokud field 10 má field 2, ale NEMÁ 11 a 12 → pravděpodobně IntersectionStatus
                if (hasField2 && !hasField11 && !hasField12)
                {
                    if (_compiledMessages.TryGetValue("ControllerToRsuMessageData", out var ctrlMsg))
                    {
                        Console.WriteLine("→ Selected ControllerToRsuMessageData (field 10 = IntersectionStatus)");
                        return ctrlMsg;
                    }
                }
            }

            // Pokud field 10 chybí, použij field 20/30 pro rozlišení
            if (!fieldNumbers.Contains(10))
            {
                bool hasField20 = fieldNumbers.Contains(20);
                bool hasField30 = fieldNumbers.Contains(30);

                // RsuToController field 20 = intersection_request, field 30 = heartbeat
                // ControllerToRsu field 20 = intersection_pass_request_status

                if (hasField20 || hasField30)
                {
                    // Fallback na scoring
                }
            }

            // Fallback: Scoring systém
            ProtoMessage bestMessage = null;
            int bestScore = 0;

            string[] messageTypesToTry = { "RsuToControllerMessageData", "ControllerToRsuMessageData" };

            foreach (var messageTypeName in messageTypesToTry)
            {
                if (_compiledMessages.TryGetValue(messageTypeName, out var message))
                {
                    try
                    {
                        var testDecode = DecodeMessageToObject(data, message, 0);
                        int score = 0;

                        score += testDecode.Count;
                        int knownFields = testDecode.Count(kvp => !kvp.Key.StartsWith("field_") && kvp.Value != null);
                        score += knownFields * 3;

                        if (!testDecode.ContainsKey("_error"))
                            score += 5;

                        if (testDecode.ContainsKey("crc")) score += 2;
                        if (testDecode.ContainsKey("timestamp")) score += 2;
                        if (testDecode.ContainsKey("nearby_vehicle_detection")) score += 15;
                        if (testDecode.ContainsKey("intersection_status")) score += 15;
                        if (testDecode.ContainsKey("intersection_pass_request_status")) score += 15;
                        if (testDecode.ContainsKey("intersection_request")) score += 15;
                        if (testDecode.ContainsKey("heartbeat")) score += 15;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestMessage = message;
                        }

                        Console.WriteLine($"Scored {messageTypeName}: {score} points (fields={testDecode.Count}, known={knownFields})");
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            if (bestMessage != null && bestScore > 0)
            {
                Console.WriteLine($"→ Selected {bestMessage.Name} with score {bestScore}");
                return bestMessage;
            }

            return _compiledMessages.Values.FirstOrDefault();
        }

        public static string DecodeProtobufMessage(byte[] data, string protoDefinition, string inputFormat = "Unknown")
        {
            if (string.IsNullOrWhiteSpace(protoDefinition))
                return "No proto definition loaded";

            if (_currentProtoDefinition != protoDefinition)
            {
                if (!CompileProtoDefinition(protoDefinition, out string error))
                    return $"Failed to compile proto: {error}";
            }

            if (_compiledMessages.Count == 0)
                return "No messages defined in proto";

            // Auto-detect message type
            ProtoMessage detectedMessage = DetectMessageType(data);

            if (detectedMessage != null)
            {
                var decodedObj = DecodeMessageToObject(data, detectedMessage, 0);

                // Clean the decoded object
                CleanDecodedObject(decodedObj);

                // Format as JSON (compact, single line)
                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                string json = JsonSerializer.Serialize(decodedObj, jsonOptions);
                return json;
            }

            return "No suitable message type found";
        }

        public static string DecodeMultipleProtobufMessages(string input, string protoDefinition)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "No input provided";

            if (string.IsNullOrWhiteSpace(protoDefinition))
                return "No proto definition loaded";

            // Split input by lines
            var lines = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var results = new List<string>();

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine))
                    continue;

                try
                {
                    // Extract timestamp before cleaning
                    string timestamp = ExtractTimestamp(trimmedLine);

                    // Clean input - remove timestamp prefix if present
                    var cleanedInput = CleanInput(trimmedLine);
                    cleanedInput = cleanedInput.Replace(" ", "").Replace("\t", "");

                    byte[] data = null;

                    // Try Base64 first
                    if (IsBase64Input(cleanedInput))
                    {
                        try
                        {
                            data = Convert.FromBase64String(cleanedInput);
                        }
                        catch { }
                    }

                    // Try Hex if Base64 failed
                    if (data == null && IsHexInput(cleanedInput))
                    {
                        if (cleanedInput.Length % 2 == 0)
                        {
                            try
                            {
                                data = new byte[cleanedInput.Length / 2];
                                for (int i = 0; i < data.Length; i++)
                                {
                                    data[i] = Convert.ToByte(cleanedInput.Substring(i * 2, 2), 16);
                                }
                            }
                            catch { }
                        }
                    }

                    if (data != null)
                    {
                        string json = DecodeProtobufMessage(data, protoDefinition, "Auto");
                        if (!string.IsNullOrWhiteSpace(json) && !json.StartsWith("Failed") && !json.StartsWith("No"))
                        {
                            // Format: timestamp {json}
                            if (!string.IsNullOrWhiteSpace(timestamp))
                            {
                                results.Add($"{timestamp} {json}");
                            }
                            else
                            {
                                results.Add($"{json}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Skip failed lines silently
                    Console.WriteLine($"Failed to decode line: {ex.Message}");
                }
            }

            // Join results with double CRLF (empty line between messages)
            return string.Join("\r\n\r\n", results);
        }

        private static void CleanDecodedObject(Dictionary<string, object> obj)
        {
            if (obj == null) return;

            var keysToRemove = new List<string>();

            foreach (var kvp in obj.ToList())
            {
                // Remove error fields
                if (kvp.Key.StartsWith("_error"))
                {
                    keysToRemove.Add(kvp.Key);
                    continue;
                }

                // Remove fields with invalid/unprintable content
                if (kvp.Value is string strValue)
                {
                    // Remove strings that look like control characters or invalid data
                    if (strValue.Contains('\0') || strValue.All(c => c < 32 && c != '\t' && c != '\n' && c != '\r'))
                    {
                        keysToRemove.Add(kvp.Key);
                        continue;
                    }
                }

                // Clean nested dictionaries
                if (kvp.Value is Dictionary<string, object> nestedDict)
                {
                    CleanDecodedObject(nestedDict);

                    // Remove if empty after cleaning
                    if (nestedDict.Count == 0)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                // Clean lists
                else if (kvp.Value is List<object> list)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i] is Dictionary<string, object> listDict)
                        {
                            CleanDecodedObject(listDict);
                            if (listDict.Count == 0)
                            {
                                list.RemoveAt(i);
                            }
                        }
                    }

                    // Remove if empty
                    if (list.Count == 0)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
            }

            // Remove marked keys
            foreach (var key in keysToRemove)
            {
                obj.Remove(key);
            }
        }

        private static void AddHumanReadableSummary(StringBuilder sb, Dictionary<string, object> data)
        {
            if (data.TryGetValue("crc", out object crcObj))
            {
                sb.AppendLine($"  CRC: {crcObj}");
            }

            if (data.TryGetValue("timestamp", out object tsObj) && tsObj is Dictionary<string, object> tsDict)
            {
                if (tsDict.TryGetValue("seconds", out object seconds))
                {
                    var dt = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(seconds));
                    sb.AppendLine($"  Timestamp: {dt:yyyy-MM-dd HH:mm:ss} UTC");
                }
            }

            if (data.TryGetValue("nearby_vehicle_detection", out object nearbyObj) && nearbyObj is Dictionary<string, object> nearbyDict)
            {
                sb.AppendLine($"   Nearby Vehicle Detection:");

                // NOVÉ: Zobrazení Vehicle Info
                if (nearbyDict.TryGetValue("vehicle_info", out object vehInfoObj) && vehInfoObj is Dictionary<string, object> vehDict)
                {
                    if (vehDict.TryGetValue("vehicle_id", out object vid))
                    {
                        sb.AppendLine($"     Vehicle ID: {vid}");
                    }
                    if (vehDict.TryGetValue("vehicle_type", out object vtype))
                    {
                        sb.AppendLine($"     Type: {GetVehicleTypeName(vtype)}");
                    }
                    if (vehDict.TryGetValue("vehicle_role", out object vrole))
                    {
                        sb.AppendLine($"     Role: {GetVehicleRoleName(vrole)}");
                    }
                }

                // Timestamp detection
                if (nearbyDict.TryGetValue("timestamp", out object detTsObj) && detTsObj is Dictionary<string, object> detTsDict)
                {
                    if (detTsDict.TryGetValue("seconds", out object detSeconds))
                    {
                        var detDt = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(detSeconds));
                        sb.AppendLine($"     Detection Time: {detDt:HH:mm:ss} UTC");
                    }
                }

                if (nearbyDict.TryGetValue("coordinates", out object coordsObj) && coordsObj is Dictionary<string, object> coordsDict)
                {
                    if (coordsDict.TryGetValue("latitude", out object lat) && coordsDict.TryGetValue("longitude", out object lon))
                    {
                        sb.AppendLine($"     Position: {lat}°N, {lon}°E");
                    }
                    if (coordsDict.TryGetValue("altitude", out object alt))
                    {
                        sb.AppendLine($"     Altitude: {alt}m");
                    }
                }

                if (nearbyDict.TryGetValue("speed", out object spd))
                {
                    sb.AppendLine($"     Speed: {spd} km/h");
                }

                if (nearbyDict.TryGetValue("heading", out object hdg))
                {
                    sb.AppendLine($"     Heading: {hdg}°");
                }

                if (nearbyDict.TryGetValue("distance", out object dist))
                {
                    sb.AppendLine($"     Distance: {dist}m");
                }

                // Public transport data
                if (nearbyDict.TryGetValue("public_transport_vehicle_data", out object ptObj) && ptObj is Dictionary<string, object> ptDict)
                {
                    sb.AppendLine($"     PUBLIC TRANSPORT DATA:");

                    if (ptDict.TryGetValue("vehicle_type", out object ptVtype))
                    {
                        sb.AppendLine($"        Type: {GetPublicTransportTypeName(ptVtype)}");
                    }
                    if (ptDict.TryGetValue("line_number", out object lineNum))
                    {
                        sb.AppendLine($"        Line: {lineNum}");
                    }
                    if (ptDict.TryGetValue("vehicle_number", out object vehNum))
                    {
                        sb.AppendLine($"        Vehicle #: {vehNum}");
                    }
                    if (ptDict.TryGetValue("delay", out object delay))
                    {
                        int delaySeconds = Convert.ToInt32(delay);
                        string delayStr = delaySeconds > 0 ? $"+{delaySeconds}s (late)" :
                                         delaySeconds < 0 ? $"{delaySeconds}s (early)" :
                                         "On time";
                        sb.AppendLine($"        Delay: {delayStr}");
                    }
                }
            }
        }

        private static string GetVehicleTypeName(object value)
        {
            if (value == null) return "Unknown";

            string strValue = value.ToString();
            if (strValue.Contains("("))
            {
                // Extract enum value number
                var match = System.Text.RegularExpressions.Regex.Match(strValue, @"\((\d+)\)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int enumValue))
                {
                    return enumValue switch
                    {
                        10 => "Pedestrian",
                        20 => "Cyclist",
                        22 => "Motorcycle",
                        30 => "Passenger Car",
                        40 => "Bus",
                        41 => "Van",
                        42 => "Truck",
                        43 => "Trailer",
                        50 => "Special Vehicle",
                        60 => "Tram",
                        _ => $"Type {enumValue}"
                    };
                }
            }

            return strValue;
        }

        private static string GetVehicleRoleName(object value)
        {
            if (value == null) return "Unknown";

            string strValue = value.ToString();
            if (strValue.Contains("("))
            {
                var match = System.Text.RegularExpressions.Regex.Match(strValue, @"\((\d+)\)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int enumValue))
                {
                    return enumValue switch
                    {
                        10 => "Public Transport",
                        11 => "Special Transport",
                        20 => "Dangerous Goods",
                        30 => "Road Work",
                        40 => "Rescue",
                        41 => "Emergency",
                        42 => "Safety Car",
                        50 => "Agriculture",
                        60 => "Commercial",
                        70 => "Military",
                        80 => "Road Operator",
                        90 => "Taxi",
                        _ => $"Role {enumValue}"
                    };
                }
            }

            return strValue;
        }

        private static string GetPublicTransportTypeName(object value)
        {
            if (value == null) return "Unknown";

            string strValue = value.ToString();
            if (strValue.Contains("("))
            {
                var match = System.Text.RegularExpressions.Regex.Match(strValue, @"\((\d+)\)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int enumValue))
                {
                    return enumValue switch
                    {
                        10 => "Bus",
                        20 => "Tram",
                        30 => "Train",
                        40 => "Metro",
                        50 => "Trolleybus",
                        _ => $"Type {enumValue}"
                    };
                }
            }

            return strValue;
        }

        private static Dictionary<string, object> DecodeMessageToObject(byte[] data, ProtoMessage message, int depth)
        {
            if (depth > 10)
                return new Dictionary<string, object> { { "_error", "Max recursion depth reached" } };

            var result = new Dictionary<string, object>();
            int position = 0;

            while (position < data.Length)
            {
                try
                {
                    if (!TryReadVarint(data, ref position, out ulong tag))
                        break;

                    int fieldNumber = (int)(tag >> 3);
                    int wireType = (int)(tag & 0x7);

                    var field = message.Fields.FirstOrDefault(f => f.Number == fieldNumber);
                    string fieldName = field?.Name ?? $"field_{fieldNumber}";
                    string fieldType = field?.Type ?? "unknown";

                    object value = null;

                    switch (wireType)
                    {
                        case 0: // Varint
                            if (TryReadVarint(data, ref position, out ulong varint))
                            {
                                if (fieldType == "bool")
                                    value = varint != 0;
                                else if (fieldType == "sint32" || fieldType == "sint64")
                                    value = DecodeZigZag(varint);
                                else if (fieldType.Contains("Enum"))
                                    value = (long)varint;  // Just the numeric value
                                else
                                    value = (long)varint;
                            }
                            break;

                        case 1: // 64-bit
                            if (position + 8 <= data.Length)
                            {
                                if (fieldType == "double" || fieldType == "Double")
                                    value = BitConverter.ToDouble(data, position);
                                else if (fieldType == "fixed64")
                                    value = BitConverter.ToUInt64(data, position);
                                else if (fieldType == "sfixed64")
                                    value = BitConverter.ToInt64(data, position);
                                else
                                    value = BitConverter.ToInt64(data, position);
                                position += 8;
                            }
                            break;

                        case 2: // Length-delimited
                            if (TryReadVarint(data, ref position, out ulong length))
                            {
                                if (position + (int)length <= data.Length)
                                {
                                    byte[] bytes = new byte[length];
                                    Array.Copy(data, position, bytes, 0, (int)length);
                                    position += (int)length;

                                    bool decodedAsNested = false;

                                    // Handle google.protobuf.Timestamp
                                    if (fieldType.Contains("Timestamp") || fieldName == "timestamp")
                                    {
                                        var timestampDict = DecodeTimestamp(bytes);
                                        if (timestampDict != null)
                                        {
                                            value = timestampDict;
                                            decodedAsNested = true;
                                        }
                                    }

                                    // Check if it's likely a string FIRST
                                    if (!decodedAsNested)
                                    {
                                        // If field type is explicitly string, or content is printable, decode as string
                                        if (fieldType == "string" ||
                                            fieldName.Contains("id") ||
                                            fieldName.Contains("name") ||
                                            fieldName.Contains("number") ||
                                            fieldName.Contains("line") ||
                                            fieldName.Contains("route") ||
                                            fieldName.Contains("schedule") ||
                                            fieldName.Contains("stop"))
                                        {
                                            if (IsLikelyString(bytes))
                                            {
                                                try
                                                {
                                                    value = Encoding.UTF8.GetString(bytes);
                                                    decodedAsNested = true;
                                                }
                                                catch { }
                                            }
                                        }
                                    }

                                    // PRIORITNÍ detekce známých typů podle field number a názvu
                                    if (!decodedAsNested)
                                    {
                                        // Pro NearbyVehicleDetectionInfo: field 1 = Timestamp
                                        if ((fieldNumber == 1 && message.Name == "NearbyVehicleDetectionInfo") ||
                                            (fieldName == "timestamp" && message.Name == "NearbyVehicleDetectionInfo"))
                                        {
                                            var timestampDict = DecodeTimestamp(bytes);
                                            if (timestampDict != null)
                                            {
                                                value = timestampDict;
                                                decodedAsNested = true;
                                            }
                                        }
                                        // Pro NearbyVehicleDetectionInfo: field 2 = VehicleInfo
                                        else if ((fieldNumber == 2 && message.Name == "NearbyVehicleDetectionInfo") ||
                                                fieldName == "vehicle_info")
                                        {
                                            if (_compiledMessages.TryGetValue("VehicleInfo", out var vehicleInfoMsg))
                                            {
                                                try
                                                {
                                                    var nested = DecodeMessageToObject(bytes, vehicleInfoMsg, depth + 1);
                                                    if (nested.Count > 0)
                                                    {
                                                        value = nested;
                                                        decodedAsNested = true;
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                        // Pro NearbyVehicleDetectionInfo: field 10 = Coordinates
                                        else if ((fieldNumber == 10 && message.Name == "NearbyVehicleDetectionInfo") ||
                                                 fieldName == "coordinates")
                                        {
                                            if (_compiledMessages.TryGetValue("Coordinates", out var coordinatesMsg))
                                            {
                                                try
                                                {
                                                    var nested = DecodeMessageToObject(bytes, coordinatesMsg, depth + 1);
                                                    if (nested.Count > 0)
                                                    {
                                                        value = nested;
                                                        decodedAsNested = true;
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                        // Pro NearbyVehicleDetectionInfo: field 30 = PublicTransportVehicleData
                                        else if ((fieldNumber == 30 && message.Name == "NearbyVehicleDetectionInfo") ||
                                                 fieldName == "public_transport_vehicle_data")
                                        {
                                            if (_compiledMessages.TryGetValue("PublicTransportVehicleData", out var ptMsg))
                                            {
                                                try
                                                {
                                                    var nested = DecodeMessageToObject(bytes, ptMsg, depth + 1);
                                                    if (nested.Count > 0)
                                                    {
                                                        value = nested;
                                                        decodedAsNested = true;
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                        // Pro Coordinates: field 10 = Accuracy
                                        else if ((fieldNumber == 10 && message.Name == "Coordinates") ||
                                                 fieldName == "accuracy")
                                        {
                                            if (_compiledMessages.TryGetValue("Accuracy", out var accuracyMsg))
                                            {
                                                try
                                                {
                                                    var nested = DecodeMessageToObject(bytes, accuracyMsg, depth + 1);
                                                    if (nested.Count > 0)
                                                    {
                                                        value = nested;
                                                        decodedAsNested = true;
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                        // Pro Accuracy: field 50 = PositionConfidenceEllipse
                                        else if ((fieldNumber == 50 && message.Name == "Accuracy") ||
                                                 fieldName == "raw_confidence_data")
                                        {
                                            if (_compiledMessages.TryGetValue("PositionConfidenceEllipse", out var ellipseMsg))
                                            {
                                                try
                                                {
                                                    var nested = DecodeMessageToObject(bytes, ellipseMsg, depth + 1);
                                                    if (nested.Count > 0)
                                                    {
                                                        value = nested;
                                                        decodedAsNested = true;
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
                                    }

                                    // Try to decode as known message type by field type name
                                    if (!decodedAsNested && !string.IsNullOrEmpty(fieldType) &&
                                        fieldType != "string" && fieldType != "bytes" &&
                                        _compiledMessages.TryGetValue(fieldType, out var nestedMessage))
                                    {
                                        try
                                        {
                                            var nested = DecodeMessageToObject(bytes, nestedMessage, depth + 1);
                                            if (nested.Count > 0 && !nested.ContainsKey("_error"))
                                            {
                                                value = nested;
                                                decodedAsNested = true;
                                            }
                                        }
                                        catch { }
                                    }

                                    // Fallback to string
                                    if (!decodedAsNested)
                                    {
                                        if (fieldType == "string" || IsLikelyString(bytes))
                                        {
                                            try
                                            {
                                                value = Encoding.UTF8.GetString(bytes);
                                            }
                                            catch
                                            {
                                                // Skip invalid hex data
                                                value = null;
                                            }
                                        }
                                        else if (fieldType == "bytes")
                                        {
                                            // Skip raw bytes in output
                                            value = null;
                                        }
                                    }
                                }
                            }
                            break;

                        case 5: // 32-bit
                            if (position + 4 <= data.Length)
                            {
                                if (fieldType == "float" || fieldType == "Float")
                                {
                                    value = BitConverter.ToSingle(data, position);
                                    // Round to 2 decimal places for readability
                                    value = Math.Round((float)value, 2);
                                }
                                else if (fieldType == "fixed32")
                                    value = BitConverter.ToUInt32(data, position);
                                else if (fieldType == "sfixed32")
                                    value = BitConverter.ToInt32(data, position);
                                else
                                    value = BitConverter.ToInt32(data, position);
                                position += 4;
                            }
                            break;

                        default:
                            // Skip unsupported wire types
                            value = null;
                            break;
                    }

                    if (value != null)
                    {
                        if (result.ContainsKey(fieldName))
                        {
                            if (result[fieldName] is List<object> list)
                            {
                                list.Add(value);
                            }
                            else
                            {
                                var existingValue = result[fieldName];
                                result[fieldName] = new List<object> { existingValue, value };
                            }
                        }
                        else
                        {
                            result[fieldName] = value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Skip errors silently
                    break;
                }
            }

            return result;
        }

        private static Dictionary<string, object> DecodeTimestamp(byte[] data)
        {
            try
            {
                var result = new Dictionary<string, object>();
                int position = 0;

                while (position < data.Length)
                {
                    if (!TryReadVarint(data, ref position, out ulong tag))
                        break;

                    int fieldNumber = (int)(tag >> 3);
                    int wireType = (int)(tag & 0x7);

                    if (wireType == 0 && TryReadVarint(data, ref position, out ulong value))
                    {
                        if (fieldNumber == 1)
                            result["seconds"] = (long)value;
                        else if (fieldNumber == 2)
                            result["nanos"] = (int)value;
                    }
                }

                return result.Count > 0 ? result : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsLikelyString(byte[] bytes)
        {
            if (bytes.Length == 0) return false;

            // Velmi krátké sekvence (1-2 bajty) nejsou pravděpodobně stringy
            if (bytes.Length <= 2) return false;

            int printableCount = 0;
            int digitCount = 0;

            foreach (byte b in bytes)
            {
                // Printable ASCII + tab/newline
                if ((b >= 32 && b <= 126) || b == 9 || b == 10 || b == 13)
                {
                    printableCount++;

                    // Count digits (0-9)
                    if (b >= '0' && b <= '9')
                        digitCount++;
                }
            }

            double printableRatio = (double)printableCount / bytes.Length;

            // If mostly digits and printable, it's likely a string ID or number
            if (digitCount > 0 && printableRatio > 0.8)
                return true;

            // Otherwise, at least 70% must be printable
            return printableRatio > 0.7;
        }

        private static bool TryReadVarint(byte[] data, ref int position, out ulong result)
        {
            result = 0;
            int shift = 0;

            while (position < data.Length)
            {
                byte b = data[position++];
                result |= (ulong)(b & 0x7F) << shift;

                if ((b & 0x80) == 0)
                    return true;

                shift += 7;
                if (shift >= 64)
                    return false;
            }

            return false;
        }

        private static long DecodeZigZag(ulong value)
        {
            return (long)(value >> 1) ^ -(long)(value & 1);
        }

        public static bool TryDecodeProtobufFromHex(string input, out string decoded)
        {
            decoded = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(input))
                    return false;

                string protoDefinition = ProtobufWindow.GetCombinedProtoDefinition();
                if (string.IsNullOrWhiteSpace(protoDefinition))
                {
                    decoded = "No proto definition loaded";
                    return false;
                }

                // Check if input contains multiple lines
                if (input.Contains('\n') || input.Contains('\r'))
                {
                    decoded = DecodeMultipleProtobufMessages(input, protoDefinition);
                    return !string.IsNullOrWhiteSpace(decoded);
                }

                // Single line processing
                // Extract timestamp before cleaning
                string timestamp = ExtractTimestamp(input);

                // Clean input - remove timestamp prefix if present
                input = CleanInput(input);
                input = input.Replace(" ", "").Replace("\t", "");

                byte[] data = null;
                string detectedFormat = "Unknown";

                if (IsBase64Input(input))
                {
                    try
                    {
                        data = Convert.FromBase64String(input);
                        detectedFormat = "Base64";
                    }
                    catch { }
                }

                if (data == null && IsHexInput(input))
                {
                    if (input.Length % 2 != 0)
                    {
                        decoded = "Invalid hex string - must have even length";
                        return false;
                    }

                    try
                    {
                        data = new byte[input.Length / 2];
                        for (int i = 0; i < data.Length; i++)
                        {
                            data[i] = Convert.ToByte(input.Substring(i * 2, 2), 16);
                        }
                        detectedFormat = "Hexadecimal";
                    }
                    catch
                    {
                        decoded = "Failed to parse hex string";
                        return false;
                    }
                }

                if (data == null)
                {
                    decoded = "Unable to detect input format (Hex or Base64)";
                    return false;
                }

                string json = DecodeProtobufMessage(data, protoDefinition, detectedFormat);

                // Format: timestamp {json}
                if (!string.IsNullOrWhiteSpace(timestamp))
                {
                    decoded = $"{timestamp} {json}";
                }
                else
                {
                    decoded = $"{json}";
                }

                return true;
            }
            catch (Exception ex)
            {
                decoded = $"Decode error: {ex.Message}";
                return false;
            }
        }

        private static bool IsBase64Input(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            if (input.Length % 4 != 0)
                return false;

            return input.All(c =>
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == '+' || c == '/' || c == '=');
        }

        private static bool IsHexInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return false;

            return input.All(c =>
                (c >= '0' && c <= '9') ||
                (c >= 'A' && c <= 'F') ||
                (c >= 'a' && c <= 'f'));
        }

        public class ProtoMessage
        {
            public string Name { get; set; }
            public List<ProtoField> Fields { get; set; } = new List<ProtoField>();
        }

        public class ProtoField
        {
            public string Name { get; set; }
            public string Type { get; set; }
            public int Number { get; set; }
            public bool IsRepeated { get; set; }
            public bool IsOptional { get; set; }
        }
    }
}