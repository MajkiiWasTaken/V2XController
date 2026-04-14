using Google.Protobuf;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
                bool insideOneof = false;

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
                        insideOneof = false;
                    }
                    else if (trimmed == "}")
                    {
                        if (insideOneof)
                        {
                            insideOneof = false;
                        }
                        else
                        {
                            currentMessage = null;
                        }
                    }
                    else if (trimmed.StartsWith("oneof "))
                    {
                        insideOneof = true;
                    }
                    else if (currentMessage != null && !trimmed.StartsWith("syntax") &&
                             !trimmed.StartsWith("package") && !trimmed.StartsWith("import") &&
                             !trimmed.StartsWith("enum"))
                    {
                        // Remove inline comments BEFORE parsing
                        int commentIndex = trimmed.IndexOf("//");
                        if (commentIndex >= 0)
                        {
                            trimmed = trimmed.Substring(0, commentIndex).Trim();
                        }

                        if (string.IsNullOrWhiteSpace(trimmed))
                            continue;

                        // Parse field definition
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
                                            IsOptional = isOptional || insideOneof
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

        // Replace DetectMessageType method (around line 150)
        private static ProtoMessage DetectMessageType(byte[] data)
        {
            // Analyzuj field numbers přímo z binárních dat
            var fieldNumbers = new HashSet<int>();
            var field10Data = new List<byte[]>();
            var field10InnerFieldNumbers = new HashSet<int>();
            var field20Data = new List<byte[]>();
            var field20InnerFieldNumbers = new HashSet<int>();
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
                                if (pos + (int)length <= data.Length)
                                {
                                    byte[] bytes = new byte[length];
                                    Array.Copy(data, pos, bytes, 0, (int)length);

                                    // Analyze field 10 (nearby_vehicle_detection vs intersection_status)
                                    if (fieldNumber == 10)
                                    {
                                        field10Data.Add(bytes);
                                        int innerPos = 0;
                                        try
                                        {
                                            while (innerPos < bytes.Length)
                                            {
                                                if (!TryReadVarint(bytes, ref innerPos, out ulong innerTag))
                                                    break;

                                                int innerFieldNumber = (int)(innerTag >> 3);
                                                int innerWireType = (int)(innerTag & 0x7);
                                                field10InnerFieldNumbers.Add(innerFieldNumber);

                                                switch (innerWireType)
                                                {
                                                    case 0: TryReadVarint(bytes, ref innerPos, out _); break;
                                                    case 1: innerPos += 8; break;
                                                    case 2:
                                                        if (TryReadVarint(bytes, ref innerPos, out ulong innerLen))
                                                            innerPos += (int)innerLen;
                                                        break;
                                                    case 5: innerPos += 4; break;
                                                    default: innerPos = bytes.Length; break;
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                    // Analyze field 20 (intersection_request vs intersection_pass_request_status)
                                    else if (fieldNumber == 20)
                                    {
                                        field20Data.Add(bytes);
                                        int innerPos = 0;
                                        try
                                        {
                                            while (innerPos < bytes.Length)
                                            {
                                                if (!TryReadVarint(bytes, ref innerPos, out ulong innerTag))
                                                    break;

                                                int innerFieldNumber = (int)(innerTag >> 3);
                                                int innerWireType = (int)(innerTag & 0x7);
                                                field20InnerFieldNumbers.Add(innerFieldNumber);

                                                switch (innerWireType)
                                                {
                                                    case 0: TryReadVarint(bytes, ref innerPos, out _); break;
                                                    case 1: innerPos += 8; break;
                                                    case 2:
                                                        if (TryReadVarint(bytes, ref innerPos, out ulong innerLen))
                                                            innerPos += (int)innerLen;
                                                        break;
                                                    case 5: innerPos += 4; break;
                                                    default: innerPos = bytes.Length; break;
                                                }
                                            }
                                        }
                                        catch { }
                                    }
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

            Console.WriteLine($"[DETECT] Field numbers: {string.Join(", ", fieldNumbers.OrderBy(x => x))}");
            if (field10InnerFieldNumbers.Count > 0)
                Console.WriteLine($"[DETECT] Field 10 inner: {string.Join(", ", field10InnerFieldNumbers.OrderBy(x => x))}");
            if (field20InnerFieldNumbers.Count > 0)
                Console.WriteLine($"[DETECT] Field 20 inner: {string.Join(", ", field20InnerFieldNumbers.OrderBy(x => x))}");

            // **DECISION LOGIC**

            // 1. Heartbeat (field 30)
            if (fieldNumbers.Contains(30) && !fieldNumbers.Contains(10) && !fieldNumbers.Contains(20))
            {
                if (_compiledMessages.TryGetValue("RsuToControllerMessageData", out var rsuMsg))
                {
                    Console.WriteLine("→ RsuToControllerMessageData (field 30 = heartbeat)");
                    return rsuMsg;
                }
            }

            // 2. IntersectionPassRequest (field 20 in RsuToController)
            //    Has fields like: request_id(1), intersection_id(2), request_type(10), request_timing(30), request_direction(40), requestor_info(50)
            if (fieldNumbers.Contains(20) && field20InnerFieldNumbers.Count > 0)
            {
                bool hasRequestId = field20InnerFieldNumbers.Contains(1);
                bool hasIntersectionId = field20InnerFieldNumbers.Contains(2);
                bool hasRequestType = field20InnerFieldNumbers.Contains(10);
                bool hasRequestorInfo = field20InnerFieldNumbers.Contains(50);

                // IntersectionPassRequest má typicky: 1, 2, 5, 10, 20, 30, 40, 50
                if ((hasRequestId || hasIntersectionId) && (hasRequestType || hasRequestorInfo))
                {
                    if (_compiledMessages.TryGetValue("RsuToControllerMessageData", out var rsuMsg))
                    {
                        Console.WriteLine("→ RsuToControllerMessageData (field 20 = intersection_request)");
                        return rsuMsg;
                    }
                }

                // IntersectionPassRequestStatus má: request_id(1), intersection_id(2), status(10), direction(30)
                bool hasStatus = field20InnerFieldNumbers.Contains(10);
                bool hasDirection = field20InnerFieldNumbers.Contains(30);

                if ((hasRequestId || hasIntersectionId) && hasStatus && !hasRequestorInfo)
                {
                    if (_compiledMessages.TryGetValue("ControllerToRsuMessageData", out var ctrlMsg))
                    {
                        Console.WriteLine("→ ControllerToRsuMessageData (field 20 = intersection_pass_request_status)");
                        return ctrlMsg;
                    }
                }
            }

            // 3. NearbyVehicleDetectionInfo (field 10) vs IntersectionStatus (field 10)
            if (fieldNumbers.Contains(10) && field10InnerFieldNumbers.Count > 0)
            {
                bool hasSpeed = field10InnerFieldNumbers.Contains(11);
                bool hasHeading = field10InnerFieldNumbers.Contains(12);
                bool hasVehicleInfo = field10InnerFieldNumbers.Contains(2);

                // NearbyVehicleDetectionInfo má: timestamp(1), vehicle_info(2), coordinates(10), speed(11), heading(12), distance(20)
                if ((hasSpeed || hasHeading || hasVehicleInfo))
                {
                    if (_compiledMessages.TryGetValue("RsuToControllerMessageData", out var rsuMsg))
                    {
                        Console.WriteLine("→ RsuToControllerMessageData (field 10 = nearby_vehicle_detection)");
                        return rsuMsg;
                    }
                }

                // IntersectionStatus má: timestamp(1), intersection_id(2), controller_status(10), intersection_lanes(20), movement_states(30)
                bool hasControllerStatus = field10InnerFieldNumbers.Contains(10);
                bool hasLanes = field10InnerFieldNumbers.Contains(20);
                bool hasMovementStates = field10InnerFieldNumbers.Contains(30);

                if (!hasSpeed && !hasHeading && (hasControllerStatus || hasLanes || hasMovementStates))
                {
                    if (_compiledMessages.TryGetValue("ControllerToRsuMessageData", out var ctrlMsg))
                    {
                        Console.WriteLine("→ ControllerToRsuMessageData (field 10 = intersection_status)");
                        return ctrlMsg;
                    }
                }
            }

            // Fallback: Scoring systém
            Console.WriteLine("[DETECT] Falling back to scoring system");
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
                        int score = testDecode.Count * 2;
                        int knownFields = testDecode.Count(kvp => !kvp.Key.StartsWith("field_") && kvp.Value != null);
                        score += knownFields * 3;

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

                        Console.WriteLine($"[DETECT] Scored {messageTypeName}: {score} points");
                    }
                    catch { }
                }
            }

            if (bestMessage != null)
            {
                Console.WriteLine($"→ Selected {bestMessage.Name} with score {bestScore}");
                return bestMessage;
            }

            return _compiledMessages.Values.FirstOrDefault();
        }

        public static string DecodeProtobufMessage(byte[] data, string protoDefinition, string inputFormat, string forceMessageType)
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

            ProtoMessage messageToUse = null;

            // If forceMessageType is specified, use it
            if (!string.IsNullOrEmpty(forceMessageType) && _compiledMessages.TryGetValue(forceMessageType, out messageToUse))
            {
                Console.WriteLine($"[DECODE] Using forced message type: {forceMessageType}");
            }
            else
            {
                // Otherwise auto-detect
                messageToUse = DetectMessageType(data);
                Console.WriteLine($"[DECODE] Auto-detected message type: {messageToUse?.Name ?? "null"}");
            }

            if (messageToUse != null)
            {
                var decodedObj = DecodeMessageToObject(data, messageToUse, 0);
                CleanDecodedObject(decodedObj);

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

        public static string DecodeProtobufMessage(byte[] data, string protoDefinition, string inputFormat = "Unknown")
        {
            return DecodeProtobufMessage(data, protoDefinition, inputFormat, null);
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
                }
                // Clean lists
                else if (kvp.Value is List<object> list)
                {
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (list[i] is Dictionary<string, object> listDict)
                        {
                            CleanDecodedObject(listDict);
                            // Don't remove from list even if empty after cleaning
                        }
                    }

                    // Remove list only if it was originally empty
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
                                {
                                    // Convert enum number to string name
                                    long enumNumber = (long)varint;
                                    string enumString = ConvertEnumNumberToString(fieldType, enumNumber);
                                    value = enumString != null ? (object)enumString : (object)enumNumber; // Fallback to number if unknown
                                }
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
                                        fieldType != "string" && fieldType != "bytes")
                                    {
                                        // Try exact match first
                                        if (_compiledMessages.TryGetValue(fieldType, out var nestedMessage))
                                        {
                                            try
                                            {
                                                Console.WriteLine($"[DECODE] Trying to decode as nested message '{fieldType}'");
                                                var nested = DecodeMessageToObject(bytes, nestedMessage, depth + 1);
                                                if (nested.Count > 0 && !nested.ContainsKey("_error"))
                                                {
                                                    value = nested;
                                                    decodedAsNested = true;
                                                }
                                            }
                                            catch { }
                                        }
                                        // Try short name match (e.g., "shared.intersection.v2.IntersectionPassRequest" → "IntersectionPassRequest")
                                        else if (fieldType.Contains("."))
                                        {
                                            string shortName = fieldType.Split('.').Last();
                                            if (_compiledMessages.TryGetValue(shortName, out var shortNameMessage))
                                            {
                                                try
                                                {
                                                    Console.WriteLine($"[DECODE] Trying to decode as nested message '{shortName}' (from full path '{fieldType}')");
                                                    var nested = DecodeMessageToObject(bytes, shortNameMessage, depth + 1);
                                                    if (nested.Count > 0 && !nested.ContainsKey("_error"))
                                                    {
                                                        value = nested;
                                                        decodedAsNested = true;
                                                    }
                                                }
                                                catch { }
                                            }
                                        }
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

        private static string ConvertEnumNumberToString(string enumType, long enumValue)
        {
            // Extract enum name from full type path (e.g., "shared.geo.v1.AccuracyLevelEnum" → "AccuracyLevelEnum")
            string enumName = enumType.Contains(".") ? enumType.Split('.').Last() : enumType;

            // Normalize to uppercase for comparison
            string normalizedEnumName = enumName.ToUpper();

            Console.WriteLine($"[DECODE ENUM] Parsing enum type '{enumType}' (normalized: '{normalizedEnumName}') value {enumValue}");

            // Direct mapping by exact enum type name
            Dictionary<long, string> mapping = null;

            // Check exact matches first (case insensitive)
            if (normalizedEnumName == "VEHICLETYPEENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "VEHICLE_TYPE_ENUM_INVALID",
                    [1] = "VEHICLE_TYPE_ENUM_UNKNOWN",
                    [10] = "VEHICLE_TYPE_ENUM_PEDESTRIAN",
                    [20] = "VEHICLE_TYPE_ENUM_CYCLIST",
                    [22] = "VEHICLE_TYPE_ENUM_MOTORCYCLE",
                    [30] = "VEHICLE_TYPE_ENUM_PASSENGER_CAR",
                    [40] = "VEHICLE_TYPE_ENUM_BUS",
                    [41] = "VEHICLE_TYPE_ENUM_VAN",
                    [42] = "VEHICLE_TYPE_ENUM_TRUCK",
                    [43] = "VEHICLE_TYPE_ENUM_TRAILER",
                    [50] = "VEHICLE_TYPE_ENUM_SPECIAL_VEHICLE",
                    [60] = "VEHICLE_TYPE_ENUM_TRAM",
                };
            }
            else if (normalizedEnumName == "VEHICLEROLEENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "VEHICLE_ROLE_ENUM_INVALID",
                    [1] = "VEHICLE_ROLE_ENUM_UNKNOWN",
                    [10] = "VEHICLE_ROLE_ENUM_PUBLIC_TRANSPORT",
                    [11] = "VEHICLE_ROLE_ENUM_SPECIAL_TRANSPORT",
                    [20] = "VEHICLE_ROLE_ENUM_DANGEROUS_GOODS",
                    [30] = "VEHICLE_ROLE_ENUM_ROAD_WORK",
                    [40] = "VEHICLE_ROLE_ENUM_RESCUE",
                    [41] = "VEHICLE_ROLE_ENUM_EMERGENCY",
                    [42] = "VEHICLE_ROLE_ENUM_SAFETY_CAR",
                    [50] = "VEHICLE_ROLE_ENUM_AGRICULTURE",
                    [60] = "VEHICLE_ROLE_ENUM_COMMERCIAL",
                    [70] = "VEHICLE_ROLE_ENUM_MILITARY",
                    [80] = "VEHICLE_ROLE_ENUM_ROAD_OPERATOR",
                    [90] = "VEHICLE_ROLE_ENUM_TAXI",
                };
            }
            else if (normalizedEnumName == "INTERSECTIONPASSREQUESTORROLEENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_INVALID",
                    [1] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_UNKNOWN",
                    [10] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_BASIC_VEHICLE",
                    [20] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_PUBLIC_TRANSPORT",
                    [30] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_SPECIAL_TRANSPORT",
                    [40] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_DANGEROUS_GOODS",
                    [50] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_ROAD_WORK",
                    [60] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_ROAD_RESCUE",
                    [70] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_EMERGENCY",
                    [80] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_SAFETY_CAR",
                    [200] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_CYCLIST",
                    [210] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_PEDESTRIAN",
                    [220] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_NON_MOTORIZED",
                    [230] = "INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_MILITARY",
                };
            }
            else if (normalizedEnumName == "INTERSECTIONPASSREQUESTORSUBROLEENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_INVALID",
                    [1] = "INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_UNKNOWN",
                    [10] = "INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_BUS",
                    [20] = "INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_TRAM",
                    [30] = "INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_METRO",
                    [40] = "INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_TRAIN",
                    [50] = "INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_EMERGENCY",
                    [110] = "INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_TROLLEYBUS",
                };
            }
            else if (normalizedEnumName == "INTERSECTIONPASSREQUESTTYPEENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "INTERSECTION_PASS_REQUEST_TYPE_ENUM_INVALID",
                    [1] = "INTERSECTION_PASS_REQUEST_TYPE_ENUM_UNKNOWN",
                    [20] = "INTERSECTION_PASS_REQUEST_TYPE_ENUM_PRIORITY_REQUEST",
                    [30] = "INTERSECTION_PASS_REQUEST_TYPE_ENUM_PRIORITY_REQUEST_UPDATE",
                    [40] = "INTERSECTION_PASS_REQUEST_TYPE_ENUM_PRIORITY_CANCELLATION",
                };
            }
            else if (normalizedEnumName == "INTERSECTIONPASSREQUESTIMPORTANCEENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_INVALID",
                    [1] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_UNKNOWN",
                    [10] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_01",
                    [20] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_02",
                    [30] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_03",
                    [40] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_04",
                    [50] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_05",
                    [60] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_06",
                    [70] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_07",
                    [80] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_08",
                    [90] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_09",
                    [100] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_10",
                    [110] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_11",
                    [120] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_12",
                    [130] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_13",
                    [140] = "INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_14",
                };
            }
            else if (normalizedEnumName == "TRANSITVEHICLEOCCUPANCYENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "TRANSIT_VEHICLE_OCCUPANCY_ENUM_INVALID",
                    [1] = "TRANSIT_VEHICLE_OCCUPANCY_ENUM_UNKNOWN",
                    [10] = "TRANSIT_VEHICLE_OCCUPANCY_ENUM_EMPTY",
                    [20] = "TRANSIT_VEHICLE_OCCUPANCY_ENUM_VERY_LOW",
                    [30] = "TRANSIT_VEHICLE_OCCUPANCY_ENUM_LOW",
                    [40] = "TRANSIT_VEHICLE_OCCUPANCY_ENUM_MEDIUM",
                    [50] = "TRANSIT_VEHICLE_OCCUPANCY_ENUM_HIGH",
                    [60] = "TRANSIT_VEHICLE_OCCUPANCY_ENUM_NEARLY_FULL",
                    [70] = "TRANSIT_VEHICLE_OCCUPANCY_ENUM_FULL",
                };
            }
            else if (normalizedEnumName == "ACCURACYLEVELENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "ACCURACY_LEVEL_ENUM_INVALID",
                    [1] = "ACCURACY_LEVEL_ENUM_UNKNOWN",
                    [10] = "ACCURACY_LEVEL_ENUM_LOW",
                    [20] = "ACCURACY_LEVEL_ENUM_MEDIUM",
                    [30] = "ACCURACY_LEVEL_ENUM_HIGH",
                    [40] = "ACCURACY_LEVEL_ENUM_HIGHEST",
                };
            }
            else if (normalizedEnumName == "PUBLICTRANSPORTVEHICLETYPE")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "PUBLIC_TRANSPORT_VEHICLE_TYPE_INVALID",
                    [1] = "PUBLIC_TRANSPORT_VEHICLE_TYPE_UNKNOWN",
                    [2] = "PUBLIC_TRANSPORT_VEHICLE_TYPE_OTHER",
                    [10] = "PUBLIC_TRANSPORT_VEHICLE_TYPE_BUS",
                    [20] = "PUBLIC_TRANSPORT_VEHICLE_TYPE_TRAM",
                    [30] = "PUBLIC_TRANSPORT_VEHICLE_TYPE_TRAIN",
                    [40] = "PUBLIC_TRANSPORT_VEHICLE_TYPE_METRO",
                    [50] = "PUBLIC_TRANSPORT_VEHICLE_TYPE_TROLLEYBUS",
                };
            }
            else if (normalizedEnumName == "INTERSECTIONCONTROLLERSTATUSENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "INTERSECTION_CONTROLLER_STATUS_ENUM_INVALID",
                    [1] = "INTERSECTION_CONTROLLER_STATUS_ENUM_UNKNOWN",
                    [10] = "INTERSECTION_CONTROLLER_STATUS_ENUM_MANUAL_CONTROL_IS_ENABLED",
                    [20] = "INTERSECTION_CONTROLLER_STATUS_ENUM_STOP_TIME_IS_ACTIVATED",
                    [30] = "INTERSECTION_CONTROLLER_STATUS_ENUM_FAILURE_FLASH",
                    [40] = "INTERSECTION_CONTROLLER_STATUS_ENUM_PREEMPT_IS_ACTIVE",
                    [50] = "INTERSECTION_CONTROLLER_STATUS_ENUM_SIGNAL_PRIORITY_IS_ACTIVE",
                    [60] = "INTERSECTION_CONTROLLER_STATUS_ENUM_FIXED_TIME_OPERATION",
                    [70] = "INTERSECTION_CONTROLLER_STATUS_ENUM_TRAFFIC_DEPENDENT_OPERATION",
                    [80] = "INTERSECTION_CONTROLLER_STATUS_ENUM_STANDBY_OPERATION",
                    [90] = "INTERSECTION_CONTROLLER_STATUS_ENUM_FAILURE_MODE",
                    [100] = "INTERSECTION_CONTROLLER_STATUS_ENUM_OFF",
                    [110] = "INTERSECTION_CONTROLLER_STATUS_ENUM_RECENT_MAP_MESSAGE_UPDATE",
                    [120] = "INTERSECTION_CONTROLLER_STATUS_ENUM_RECENT_CHANGE_IN_MAP_ASSIGNED_LANES_IDS_USED",
                    [130] = "INTERSECTION_CONTROLLER_STATUS_ENUM_NO_VALID_MAP_IS_AVAILABLE_AT_THIS_TIME",
                    [140] = "INTERSECTION_CONTROLLER_STATUS_ENUM_NO_VALID_SPAT_IS_AVAILABLE_AT_THIS_TIME",
                };
            }
            else if (normalizedEnumName == "TRAFFICLIGHTSIGNALSTATEENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_INVALID",
                    [1] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_UNKNOWN",
                    [20] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_DARK",
                    [30] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_STOP_THEN_PROCEED",
                    [40] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_STOP_AND_REMAIN",
                    [50] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PRE_MOVEMENT",
                    [60] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PERMISSIVE_MOVEMENT_ALLOWED",
                    [70] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PROTECTED_MOVEMENT_ALLOWED",
                    [80] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PERMISSIVE_CLEARANCE",
                    [90] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PROTECTED_CLEARANCE",
                    [100] = "TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_CAUTION_CONFLICTING_TRAFFIC",
                };
            }
            else if (normalizedEnumName == "INTERSECTIONPASSREQUESTSTATUSENUM")
            {
                mapping = new Dictionary<long, string>
                {
                    [0] = "INTERSECTION_PASS_REQUEST_STATUS_ENUM_INVALID",
                    [1] = "INTERSECTION_PASS_REQUEST_STATUS_ENUM_UNKNOWN",
                    [10] = "INTERSECTION_PASS_REQUEST_STATUS_ENUM_REQUESTED",
                    [20] = "INTERSECTION_PASS_REQUEST_STATUS_ENUM_PROCESSING",
                    [30] = "INTERSECTION_PASS_REQUEST_STATUS_ENUM_WATCH_OTHER_TRAFFIC",
                    [40] = "INTERSECTION_PASS_REQUEST_STATUS_ENUM_GRANTED",
                    [50] = "INTERSECTION_PASS_REQUEST_STATUS_ENUM_REJECTED",
                    [60] = "INTERSECTION_PASS_REQUEST_STATUS_ENUM_MAX_PRESENCE",
                    [70] = "INTERSECTION_PASS_REQUEST_STATUS_ENUM_RESERVICE_LOCKED",
                };
            }

            // Try to find value in mapping
            if (mapping != null && mapping.TryGetValue(enumValue, out string enumString))
            {
                Console.WriteLine($"[DECODE ENUM] {enumType} value {enumValue} → {enumString}");
                return enumString;
            }

            // Fallback: return null to use number
            Console.WriteLine($"[DECODE ENUM] Unknown enum '{enumType}' (normalized: '{normalizedEnumName}') value {enumValue}, using number");
            return null;
        }


        public static bool TryEncodeJsonToProtobuf(string jsonInput, bool useBase64, out string output, out string errorMessage)
        {
            output = string.Empty;
            errorMessage = string.Empty;

            try
            {
                // Clean up the JSON (remove comments and fix formatting)
                var cleanJson = CleanJsonForParsing(jsonInput);

                if (string.IsNullOrWhiteSpace(cleanJson))
                {
                    errorMessage = "No valid JSON found after cleaning";
                    return false;
                }

                // Parse JSON with more lenient settings
                Dictionary<string, object> jsonObject;
                try
                {
                    var jsonOptions = new JsonSerializerOptions
                    {
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        PropertyNameCaseInsensitive = false
                    };

                    jsonObject = JsonSerializer.Deserialize<Dictionary<string, object>>(cleanJson, jsonOptions);
                    if (jsonObject == null)
                    {
                        errorMessage = "Invalid JSON format - parsed as null";
                        return false;
                    }
                }
                catch (JsonException ex)
                {
                    errorMessage = $"JSON parsing error: {ex.Message}";
                    return false;
                }

                // Try to encode with each known message type
                foreach (var messageKvp in _compiledMessages)
                {
                    try
                    {
                        var bytes = EncodeMessageToBytes(jsonObject, messageKvp.Value);
                        if (bytes != null && bytes.Length > 0)
                        {
                            if (useBase64)
                            {
                                output = Convert.ToBase64String(bytes);
                                Console.WriteLine($"[ENCODE] Successfully encoded as {messageKvp.Key}: {bytes.Length} bytes -> Base64");
                            }
                            else
                            {
                                output = BitConverter.ToString(bytes).Replace("-", "");
                                Console.WriteLine($"[ENCODE] Successfully encoded as {messageKvp.Key}: {bytes.Length} bytes -> Hex");
                            }
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ENCODE] Failed to encode as {messageKvp.Key}: {ex.Message}");
                        // Try next message type
                        continue;
                    }
                }

                errorMessage = "JSON doesn't match any known message type. Please check the structure.";
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool TryEncodeJsonToProtobufWithType(string jsonInput, string messageTypeName, bool useBase64, out string output, out string errorMessage)
        {
            output = string.Empty;
            errorMessage = string.Empty;

            try
            {
                // Debug: List all available message types
                Console.WriteLine($"[ENCODE] Available message types: {string.Join(", ", _compiledMessages.Keys)}");
                Console.WriteLine($"[ENCODE] Looking for message type: {messageTypeName}");

                // Find the message type
                if (!_compiledMessages.TryGetValue(messageTypeName, out var messageType))
                {
                    errorMessage = $"Message type '{messageTypeName}' not found in compiled definitions.\n\nAvailable types:\n{string.Join("\n", _compiledMessages.Keys)}";
                    return false;
                }

                Console.WriteLine($"[ENCODE] Found message type '{messageTypeName}' with {messageType.Fields.Count} fields");

                // Clean up the JSON
                var cleanJson = CleanJsonForParsing(jsonInput);

                if (string.IsNullOrWhiteSpace(cleanJson))
                {
                    errorMessage = "No valid JSON found after cleaning";
                    return false;
                }

                // Parse JSON
                Dictionary<string, object> jsonObject;
                try
                {
                    var jsonOptions = new JsonSerializerOptions
                    {
                        AllowTrailingCommas = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        PropertyNameCaseInsensitive = false
                    };

                    jsonObject = JsonSerializer.Deserialize<Dictionary<string, object>>(cleanJson, jsonOptions);
                    if (jsonObject == null)
                    {
                        errorMessage = "Invalid JSON format - parsed as null";
                        return false;
                    }

                    Console.WriteLine($"[ENCODE] Parsed JSON with {jsonObject.Count} top-level fields: {string.Join(", ", jsonObject.Keys)}");
                }
                catch (JsonException ex)
                {
                    errorMessage = $"JSON parsing error: {ex.Message}";
                    return false;
                }

                // Encode using the specified message type
                try
                {
                    var bytes = EncodeMessageToBytes(jsonObject, messageType);
                    if (bytes != null && bytes.Length > 0)
                    {
                        if (useBase64)
                        {
                            output = Convert.ToBase64String(bytes);
                            Console.WriteLine($"[ENCODE] Successfully encoded as {messageTypeName}: {bytes.Length} bytes -> Base64");
                        }
                        else
                        {
                            output = BitConverter.ToString(bytes).Replace("-", "");
                            Console.WriteLine($"[ENCODE] Successfully encoded as {messageTypeName}: {bytes.Length} bytes -> Hex");
                        }
                        return true;
                    }
                    else
                    {
                        errorMessage = "Encoding produced empty result";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = $"Failed to encode as {messageTypeName}: {ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                    Console.WriteLine($"[ENCODE ERROR] {errorMessage}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static string CleanJsonForParsing(string jsonInput)
        {
            if (string.IsNullOrWhiteSpace(jsonInput))
                return string.Empty;

            // Split by lines and process
            var lines = jsonInput.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
            var cleanedLines = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                // Skip comment lines
                if (trimmed.StartsWith("//"))
                    continue;

                // Remove inline comments
                int commentIndex = line.IndexOf("//");
                if (commentIndex >= 0)
                {
                    var beforeComment = line.Substring(0, commentIndex).TrimEnd();
                    if (!string.IsNullOrWhiteSpace(beforeComment))
                        cleanedLines.Add(beforeComment);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    cleanedLines.Add(line);
                }
            }

            return string.Join("\n", cleanedLines);
        }

        private static byte[] EncodeMessageToBytes(Dictionary<string, object> jsonObject, ProtoMessage message)
        {
            Console.WriteLine($"[ENCODE] Encoding message '{message.Name}' with {jsonObject.Count} fields");

            using (var ms = new MemoryStream())
            {
                foreach (var field in message.Fields.OrderBy(f => f.Number))
                {
                    if (!jsonObject.TryGetValue(field.Name, out var value))
                    {
                        Console.WriteLine($"[ENCODE]   Field '{field.Name}' (#{field.Number}) - NOT FOUND in JSON");
                        continue;
                    }

                    if (value == null)
                    {
                        Console.WriteLine($"[ENCODE]   Field '{field.Name}' (#{field.Number}) - NULL value, skipping");
                        continue;
                    }

                    Console.WriteLine($"[ENCODE]   Field '{field.Name}' (#{field.Number}, type={field.Type}) - value type: {value.GetType().Name}");

                    // Handle JsonElement
                    if (value is JsonElement je)
                    {
                        Console.WriteLine($"[ENCODE]     JsonElement.ValueKind = {je.ValueKind}");

                        // Skip default/empty values EXCEPT for important fields
                        if (IsDefaultOrEmptyValue(je, field.Type))
                        {
                            // Check if this is an important field that should be encoded even if 0/default
                            bool isImportantField = field.Name.Contains("id") ||
                                field.Name.Contains("crc") ||
                                field.Name.Contains("seconds") ||
                                field.Name.Contains("nanos") ||
                                field.Name.Contains("number") ||
                                field.Name.Contains("revision");

                            if (!isImportantField)
                            {
                                Console.WriteLine($"[ENCODE]     Skipping (default/empty)");
                                continue;
                            }
                            else
                            {
                                Console.WriteLine($"[ENCODE]     Important field - encoding despite default value");
                            }
                        }

                        if (field.IsRepeated && je.ValueKind == JsonValueKind.Array)
                        {
                            int arrayLen = je.GetArrayLength();
                            Console.WriteLine($"[ENCODE]     Repeated field, array length: {arrayLen}");

                            if (arrayLen == 0)
                                continue;

                            foreach (var item in je.EnumerateArray())
                            {
                                WriteField(ms, field, item);
                            }
                        }
                        else
                        {
                            WriteField(ms, field, je);
                        }
                    }
                    else
                    {
                        // Direct object (not JsonElement)
                        if (IsDefaultOrEmptyValueDirect(value, field.Type))
                        {
                            // Check if this is an important field
                            bool isImportantField = field.Name.Contains("id") ||
                                                    field.Name.Contains("crc") ||
                                                    field.Name.Contains("seconds") ||
                                                    field.Name.Contains("nanos") ||
                                                    field.Name.Contains("number");

                            if (!isImportantField)
                            {
                                Console.WriteLine($"[ENCODE]     Skipping (default/empty direct)");
                                continue;
                            }
                            else
                            {
                                Console.WriteLine($"[ENCODE]     Important field - encoding despite default value");
                            }
                        }

                        WriteField(ms, field, value);
                    }
                }

                byte[] result = ms.ToArray();
                Console.WriteLine($"[ENCODE] Total encoded bytes: {result.Length}");
                return result;
            }
        }


        // Replace IsDefaultOrEmptyValue method (around line 825)
        private static bool IsDefaultOrEmptyValue(JsonElement je, string fieldType)
        {
            switch (je.ValueKind)
            {
                case JsonValueKind.Null:
                    return true;

                case JsonValueKind.String:
                    string strVal = je.GetString();

                    // Skip empty strings
                    if (string.IsNullOrEmpty(strVal))
                        return true;

                    // Skip ONLY _INVALID enum values (not _UNKNOWN!)
                    if (fieldType.Contains("Enum"))
                    {
                        if (strVal.EndsWith("_INVALID"))
                            return true;
                    }

                    return false;

                case JsonValueKind.Array:
                    return je.GetArrayLength() == 0;

                case JsonValueKind.Object:
                    return IsEmptyObject(je);

                case JsonValueKind.Number:
                    // Skip 0 values
                    double numVal = je.GetDouble();
                    return numVal == 0;

                case JsonValueKind.False:
                    // Skip false booleans
                    return true;

                case JsonValueKind.True:
                    return false;

                default:
                    return false;
            }
        }


        private static bool IsDefaultOrEmptyValueDirect(object value, string fieldType)
        {
            if (value == null)
                return true;

            if (value is string s)
            {
                // Skip empty strings
                if (string.IsNullOrEmpty(s))
                    return true;

                // Skip ONLY _INVALID enum values (not _UNKNOWN!)
                if (fieldType.Contains("Enum"))
                {
                    if (s.EndsWith("_INVALID"))
                        return true;
                }

                return false;
            }

            // Skip 0 numeric values
            if (value is int intVal && intVal == 0)
                return true;

            if (value is long longVal && longVal == 0)
                return true;

            if (value is float floatVal && floatVal == 0.0f)
                return true;

            if (value is double doubleVal && doubleVal == 0.0)
                return true;

            // Skip false booleans
            if (value is bool boolVal && !boolVal)
                return true;

            if (value is Dictionary<string, object> dict)
                return IsEmptyObjectDirect(dict);

            return false;
        }


        private static bool IsEmptyObjectDirect(Dictionary<string, object> dict)
        {
            if (dict == null || dict.Count == 0)
                return true;

            // Check if ALL values are empty/default
            foreach (var kvp in dict)
            {
                if (!IsDefaultOrEmptyValueDirect(kvp.Value, ""))
                    return false;
            }

            return true;
        }

        private static bool IsDefaultValue(object value, string type)
        {
            if (value == null) return true;

            if (value is JsonElement je)
            {
                return IsDefaultOrEmptyValue(je, type);
            }

            return IsDefaultOrEmptyValueDirect(value, type);
        }

        // Replace IsEmptyObject method (around line 950)
        private static bool IsEmptyObject(JsonElement je)
        {
            if (je.ValueKind != JsonValueKind.Object)
                return false;

            // Empty object {} is considered empty
            if (!je.EnumerateObject().Any())
                return true;

            // Check if ALL properties are empty/default
            foreach (var prop in je.EnumerateObject())
            {
                // Get field type from parent if available (for proper enum detection)
                // For now, just check if value is non-default
                var propValue = prop.Value;

                // Special check for non-empty values
                if (propValue.ValueKind == JsonValueKind.String)
                {
                    // Non-empty strings are NOT empty
                    if (!string.IsNullOrEmpty(propValue.GetString()))
                        return false;
                }
                else if (propValue.ValueKind == JsonValueKind.Number)
                {
                    // Any number is NOT empty (we encode all numbers)
                    return false;
                }
                else if (propValue.ValueKind == JsonValueKind.True || propValue.ValueKind == JsonValueKind.False)
                {
                    // Any boolean is NOT empty (we encode all booleans)
                    return false;
                }
                else if (propValue.ValueKind == JsonValueKind.Object)
                {
                    // Recursively check nested objects
                    if (!IsEmptyObject(propValue))
                        return false;
                }
                else if (propValue.ValueKind == JsonValueKind.Array)
                {
                    // Non-empty arrays are NOT empty
                    if (propValue.GetArrayLength() > 0)
                        return false;
                }
            }

            // All properties are empty
            return true;
        }

        private static void WriteField(MemoryStream ms, ProtoField field, object value)
        {
            // For nested messages, check if COMPLETELY empty before writing tag
            if (value is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                if (IsEmptyObject(je))
                {
                    Console.WriteLine($"[ENCODE]     Nested message is completely empty, skipping");
                    return; // Don't write empty nested messages
                }
            }
            else if (value is Dictionary<string, object> dict)
            {
                if (IsEmptyObjectDirect(dict))
                {
                    Console.WriteLine($"[ENCODE]     Nested message is completely empty (direct), skipping");
                    return; // Don't write empty nested messages
                }
            }

            int wireType = GetWireType(field.Type);
            ulong tag = ((ulong)field.Number << 3) | (ulong)wireType;

            Console.WriteLine($"[ENCODE]     Writing field tag: {tag} (field#{field.Number}, wireType={wireType})");

            WriteVarint(ms, tag);

            if (value is JsonElement jeVal)
            {
                WriteFieldValue(ms, field, jeVal);
            }
            else
            {
                WriteFieldValueDirect(ms, field, value);
            }
        }

        private static byte[] EncodeGoogleTimestamp(JsonElement je)
        {
            using (var ms = new MemoryStream())
            {
                if (je.TryGetProperty("seconds", out var secondsProp) && secondsProp.ValueKind == JsonValueKind.Number)
                {
                    long seconds = secondsProp.GetInt64();
                    if (seconds != 0)
                    {
                        WriteVarint(ms, (1 << 3) | 0); // Tag for field 1 (varint)
                        WriteVarint(ms, (ulong)seconds);
                    }
                }

                if (je.TryGetProperty("nanos", out var nanosProp) && nanosProp.ValueKind == JsonValueKind.Number)
                {
                    int nanos = nanosProp.GetInt32();
                    if (nanos != 0)
                    {
                        WriteVarint(ms, (2 << 3) | 0); // Tag for field 2 (varint)
                        WriteVarint(ms, (ulong)nanos);
                    }
                }

                return ms.ToArray();
            }
        }

        private static byte[] EncodeGoogleDoubleValue(JsonElement je)
        {
            using (var ms = new MemoryStream())
            {
                if (je.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.Number)
                {
                    double val = valueProp.GetDouble();
                    WriteVarint(ms, (1 << 3) | 1); // Tag for field 1 (64-bit)
                    byte[] wrapperBytes = BitConverter.GetBytes(val);
                    ms.Write(wrapperBytes, 0, wrapperBytes.Length);
                }
                return ms.ToArray();
            }
        }

        private static byte[] EncodeGoogleFloatValue(JsonElement je)
        {
            using (var ms = new MemoryStream())
            {
                if (je.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.Number)
                {
                    float val = valueProp.GetSingle();
                    WriteVarint(ms, (1 << 3) | 5); // Tag for field 1 (32-bit)
                    byte[] wrapperBytes = BitConverter.GetBytes(val);
                    ms.Write(wrapperBytes, 0, wrapperBytes.Length);
                }
                return ms.ToArray();
            }
        }

        private static void WriteFieldValue(MemoryStream ms, ProtoField field, JsonElement value)
        {
            switch (field.Type.ToLower())
            {
                case "int32":
                case "int64":
                case "uint32":
                case "uint64":
                    if (value.ValueKind == JsonValueKind.Number)
                    {
                        WriteVarint(ms, (ulong)value.GetInt64());
                    }
                    else if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out long parsed))
                    {
                        WriteVarint(ms, (ulong)parsed);
                    }
                    break;

                case "sint32":
                case "sint64":
                    if (value.ValueKind == JsonValueKind.Number)
                    {
                        long numValue = value.GetInt64();
                        WriteVarint(ms, EncodeZigZag(numValue));
                    }
                    break;

                case "bool":
                    bool boolVal = value.ValueKind == JsonValueKind.True ||
                                  (value.ValueKind == JsonValueKind.String && value.GetString()?.ToLower() == "true");
                    WriteVarint(ms, boolVal ? 1UL : 0UL);
                    break;

                case "float":
                    float floatVal = value.ValueKind == JsonValueKind.Number ? value.GetSingle() : 0f;
                    byte[] floatBytes = BitConverter.GetBytes(floatVal);
                    ms.Write(floatBytes, 0, floatBytes.Length);
                    break;

                case "double":
                    double doubleVal = value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0.0;
                    byte[] doubleBytes = BitConverter.GetBytes(doubleVal);
                    ms.Write(doubleBytes, 0, doubleBytes.Length);
                    break;

                case "string":
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        byte[] strBytes = Encoding.UTF8.GetBytes(value.GetString() ?? "");
                        WriteVarint(ms, (ulong)strBytes.Length);
                        ms.Write(strBytes, 0, strBytes.Length);
                    }
                    break;

                default:
                    // Handle enums (can be string or number)
                    if (field.Type.Contains("Enum"))
                    {
                        if (value.ValueKind == JsonValueKind.Number)
                        {
                            int enumNum = value.GetInt32();
                            Console.WriteLine($"[ENCODE]       Writing enum (numeric): {enumNum}");
                            WriteVarint(ms, (ulong)enumNum);
                        }
                        else if (value.ValueKind == JsonValueKind.String)
                        {
                            // Parse enum string to number
                            string enumString = value.GetString();
                            int enumValue = ParseEnumStringToNumber(enumString);
                            Console.WriteLine($"[ENCODE]       Writing enum (string '{enumString}'): {enumValue}");
                            WriteVarint(ms, (ulong)enumValue);
                        }
                    }
                    // Handle nested messages
                    else if (value.ValueKind == JsonValueKind.Object)
                    {
                        // Handle Google Protobuf wrapper types
                        if (field.Type.Contains("Timestamp"))
                        {
                            Console.WriteLine($"[ENCODE]       Encoding google.protobuf.Timestamp");
                            byte[] timestampBytes = EncodeGoogleTimestamp(value);
                            if (timestampBytes.Length > 0)
                            {
                                Console.WriteLine($"[ENCODE]       Writing {timestampBytes.Length} bytes for Timestamp");
                                WriteVarint(ms, (ulong)timestampBytes.Length);
                                ms.Write(timestampBytes, 0, timestampBytes.Length);
                            }
                            else
                            {
                                Console.WriteLine($"[ENCODE]       Timestamp is empty (0,0), writing 0 length");
                                WriteVarint(ms, 0); // Empty message
                            }
                        }
                        else if (field.Type.Contains("DoubleValue"))
                        {
                            Console.WriteLine($"[ENCODE]       Encoding google.protobuf.DoubleValue");
                            byte[] encodedDoubleValue = EncodeGoogleDoubleValue(value);
                            if (encodedDoubleValue.Length > 0)
                            {
                                WriteVarint(ms, (ulong)encodedDoubleValue.Length);
                                ms.Write(encodedDoubleValue, 0, encodedDoubleValue.Length);
                            }
                            else
                            {
                                WriteVarint(ms, 0);
                            }
                        }
                        else if (field.Type.Contains("FloatValue"))
                        {
                            Console.WriteLine($"[ENCODE]       Encoding google.protobuf.FloatValue");
                            byte[] encodedFloatValue = EncodeGoogleFloatValue(value);
                            if (encodedFloatValue.Length > 0)
                            {
                                WriteVarint(ms, (ulong)encodedFloatValue.Length);
                                ms.Write(encodedFloatValue, 0, encodedFloatValue.Length);
                            }
                            else
                            {
                                WriteVarint(ms, 0);
                            }
                        }
                        else if (field.Type.Contains("Duration"))
                        {
                            // Duration má stejnou strukturu jako Timestamp
                            Console.WriteLine($"[ENCODE]       Encoding google.protobuf.Duration");
                            byte[] durationBytes = EncodeGoogleTimestamp(value); // Reuse
                            if (durationBytes.Length > 0)
                            {
                                WriteVarint(ms, (ulong)durationBytes.Length);
                                ms.Write(durationBytes, 0, durationBytes.Length);
                            }
                            else
                            {
                                WriteVarint(ms, 0);
                            }
                        }
                        else
                        {
                            // Standard nested message handling
                            var matchingMessage = _compiledMessages.Values.FirstOrDefault(m =>
                                m.Name == field.Type ||
                                field.Type.EndsWith("." + m.Name));

                            if (matchingMessage != null)
                            {
                                Console.WriteLine($"[ENCODE]       Found nested message type: {matchingMessage.Name}");
                                var nestedDict = JsonSerializer.Deserialize<Dictionary<string, object>>(value.GetRawText());
                                byte[] nestedBytes = EncodeMessageToBytes(nestedDict, matchingMessage);

                                if (nestedBytes != null && nestedBytes.Length > 0)
                                {
                                    Console.WriteLine($"[ENCODE]       Writing {nestedBytes.Length} bytes for nested message");
                                    WriteVarint(ms, (ulong)nestedBytes.Length);
                                    ms.Write(nestedBytes, 0, nestedBytes.Length);
                                }
                                else
                                {
                                    Console.WriteLine($"[ENCODE]       Nested message encoded to 0 bytes, writing empty");
                                    WriteVarint(ms, 0);
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[ENCODE]       WARNING: No matching message type found for '{field.Type}'");
                            }
                        }
                    }
                    break;
            }
        }

        private static int ParseEnumStringToNumber(string enumString)
        {
            if (string.IsNullOrWhiteSpace(enumString))
                return 0;

            // If it's already a number, return it
            if (int.TryParse(enumString, out int directNumber))
                return directNumber;

            // Extract number from enum string like "VEHICLE_TYPE_ENUM_INVALID"
            // Pattern: ends with _<NUMBER> or just use hardcoded mappings

            // For INVALID enums, always return 0
            if (enumString.Contains("_INVALID") || enumString.Contains("INVALID"))
                return 0;

            if (enumString.Contains("_UNKNOWN") || enumString.Contains("UNKNOWN"))
                return 1;

            // Hardcoded common values (extend as needed)
            var knownEnums = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                // VehicleTypeEnum
                ["VEHICLE_TYPE_ENUM_INVALID"] = 0,
                ["VEHICLE_TYPE_ENUM_UNKNOWN"] = 1,
                ["VEHICLE_TYPE_ENUM_PEDESTRIAN"] = 10,
                ["VEHICLE_TYPE_ENUM_CYCLIST"] = 20,
                ["VEHICLE_TYPE_ENUM_MOTORCYCLE"] = 22,
                ["VEHICLE_TYPE_ENUM_PASSENGER_CAR"] = 30,
                ["VEHICLE_TYPE_ENUM_BUS"] = 40,
                ["VEHICLE_TYPE_ENUM_VAN"] = 41,
                ["VEHICLE_TYPE_ENUM_TRUCK"] = 42,
                ["VEHICLE_TYPE_ENUM_TRAILER"] = 43,
                ["VEHICLE_TYPE_ENUM_SPECIAL_VEHICLE"] = 50,
                ["VEHICLE_TYPE_ENUM_TRAM"] = 60,

                // VehicleRoleEnum
                ["VEHICLE_ROLE_ENUM_INVALID"] = 0,
                ["VEHICLE_ROLE_ENUM_UNKNOWN"] = 1,
                ["VEHICLE_ROLE_ENUM_PUBLIC_TRANSPORT"] = 10,
                ["VEHICLE_ROLE_ENUM_SPECIAL_TRANSPORT"] = 11,
                ["VEHICLE_ROLE_ENUM_DANGEROUS_GOODS"] = 20,
                ["VEHICLE_ROLE_ENUM_ROAD_WORK"] = 30,
                ["VEHICLE_ROLE_ENUM_RESCUE"] = 40,
                ["VEHICLE_ROLE_ENUM_EMERGENCY"] = 41,
                ["VEHICLE_ROLE_ENUM_SAFETY_CAR"] = 42,
                ["VEHICLE_ROLE_ENUM_AGRICULTURE"] = 50,
                ["VEHICLE_ROLE_ENUM_COMMERCIAL"] = 60,
                ["VEHICLE_ROLE_ENUM_MILITARY"] = 70,
                ["VEHICLE_ROLE_ENUM_ROAD_OPERATOR"] = 80,
                ["VEHICLE_ROLE_ENUM_TAXI"] = 90,

                // PublicTransportVehicleType
                ["PUBLIC_TRANSPORT_VEHICLE_TYPE_INVALID"] = 0,
                ["PUBLIC_TRANSPORT_VEHICLE_TYPE_UNKNOWN"] = 1,
                ["PUBLIC_TRANSPORT_VEHICLE_TYPE_OTHER"] = 2,
                ["PUBLIC_TRANSPORT_VEHICLE_TYPE_BUS"] = 10,
                ["PUBLIC_TRANSPORT_VEHICLE_TYPE_TRAM"] = 20,
                ["PUBLIC_TRANSPORT_VEHICLE_TYPE_TRAIN"] = 30,
                ["PUBLIC_TRANSPORT_VEHICLE_TYPE_METRO"] = 40,
                ["PUBLIC_TRANSPORT_VEHICLE_TYPE_TROLLEYBUS"] = 50,

                // AccuracyLevelEnum
                ["ACCURACY_LEVEL_ENUM_INVALID"] = 0,
                ["ACCURACY_LEVEL_ENUM_UNKNOWN"] = 1,
                ["ACCURACY_LEVEL_ENUM_LOW"] = 10,
                ["ACCURACY_LEVEL_ENUM_MEDIUM"] = 20,
                ["ACCURACY_LEVEL_ENUM_HIGH"] = 30,
                ["ACCURACY_LEVEL_ENUM_HIGHEST"] = 40,

                // IntersectionPassRequestTypeEnum
                ["INTERSECTION_PASS_REQUEST_TYPE_ENUM_INVALID"] = 0,
                ["INTERSECTION_PASS_REQUEST_TYPE_ENUM_UNKNOWN"] = 1,
                ["INTERSECTION_PASS_REQUEST_TYPE_ENUM_PRIORITY_REQUEST"] = 20,
                ["INTERSECTION_PASS_REQUEST_TYPE_ENUM_PRIORITY_REQUEST_UPDATE"] = 30,
                ["INTERSECTION_PASS_REQUEST_TYPE_ENUM_PRIORITY_CANCELLATION"] = 40,

                // IntersectionPassRequestorRoleEnum
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_INVALID"] = 0,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_UNKNOWN"] = 1,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_BASIC_VEHICLE"] = 10,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_PUBLIC_TRANSPORT"] = 20,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_SPECIAL_TRANSPORT"] = 30,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_DANGEROUS_GOODS"] = 40,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_ROAD_WORK"] = 50,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_ROAD_RESCUE"] = 60,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_EMERGENCY"] = 70,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_SAFETY_CAR"] = 80,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_CYCLIST"] = 200,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_PEDESTRIAN"] = 210,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_NON_MOTORIZED"] = 220,
                ["INTERSECTION_PASS_REQUESTOR_ROLE_ENUM_MILITARY"] = 230,

                // IntersectionPassRequestorSubRoleEnum
                ["INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_INVALID"] = 0,
                ["INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_UNKNOWN"] = 1,
                ["INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_BUS"] = 10,
                ["INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_TRAM"] = 20,
                ["INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_METRO"] = 30,
                ["INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_TRAIN"] = 40,
                ["INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_EMERGENCY"] = 50,
                ["INTERSECTION_PASS_REQUESTOR_SUB_ROLE_ENUM_TROLLEYBUS"] = 110,

                // IntersectionPassRequestImportanceEnum
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_INVALID"] = 0,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_UNKNOWN"] = 1,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_01"] = 10,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_02"] = 20,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_03"] = 30,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_04"] = 40,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_05"] = 50,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_06"] = 60,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_07"] = 70,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_08"] = 80,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_09"] = 90,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_10"] = 100,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_11"] = 110,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_12"] = 120,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_13"] = 130,
                ["INTERSECTION_PASS_REQUEST_IMPORTANCE_ENUM_LEVEL_14"] = 140,

                // TransitVehicleOccupancyEnum
                ["TRANSIT_VEHICLE_OCCUPANCY_ENUM_INVALID"] = 0,
                ["TRANSIT_VEHICLE_OCCUPANCY_ENUM_UNKNOWN"] = 1,
                ["TRANSIT_VEHICLE_OCCUPANCY_ENUM_EMPTY"] = 10,
                ["TRANSIT_VEHICLE_OCCUPANCY_ENUM_VERY_LOW"] = 20,
                ["TRANSIT_VEHICLE_OCCUPANCY_ENUM_LOW"] = 30,
                ["TRANSIT_VEHICLE_OCCUPANCY_ENUM_MEDIUM"] = 40,
                ["TRANSIT_VEHICLE_OCCUPANCY_ENUM_HIGH"] = 50,
                ["TRANSIT_VEHICLE_OCCUPANCY_ENUM_NEARLY_FULL"] = 60,
                ["TRANSIT_VEHICLE_OCCUPANCY_ENUM_FULL"] = 70,

                // IntersectionControllerStatusEnum
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_INVALID"] = 0,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_UNKNOWN"] = 1,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_MANUAL_CONTROL_IS_ENABLED"] = 10,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_STOP_TIME_IS_ACTIVATED"] = 20,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_FAILURE_FLASH"] = 30,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_PREEMPT_IS_ACTIVE"] = 40,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_SIGNAL_PRIORITY_IS_ACTIVE"] = 50,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_FIXED_TIME_OPERATION"] = 60,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_TRAFFIC_DEPENDENT_OPERATION"] = 70,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_STANDBY_OPERATION"] = 80,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_FAILURE_MODE"] = 90,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_OFF"] = 100,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_RECENT_MAP_MESSAGE_UPDATE"] = 110,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_RECENT_CHANGE_IN_MAP_ASSIGNED_LANES_IDS_USED"] = 120,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_NO_VALID_MAP_IS_AVAILABLE_AT_THIS_TIME"] = 130,
                ["INTERSECTION_CONTROLLER_STATUS_ENUM_NO_VALID_SPAT_IS_AVAILABLE_AT_THIS_TIME"] = 140,

                // TrafficLightSignalStateEnum
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_INVALID"] = 0,
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_UNKNOWN"] = 1,
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_DARK"] = 20,
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_STOP_THEN_PROCEED"] = 30,
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_STOP_AND_REMAIN"] = 40,
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PRE_MOVEMENT"] = 50,
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PERMISSIVE_MOVEMENT_ALLOWED"] = 60,
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PROTECTED_MOVEMENT_ALLOWED"] = 70,
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PERMISSIVE_CLEARANCE"] = 80,
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_PROTECTED_CLEARANCE"] = 90,
                ["TRAFFIC_LIGHT_SIGNAL_STATE_ENUM_CAUTION_CONFLICTING_TRAFFIC"] = 100,

                // IntersectionPassRequestStatusEnum
                ["INTERSECTION_PASS_REQUEST_STATUS_ENUM_INVALID"] = 0,
                ["INTERSECTION_PASS_REQUEST_STATUS_ENUM_UNKNOWN"] = 1,
                ["INTERSECTION_PASS_REQUEST_STATUS_ENUM_REQUESTED"] = 10,
                ["INTERSECTION_PASS_REQUEST_STATUS_ENUM_PROCESSING"] = 20,
                ["INTERSECTION_PASS_REQUEST_STATUS_ENUM_WATCH_OTHER_TRAFFIC"] = 30,
                ["INTERSECTION_PASS_REQUEST_STATUS_ENUM_GRANTED"] = 40,
                ["INTERSECTION_PASS_REQUEST_STATUS_ENUM_REJECTED"] = 50,
                ["INTERSECTION_PASS_REQUEST_STATUS_ENUM_MAX_PRESENCE"] = 60,
                ["INTERSECTION_PASS_REQUEST_STATUS_ENUM_RESERVICE_LOCKED"] = 70,
            };

            if (knownEnums.TryGetValue(enumString.ToUpper(), out int enumValue))
            {
                Console.WriteLine($"[ENUM] Parsed '{enumString}' -> {enumValue}");
                return enumValue;
            }

            Console.WriteLine($"[ENUM] Unknown enum string '{enumString}', defaulting to 0");
            return 0; // Default to INVALID
        }

        private static void WriteFieldValueDirect(MemoryStream ms, ProtoField field, object value)
        {
            // Handle string enum values
            if (value is string strValue)
            {
                // Check if it's an enum field
                if (field.Type.Contains("Enum"))
                {
                    int enumValue = ParseEnumStringToNumber(strValue);
                    WriteVarint(ms, (ulong)enumValue);
                    return;
                }

                // Otherwise it's a regular string
                byte[] strBytes = Encoding.UTF8.GetBytes(strValue);
                WriteVarint(ms, (ulong)strBytes.Length);
                ms.Write(strBytes, 0, strBytes.Length);
            }
            else if (value is Dictionary<string, object> dict)
            {
                var matchingMessage = _compiledMessages.Values.FirstOrDefault(m =>
                    m.Name == field.Type ||
                    field.Type.EndsWith("." + m.Name));

                if (matchingMessage != null)
                {
                    byte[] nestedBytes = EncodeMessageToBytes(dict, matchingMessage);
                    WriteVarint(ms, (ulong)nestedBytes.Length);
                    ms.Write(nestedBytes, 0, nestedBytes.Length);
                }
            }
            else if (value is long l)
            {
                WriteVarint(ms, (ulong)l);
            }
            else if (value is int i)
            {
                WriteVarint(ms, (ulong)i);
            }
            else if (value is bool b)
            {
                WriteVarint(ms, b ? 1UL : 0UL);
            }
            else if (value is double d)
            {
                byte[] doubleBytes = BitConverter.GetBytes(d);
                ms.Write(doubleBytes, 0, doubleBytes.Length);
            }
            else if (value is float f)
            {
                byte[] floatBytes = BitConverter.GetBytes(f);
                ms.Write(floatBytes, 0, floatBytes.Length);
            }
        }

        private static int GetWireType(string type)
        {
            switch (type.ToLower())
            {
                case "int32":
                case "int64":
                case "uint32":
                case "uint64":
                case "sint32":
                case "sint64":
                case "bool":
                    return 0; // Varint

                case "fixed64":
                case "sfixed64":
                case "double":
                    return 1; // 64-bit

                case "string":
                case "bytes":
                    return 2; // Length-delimited

                case "fixed32":
                case "sfixed32":
                case "float":
                    return 5; // 32-bit

                default:
                    // Nested messages and enums
                    if (type.Contains("Enum"))
                        return 0; // Varint for enums
                    else
                        return 2; // Length-delimited for messages
            }
        }

        private static void WriteVarint(MemoryStream ms, ulong value)
        {
            while (value >= 0x80)
            {
                ms.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }
            ms.WriteByte((byte)value);
        }

        private static ulong EncodeZigZag(long value)
        {
            return (ulong)((value << 1) ^ (value >> 63));
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

        public static bool TryDecodeProtobufFromHex(string input, out string decoded, string forceMessageType = null)
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

                if (input.Contains('\n') || input.Contains('\r'))
                {
                    decoded = DecodeMultipleProtobufMessages(input, protoDefinition);
                    return !string.IsNullOrWhiteSpace(decoded);
                }

                string timestamp = ExtractTimestamp(input);
                input = CleanInput(input);
                input = input.Replace(" ", "").Replace("\t", "");

                byte[] data = null;
                string detectedFormat = "Unknown";

                if (IsBase64Input(input))
                {
                    try { data = Convert.FromBase64String(input); detectedFormat = "Base64"; }
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
                            data[i] = Convert.ToByte(input.Substring(i * 2, 2), 16);
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

                string json = DecodeProtobufMessage(data, protoDefinition, detectedFormat, forceMessageType);

                if (!string.IsNullOrWhiteSpace(timestamp))
                    decoded = $"{timestamp} {json}";
                else
                    decoded = $"{json}";

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