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

            if (xmlDoc.DocumentElement.Name == "CAM")
            {
                msg.MessageType = "CAM";
                var vehPt = xmlDoc.SelectSingleNode("//vehPt");
                if (vehPt != null)
                {
                    // Parse statId as VehicleID
                    if (vehPt.Attributes["statId"] != null)
                        msg.VehicleID = vehPt.Attributes["statId"].Value;

                    if (vehPt.Attributes["lat"] != null)
                        msg.Latitude = double.Parse(vehPt.Attributes["lat"].Value, System.Globalization.CultureInfo.InvariantCulture);

                    if (vehPt.Attributes["lng"] != null)
                        msg.Longitude = double.Parse(vehPt.Attributes["lng"].Value, System.Globalization.CultureInfo.InvariantCulture);

                    if (vehPt.Attributes["speed"] != null)
                        msg.Speed = double.Parse(vehPt.Attributes["speed"].Value, System.Globalization.CultureInfo.InvariantCulture);

                    if (vehPt.Attributes["heading"] != null)
                        msg.Heading = double.Parse(vehPt.Attributes["heading"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    
                    if (vehPt.Attributes["alt"] != null)
                        msg.Altitude = double.Parse(vehPt.Attributes["alt"].Value, System.Globalization.CultureInfo.InvariantCulture);

                    if (vehPt.Attributes["lastRec"] != null)
                        msg.Timestamp = DateTime.Parse(vehPt.Attributes["lastRec"].Value, null, System.Globalization.DateTimeStyles.RoundtripKind);
                }

            }
            else if (xmlDoc.DocumentElement.Name == "SRV")
            {
                msg.MessageType = "SRV";
                var service = xmlDoc.SelectSingleNode("//service");
                if (service != null)
                {
                    if (service.Attributes["logicalId"] != null)
                        msg.VehicleID = service.Attributes["logicalId"].Value;

                    if (service.Attributes["dt"] != null)
                        msg.Timestamp = DateTime.Parse(service.Attributes["dt"].Value, null, System.Globalization.DateTimeStyles.RoundtripKind);

                    if (service.Attributes["lat"] != null)
                        msg.Latitude = double.Parse(service.Attributes["lat"].Value, System.Globalization.CultureInfo.InvariantCulture);

                    if (service.Attributes["lng"] != null)
                        msg.Longitude = double.Parse(service.Attributes["lng"].Value, System.Globalization.CultureInfo.InvariantCulture);

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
