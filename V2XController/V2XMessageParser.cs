using System.IO;

/**********************************************************************************************************
 * V2X Controller - V2XMessageParser.cs
 * Author: Michal Švrček
 * Version: 1.0.0
 * Description: Parses V2X messages (CAM and SRV) from raw XML strings. Extracts relevant information such as
 *              VehicleID, latitude, longitude, speed, heading, altitude, and timestamp. Provides methods
 *              to determine the message type and to parse messages into V2XMessage objects.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/


namespace V2XController
{
    internal class V2XMessageParser
    {
        public static string GetMessageType(string rawXml)
        {
            if (rawXml.Contains("<CAM>")) return "CAM";
            if (rawXml.Contains("<SRV>")) return "SRV";
            return "UNKNOWN";
        }

        // Ensure statId is parsed as VehicleID in ParseV2XMessage
        public static V2XMessage ParseV2XMessage(string rawXml)
        {
            var msg = new V2XMessage();
            msg.RawContent = rawXml;

            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.LoadXml(rawXml);

            if (xmlDoc.DocumentElement?.Name == "CAM")
            {
                msg.MessageType = "CAM";
                var vehPt = xmlDoc.SelectSingleNode("//vehPt");
                if (vehPt != null)
                {
                    // Use local attribute references to avoid possible null deref warnings
                    var aStat = vehPt.Attributes?["statId"];
                    if (aStat != null)
                        msg.VehicleID = aStat.Value;

                    var aLat = vehPt.Attributes?["lat"];
                    if (aLat != null)
                        msg.Latitude = double.Parse(aLat.Value, System.Globalization.CultureInfo.InvariantCulture);

                    var aLng = vehPt.Attributes?["lng"];
                    if (aLng != null)
                        msg.Longitude = double.Parse(aLng.Value, System.Globalization.CultureInfo.InvariantCulture);

                    var aSpeed = vehPt.Attributes?["speed"];
                    if (aSpeed != null)
                        msg.Speed = double.Parse(aSpeed.Value, System.Globalization.CultureInfo.InvariantCulture);

                    var aHeading = vehPt.Attributes?["heading"];
                    if (aHeading != null)
                        msg.Heading = double.Parse(aHeading.Value, System.Globalization.CultureInfo.InvariantCulture);

                    var aAlt = vehPt.Attributes?["alt"];
                    if (aAlt != null)
                        msg.Altitude = double.Parse(aAlt.Value, System.Globalization.CultureInfo.InvariantCulture);

                    var aLast = vehPt.Attributes?["lastRec"];
                    if (aLast != null)
                        msg.Timestamp = DateTime.Parse(aLast.Value, null, System.Globalization.DateTimeStyles.RoundtripKind);
                }
            }
            else if (xmlDoc.DocumentElement?.Name == "SRV")
            {
                msg.MessageType = "SRV";
                var service = xmlDoc.SelectSingleNode("//service");
                if (service != null)
                {
                    var aLogical = service.Attributes?["logicalId"];
                    if (aLogical != null)
                        msg.VehicleID = aLogical.Value;

                    var aDt = service.Attributes?["dt"];
                    if (aDt != null)
                        msg.Timestamp = DateTime.Parse(aDt.Value, null, System.Globalization.DateTimeStyles.RoundtripKind);

                    var aLat = service.Attributes?["lat"];
                    if (aLat != null)
                        msg.Latitude = double.Parse(aLat.Value, System.Globalization.CultureInfo.InvariantCulture);

                    var aLng = service.Attributes?["lng"];
                    if (aLng != null)
                        msg.Longitude = double.Parse(aLng.Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            return msg;
        }


        public static List<V2XMessage> LoadMessagesFromCSV(string path)
        {
            var messages = new List<V2XMessage>();

            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 6) continue;

                messages.Add(new V2XMessage
                {
                    VehicleID = parts[0],
                    Timestamp = DateTime.Parse(parts[1]),
                    Latitude = double.Parse(parts[2]),
                    Longitude = double.Parse(parts[3]),
                    Speed = double.Parse(parts[4]),
                    Heading = double.Parse(parts[5]),
                    MessageType = "CSV"
                });
            }

            return messages;
        }
    }
}
