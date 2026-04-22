using System.Globalization;
using System.Xml.Linq;

/**********************************************************************************************************
 * V2X Controller - TerminalWidow.xaml.cs
 * Author: Michal Švrček
 * Version: 1.0.0
 * Description: SRV message logic of the V2X Controller application. Handles parsing and representation 
 *              of SRV messages.
 *              
 * Copyright (c) 2025 Hroší stavby Morava a.s.
 * All rights reserved.
 *********************************************************************************************************/
namespace V2XController
{
    class SRVMessage
    {
        public string LogicalId { get; set; }
        public DateTime Dt { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
        public string Crc { get; set; }

        public static SRVMessage ParseSrvMessage(string xml)
        {
            var doc = XDocument.Parse(xml);
            var service = doc.Root.Element("service");
            var crc = doc.Root.Element("crc")?.Value;

            if (service == null) return null;

            return new SRVMessage
            {
                LogicalId = (string)service.Attribute("logicalId"),
                Dt = DateTime.Parse((string)service.Attribute("dt")),
                Latitude = double.Parse((string)service.Attribute("lat"), CultureInfo.InvariantCulture),
                Longitude = double.Parse((string)service.Attribute("lng"), CultureInfo.InvariantCulture),
                Altitude = double.Parse((string)service.Attribute("alt"), CultureInfo.InvariantCulture),
                Crc = crc
            };
        }

    }

}
