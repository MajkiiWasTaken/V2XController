using System;
using System.Text.Json;
using System.Windows.Media;

namespace V2XController
{
    /// <summary>
    /// Class for parsing Protobuf SRV messages (Heartbeat)
    /// </summary>
    internal class ProtoSrv
    {
        public DateTime? Timestamp { get; set; }
        public string? DeviceId { get; set; }
        public int? Crc { get; set; }

        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? Altitude { get; set; }
        public double? AccuracyInMeters { get; set; }

        #region Parsing messages

        /// <summary>
        /// Parses decoded Protobuf JSON into ProtoSrv object
        /// </summary>
        public static ProtoSrv? ParseFromJson(string decodedJson)
        {
            if (string.IsNullOrWhiteSpace(decodedJson))
                return null;

            try
            {
                using var jsonDoc = JsonDocument.Parse(decodedJson);
                var root = jsonDoc.RootElement;

                var protoSrv = new ProtoSrv();

                // Parse CRC
                if (root.TryGetProperty("crc", out var crcProp))
                    protoSrv.Crc = crcProp.GetInt32();

                // Parse timestamp
                if (root.TryGetProperty("timestamp", out var tsProp))
                {
                    if (tsProp.TryGetProperty("seconds", out var secProp))
                    {
                        long seconds = secProp.GetInt64();
                        protoSrv.Timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                    }
                }

                // Parse device_id
                if (root.TryGetProperty("device_id", out var deviceIdProp))
                {
                    if (deviceIdProp.ValueKind == JsonValueKind.String)
                        protoSrv.DeviceId = deviceIdProp.GetString();
                    else if (deviceIdProp.ValueKind == JsonValueKind.Number)
                        protoSrv.DeviceId = deviceIdProp.GetInt32().ToString();
                }

                // Parse heartbeat
                if (root.TryGetProperty("heartbeat", out var hbProp))
                {
                    // Device ID from heartbeat (override if present)
                    if (hbProp.TryGetProperty("device_id", out var hbDeviceIdProp))
                    {
                        var deviceId = hbDeviceIdProp.GetString();
                        if (!string.IsNullOrEmpty(deviceId))
                            protoSrv.DeviceId = deviceId;
                    }

                    // Timestamp from heartbeat (override if present)
                    if (hbProp.TryGetProperty("timestamp", out var hbTsProp))
                    {
                        if (hbTsProp.TryGetProperty("seconds", out var hbSecProp))
                        {
                            long seconds = hbSecProp.GetInt64();
                            if (seconds > 0)
                                protoSrv.Timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                        }
                    }

                    // Position data
                    if (hbProp.TryGetProperty("position", out var posProp))
                    {
                        // Latitude
                        if (posProp.TryGetProperty("latitude", out var latProp))
                            protoSrv.Latitude = latProp.GetDouble();

                        // Longitude
                        if (posProp.TryGetProperty("longitude", out var lonProp))
                            protoSrv.Longitude = lonProp.GetDouble();

                        // Altitude
                        if (posProp.TryGetProperty("altitude", out var altProp))
                        {
                            if (altProp.TryGetProperty("value", out var altValProp))
                                protoSrv.Altitude = altValProp.GetDouble();
                        }

                        // Accuracy
                        if (posProp.TryGetProperty("accuracy", out var accProp))
                        {
                            // Try accuracy_in_meters first
                            if (accProp.TryGetProperty("accuracy_in_meters", out var accInMProp))
                            {
                                if (accInMProp.TryGetProperty("value", out var accValProp))
                                    protoSrv.AccuracyInMeters = accValProp.GetDouble();
                            }
                            // Fallback to raw_confidence_data
                            else if (accProp.TryGetProperty("raw_confidence_data", out var rawConf))
                            {
                                if (rawConf.TryGetProperty("semi_major_axis_length", out var semiMajor))
                                {
                                    if (semiMajor.TryGetProperty("value", out var valProp))
                                        protoSrv.AccuracyInMeters = valProp.GetDouble();
                                }
                            }
                        }
                    }
                }

                return protoSrv;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProtoSrv] Parse error: {ex.Message}");
                return null;
            }
        }

        #endregion

        /// <summary>
        /// Converts ProtoSrv to SRVMessage for display on map
        /// </summary>
        public SRVMessage ToSrvMessage()
        {
            return new SRVMessage
            {
                Latitude = Latitude ?? 0.0,
                Longitude = Longitude ?? 0.0,
                Dt = Timestamp ?? DateTime.UtcNow
            };
        }
    }
}