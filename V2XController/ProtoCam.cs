using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Threading.Tasks;

/**********************************************************************************************************
 * ProtoCam.xaml.cs
 * Author: Michal Svrcek
 * Version: 1.0.0
 *
 * Description: Class for parsing Protobuf CAM messages
 *
 * Copyright (c) 2026 Hrosi stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/

namespace V2XController
{
    /* * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * 
     * For example:                                                                                                                                *
     *                                                                                                                                             *
     * Whole Protobuf CAM message:                                                                                                                 *
     * -------------------------------------------------------------------------------------------------                                           *
     * |  COvDAxILCJrg8c8GEKTWiiFShwEKCwia4PHPBhCk1oohEhAKCjIzNTg5NDUwMTcQPBgKUjoKCQkAAACgmRlsQBEBP/   |                                           *
     * |  Tu2etIQBnCNdKtMkgyQFIbUB6iAQUN4XqkQJIDDgoFDeF6pEASBQ3heqRAXTMzP0FlZuaqQ6UBuKeAQ/             |                                           *
     * |  IBGQgUogEEMTMzOfIBBDIwMDSABQDCDAPwAQA=                                                       |                                           *
     * -------------------------------------------------------------------------------------------------                                           *
     *                                                                                                                                             *
     * translated into JSON (Nearby Vehicle Detecion [field 10], message RsuToControllerMessageData,                                               *
     *                       File: prodin_infrastructure.proto, INTENS Corporation s.r.o.):                                                        *
     *                                                                                                                                             *
     * --------------------------------------------------------------------------------------------------------------------------------            *
     * |  {"crc":57835,"timestamp":{"seconds":1778151450,"nanos":69380900                                                             |            *
     * |  "nearby_vehicle_detection":{"timestamp":{"seconds":1778151450,"nanos":69380900},                                            |            *
     * |  "vehicle_info":{"vehicle_id":"2358945017","vehicle_type":"VEHICLE_TYPE_ENUM_TRAM",                                          |            *
     * |  "vehicle_role":"VEHICLE_ROLE_ENUM_PUBLIC_TRANSPORT"},"coordinates":{"latitude":49.842588299999996,"longitude":18.2820233,   |            *
     * |  "accuracy":{"accuracy_level":"ACCURACY_LEVEL_ENUM_HIGH","raw_confidence_data":{"semi_major_axis_length":{"value":5.14},     |            *
     * |  "semi_minor_axis_length":{"value":5.14}}}},"speed":11.95,"heading":341.8,"distance":257.31,                                 |            *
     * |  "public_transport_vehicle_data":{"vehicle_type":20,"vehicle_number":"1339","line_number":"2004",                            |            *
     * |  "passengers_count":0,"vehicle_status_flags":{"is_passenger_boarding_and_disembarking_in_progress":false}}}}                 |            *
     * --------------------------------------------------------------------------------------------------------------------------------            *
     * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * */

    /// <summary>
    /// Class for parsing Protobuf CAM messages
    /// </summary>
    internal class ProtoCam
    {
        public DateTime? Timestamp { get; set; }

        public string? VehicleId { get; set; }
        public int? VehicleNumber { get; set; }
        public int? LineNumber { get; set; }
        public int? PassengerCount { get; set; }
        public int? Crc { get; set; }

        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? Speed { get; set; }
        public double? Heading { get; set; } 
        public double? AccuracyInMeters { get; set; }
        public double? DistanceFromRsu { get; set; }
        public bool? IsManual { get; set; } = false;

        public VehicleTypeEnum VehicleType { get; set; }
        public PrioritizationVehicleRoleEnum VehicleRole { get; set; }


        #region Parsing messages

        /// <summary>
        /// Parses decoded Protobuf JSON into ProtoCam object
        /// </summary>
        public static ProtoCam? ParseFromJson(string decodedJson)
        {
            if (string.IsNullOrWhiteSpace(decodedJson))
                return null;

            try
            {
                using var jsonDoc = JsonDocument.Parse(decodedJson);
                var root = jsonDoc.RootElement;

                var protoCam = new ProtoCam();

                // Parse CRC
                if (root.TryGetProperty("crc", out var crcProp))
                    protoCam.Crc = crcProp.GetInt32();

                // Parse timestamp
                if (root.TryGetProperty("timestamp", out var tsProp))
                {
                    if (tsProp.TryGetProperty("seconds", out var secProp))
                    {
                        long seconds = secProp.GetInt64();
                        protoCam.Timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                    }
                }

                // Parse nearby_vehicle_detection
                if (root.TryGetProperty("nearby_vehicle_detection", out var nvdProp))
                {
                    // Vehicle info
                    if (nvdProp.TryGetProperty("vehicle_info", out var vehInfo))
                    {
                        if (vehInfo.TryGetProperty("vehicle_id", out var vidProp))
                        {
                            var vidStr = vidProp.GetString();
                            if (!string.IsNullOrWhiteSpace(vidStr) && vidStr != "-1")
                                protoCam.VehicleId = vidStr;
                        }

                        if (vehInfo.TryGetProperty("vehicle_type", out var vtProp))
                        {
                            var vtStr = vtProp.GetString();
                            if (Enum.TryParse<VehicleTypeEnum>(vtStr, true, out var vt))
                                protoCam.VehicleType = vt;
                        }

                        if (vehInfo.TryGetProperty("vehicle_role", out var vrProp))
                        {
                            var vrStr = vrProp.GetString();
                            // Map from proto enum to our enum
                            if (vrStr?.Contains("PUBLIC_TRANSPORT") == true)
                                protoCam.VehicleRole = PrioritizationVehicleRoleEnum.PRIORITIZATION_VEHICLE_ROLE_ENUM_PUBLIC_TRANSPORT;
                            else if (vrStr?.Contains("EMERGENCY") == true)
                                protoCam.VehicleRole = PrioritizationVehicleRoleEnum.PRIORITIZATION_VEHICLE_ROLE_ENUM_EMERGENCY;
                        }
                    }

                    // Coordinates
                    if (nvdProp.TryGetProperty("coordinates", out var coords))
                    {
                        if (coords.TryGetProperty("latitude", out var latProp))
                            protoCam.Latitude = latProp.GetDouble();

                        if (coords.TryGetProperty("longitude", out var lonProp))
                            protoCam.Longitude = lonProp.GetDouble();

                        // Accuracy
                        if (coords.TryGetProperty("accuracy", out var accProp))
                        {
                            if (accProp.TryGetProperty("raw_confidence_data", out var rawConf))
                            {
                                if (rawConf.TryGetProperty("semi_major_axis_length", out var semiMajor))
                                {
                                    if (semiMajor.TryGetProperty("value", out var valProp))
                                        protoCam.AccuracyInMeters = valProp.GetDouble();
                                }
                            }
                        }
                    }

                    // Speed (km/h)
                    if (nvdProp.TryGetProperty("speed", out var spdProp))
                        protoCam.Speed = spdProp.GetDouble();

                    // Heading (degrees)
                    if (nvdProp.TryGetProperty("heading", out var hdgProp))
                        protoCam.Heading = hdgProp.GetDouble();

                    // Distance from RSU
                    if (nvdProp.TryGetProperty("distance", out var distProp))
                        protoCam.DistanceFromRsu = distProp.GetDouble();

                    // Public transport data
                    if (nvdProp.TryGetProperty("public_transport_vehicle_data", out var ptData))
                    {
                        if (ptData.TryGetProperty("vehicle_number", out var vnProp))
                        {
                            if (int.TryParse(vnProp.GetString(), out int vn) && vn >= 0)
                                protoCam.VehicleNumber = vn;
                        }

                        if (ptData.TryGetProperty("line_number", out var lnProp))
                        {
                            if (int.TryParse(lnProp.GetString(), out int ln) && ln >= 0)
                                protoCam.LineNumber = ln;
                        }

                        if (ptData.TryGetProperty("passengers_count", out var pcProp))
                            protoCam.PassengerCount = pcProp.GetInt32();
                    }
                }

                return protoCam;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProtoCam] Parse error: {ex.Message}");
                return null;
            }

            #endregion
        }

        /// <summary>
        /// Converts ProtoCam to V2XMessage for display on map
        /// </summary>
        public V2XMessage ToV2XMessage()
        {
            return new V2XMessage
            {
                VehicleID = VehicleId ?? VehicleNumber?.ToString() ?? "Unknown",
                Timestamp = Timestamp ?? DateTime.UtcNow,
                Latitude = Latitude ?? 0.0,
                Longitude = Longitude ?? 0.0,
                Speed = Speed ?? 0.0,
                Heading = Heading ?? 0.0,
                MessageType = "CAM",
                IsManual = IsManual ?? false
            };
        }
    }

    #region Enums
    /// <summary>
    /// Specifies the types of vehicles recognized by the system.
    /// </summary>
    public enum VehicleTypeEnum
    {
        VEHICLE_TYPE_ENUM_INVALID,
        VEHICLE_TYPE_ENUM_UNKNOWN,
        VEHICLE_TYPE_ENUM_AGRICULTURAL_VEHICLE,
        VEHICLE_TYPE_ENUM_ANY_VEHICLE,
        VEHICLE_TYPE_ENUM_ARTICULATED_BUS,
        VEHICLE_TYPE_ENUM_ARTICULATED_TROLLEY_BUS,
        VEHICLE_TYPE_ENUM_ARTICULATED_VEHICLE,
        VEHICLE_TYPE_ENUM_BICYCLE,
        VEHICLE_TYPE_ENUM_BUS,
        VEHICLE_TYPE_ENUM_CAR,
        VEHICLE_TYPE_ENUM_CARAVAN,
        VEHICLE_TYPE_ENUM_CAR_OR_LIGHT_VEHICLE,
        VEHICLE_TYPE_ENUM_CAR_WITH_CARAVAN,
        VEHICLE_TYPE_ENUM_CAR_WITH_TRAILER,
        VEHICLE_TYPE_ENUM_CONSTRUCTION_OR_MAINTENANCE_VEHICLE,
        VEHICLE_TYPE_ENUM_FOUR_WHEEL_DRIVE,
        VEHICLE_TYPE_ENUM_HEAVY_GOODS_VEHICLE,
        VEHICLE_TYPE_ENUM_HEAVY_GOODS_VEHICLE_WITH_TRAILER,
        VEHICLE_TYPE_ENUM_HEAVY_DUTY_TRANSPORTER,
        VEHICLE_TYPE_ENUM_HEAVY_VEHICLE,
        VEHICLE_TYPE_ENUM_HIGH_SIDED_VEHICLE,
        VEHICLE_TYPE_ENUM_LIGHT_COMMERCIAL_VEHICLE,
        VEHICLE_TYPE_ENUM_LARGE_CAR,
        VEHICLE_TYPE_ENUM_LARGE_GOODS_VEHICLE,
        VEHICLE_TYPE_ENUM_LIGHT_COMMERCIAL_VEHICLE_WITH_TRAILER,
        VEHICLE_TYPE_ENUM_LONG_HEAVY_LORRY,
        VEHICLE_TYPE_ENUM_LORRY,
        VEHICLE_TYPE_ENUM_METRO,
        VEHICLE_TYPE_ENUM_MINIBUS,
        VEHICLE_TYPE_ENUM_MOPED,
        VEHICLE_TYPE_ENUM_MOTORCYCLE,
        VEHICLE_TYPE_ENUM_MOTORCYCLE_WITH_SIDE_CAR,
        VEHICLE_TYPE_ENUM_MOTORHOME,
        VEHICLE_TYPE_ENUM_MOTORSCOOTER,
        VEHICLE_TYPE_ENUM_PASSENGER_CAR,
        VEHICLE_TYPE_ENUM_SMALL_CAR,
        VEHICLE_TYPE_ENUM_TANKER,
        VEHICLE_TYPE_ENUM_THREE_WHEELED_VEHICLE,
        VEHICLE_TYPE_ENUM_TRAILER,
        VEHICLE_TYPE_ENUM_TRAM,
        VEHICLE_TYPE_ENUM_TROLLEY_BUS,
        VEHICLE_TYPE_ENUM_TWO_WHEELED_VEHICLE,
        VEHICLE_TYPE_ENUM_VAN,
        VEHICLE_TYPE_ENUM_VEHICLE_WITH_CARAVAN,
        VEHICLE_TYPE_ENUM_VEHICLE_WITH_CATALYTIC_CONVERTER,
        VEHICLE_TYPE_ENUM_VEHICLE_WITHOUT_CATALYTIC_CONVERTER,
        VEHICLE_TYPE_ENUM_VEHICLE_WITH_TRAILER,
        VEHICLE_TYPE_ENUM_WITH_EVEN_NUMBERED_REGISTRATION_PLATES,
        VEHICLE_TYPE_ENUM_WITH_ODD_NUMBERED_REGISTRATION_PLATES,
        VEHICLE_TYPE_ENUM_OTHER
    }

    /// <summary>
    /// Specifies vehicle roles used in prioritization scenarios.
    /// </summary>
    public enum PrioritizationVehicleRoleEnum
    {
        PRIORITIZATION_VEHICLE_ROLE_ENUM_INVALID,
        PRIORITIZATION_VEHICLE_ROLE_ENUM_UNKNOWN,
        PRIORITIZATION_VEHICLE_ROLE_ENUM_EMERGENCY,
        PRIORITIZATION_VEHICLE_ROLE_ENUM_PUBLIC_TRANSPORT
    }

    #endregion
}
