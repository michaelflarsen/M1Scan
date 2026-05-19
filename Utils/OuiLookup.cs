using System;
using System.Collections.Generic;

namespace M1Scan.Utils
{
    public static class OuiLookup
    {
        // OUI = første 6 hex-tegn af MAC (uden separatorer, store bogstaver)
        private static readonly Dictionary<string, string> _oui;

        static OuiLookup()
        {
            try
            {
                _oui = BuildDict();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OuiLookup init failed: {ex.Message}");
                _oui = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static Dictionary<string, string> BuildDict() => new(StringComparer.OrdinalIgnoreCase)
        {
            // Apple
            {"000393","Apple"},{"000A27","Apple"},{"000A95","Apple"},{"000D93","Apple"},
            {"001124","Apple"},{"001451","Apple"},{"0016CB","Apple"},{"001731","Apple"},
            {"001B63","Apple"},{"001CF0","Apple"},{"001E52","Apple"},{"001E8C","Apple"},
            {"002241","Apple"},{"002312","Apple"},{"0023DF","Apple"},{"002500","Apple"},
            {"002608","Apple"},{"0026B9","Apple"},{"0026BB","Apple"},{"003065","Apple"},
            {"04F7E4","Apple"},{"08F4AB","Apple"},{"0C3E9F","Apple"},
            {"10DDB1","Apple"},{"143092","Apple"},{"18AF61","Apple"},{"1C1AC0","Apple"},
            {"20C9D0","Apple"},{"28E02C","Apple"},{"34AB37","Apple"},{"38C986","Apple"},
            {"3C0754","Apple"},{"3C15C2","Apple"},{"40A6D9","Apple"},{"44D884","Apple"},
            {"48D705","Apple"},{"4C3275","Apple"},{"54AE27","Apple"},{"60C547","Apple"},
            {"64200C","Apple"},{"6C96CF","Apple"},{"70DEE2","Apple"},{"7845D2","Apple"},
            {"7C6D62","Apple"},{"84789C","Apple"},{"8C2937","Apple"},{"90B21F","Apple"},
            {"9427E8","Apple"},{"985FD3","Apple"},{"A04E81","Apple"},{"A4B197","Apple"},
            {"A8BE27","Apple"},{"AC3C0B","Apple"},{"B8E856","Apple"},{"BC52B7","Apple"},
            {"C82A14","Apple"},{"CC29F5","Apple"},{"D0E140","Apple"},{"D4619D","Apple"},
            {"D8004D","Apple"},{"D83CED","Apple"},{"D8FBC2","Apple"},{"DC2B2A","Apple"},
            {"E0B52D","Apple"},{"F0D1A9","Apple"},{"F0DBE2","Apple"},
            {"F4F15A","Apple"},{"F81EDF","Apple"},
            // Samsung
            {"001599","Samsung"},{"002119","Samsung"},{"002339","Samsung"},{"002566","Samsung"},
            {"002638","Samsung"},{"0025FE","Samsung"},{"00265B","Samsung"},{"002716","Samsung"},
            {"002D54","Samsung"},{"00E3B2","Samsung"},{"101444","Samsung"},{"1C5A3E","Samsung"},
            {"1C66AA","Samsung"},{"2C0E3D","Samsung"},{"34145F","Samsung"},{"380195","Samsung"},
            {"3C8BFE","Samsung"},{"40B827","Samsung"},{"48DBF1","Samsung"},{"4CBCF3","Samsung"},
            {"50010F","Samsung"},{"5001BB","Samsung"},{"546C0E","Samsung"},{"5C0A5B","Samsung"},
            {"5C3C27","Samsung"},{"6077E2","Samsung"},{"64B853","Samsung"},{"6C8336","Samsung"},
            {"788776","Samsung"},{"7C9EBD","Samsung"},{"84553A","Samsung"},{"8C71F8","Samsung"},
            {"8C771F","Samsung"},{"90C1C6","Samsung"},{"94766F","Samsung"},{"984F58","Samsung"},
            {"9C8D7C","Samsung"},{"A04244","Samsung"},{"A84E3F","Samsung"},{"A8810A","Samsung"},
            {"B47443","Samsung"},{"B4BF01","Samsung"},{"BC765E","Samsung"},{"C07ABF","Samsung"},
            {"C0BDD1","Samsung"},{"C4731E","Samsung"},{"CC07AB","Samsung"},{"CC3A61","Samsung"},
            {"D0176A","Samsung"},{"D013FD","Samsung"},{"D4E8B2","Samsung"},{"DC7144","Samsung"},
            {"E4B021","Samsung"},{"E876B4","Samsung"},{"EC1D7A","Samsung"},{"F4428F","Samsung"},
            {"F48B32","Samsung"},{"F47B5E","Samsung"},{"FC1910","Samsung"},
            // Intel
            {"001B21","Intel"},{"001D92","Intel"},{"001E65","Intel"},{"0021D8","Intel"},
            {"0026C7","Intel"},{"003048","Intel"},{"046E73","Intel"},{"0C54A5","Intel"},
            {"1047E7","Intel"},{"14A4C6","Intel"},{"18B4AD","Intel"},{"1C36BB","Intel"},
            {"202685","Intel"},{"2C41CB","Intel"},{"3C970E","Intel"},{"485B39","Intel"},
            {"4CC795","Intel"},{"5CF951","Intel"},{"602EDA","Intel"},{"6487D7","Intel"},
            {"6C8814","Intel"},{"7085C2","Intel"},{"7486E2","Intel"},{"782D7E","Intel"},
            {"803719","Intel"},{"8086F2","Intel"},{"8C8590","Intel"},{"8C8D28","Intel"},
            {"906251","Intel"},{"94659C","Intel"},{"9C2A83","Intel"},{"A0369F","Intel"},
            {"A04C5B","Intel"},{"A4C3F0","Intel"},{"A86B7C","Intel"},{"AC7BA1","Intel"},
            {"B4AEE3","Intel"},{"C4D987","Intel"},{"C4956E","Intel"},{"C8D9D2","Intel"},
            {"D046FB","Intel"},{"E4A7A0","Intel"},{"E82A44","Intel"},{"E8B1FC","Intel"},
            {"F8341F","Intel"},{"F8DEA4","Intel"},
            // Cisco
            {"000142","Cisco"},{"000164","Cisco"},{"0001C7","Cisco"},{"000216","Cisco"},
            {"00024A","Cisco"},{"000261","Cisco"},{"0002B9","Cisco"},{"0002FC","Cisco"},
            {"000301","Cisco"},{"000D29","Cisco"},{"000E38","Cisco"},{"001A6C","Cisco"},
            {"001BD4","Cisco"},{"0021A0","Cisco"},{"002290","Cisco"},{"30F72C","Cisco"},
            {"3C08F6","Cisco"},{"3C7D39","Cisco"},{"442B03","Cisco"},{"58AC78","Cisco"},
            {"68BC0C","Cisco"},{"70105C","Cisco"},{"74A02F","Cisco"},{"8C0DFF","Cisco"},
            {"8CB64F","Cisco"},{"A07049","Cisco"},{"AC5DE0","Cisco"},{"B8782E","Cisco"},
            {"C89C1D","Cisco"},{"CC984B","Cisco"},{"D0574C","Cisco"},{"D8B190","Cisco"},
            {"E8BAE9","Cisco"},{"F4CFE2","Cisco"},{"F8A5C5","Cisco"},{"FCA8D3","Cisco"},
            // TP-Link
            {"1C61B4","TP-Link"},{"1C3BF3","TP-Link"},{"14CC20","TP-Link"},{"18D6C7","TP-Link"},
            {"2C4D54","TP-Link"},{"3C7A8A","TP-Link"},{"50C7BF","TP-Link"},{"5027B7","TP-Link"},
            {"54AF97","TP-Link"},{"60A4B7","TP-Link"},{"6C5AB5","TP-Link"},{"78481E","TP-Link"},
            {"783A84","TP-Link"},{"84168D","TP-Link"},{"8CED43","TP-Link"},{"90F652","TP-Link"},
            {"909A4A","TP-Link"},{"A0F3C1","TP-Link"},{"A42BB0","TP-Link"},{"B0487A","TP-Link"},
            {"B4476A","TP-Link"},{"C46E1F","TP-Link"},{"C4E984","TP-Link"},{"C80E14","TP-Link"},
            {"D46A6A","TP-Link"},{"E848B8","TP-Link"},{"EC0884","TP-Link"},{"F4F26D","TP-Link"},
            {"FC0290","TP-Link"},{"F49FF3","TP-Link"},
            // Netgear
            {"001E2A","Netgear"},{"00146C","Netgear"},{"00184D","Netgear"},{"00224B","Netgear"},
            {"1B4E8F","Netgear"},{"204E7F","Netgear"},{"20E52A","Netgear"},{"28C68E","Netgear"},
            {"2CAB25","Netgear"},{"30469A","Netgear"},{"4452B9","Netgear"},{"5440AD","Netgear"},
            {"6CB0CE","Netgear"},{"84189F","Netgear"},{"9C3DCF","Netgear"},{"A021B7","Netgear"},
            {"A040A0","Netgear"},{"C03F0E","Netgear"},{"E0469A","Netgear"},
            // Asus
            {"001A92","Asus"},{"002354","Asus"},{"04D9F5","Asus"},{"04D4C4","Asus"},
            {"08606E","Asus"},{"107B44","Asus"},{"1C872C","Asus"},{"2C56DC","Asus"},
            {"2CFDA1","Asus"},{"382C4A","Asus"},{"38D547","Asus"},{"40167E","Asus"},
            {"50465D","Asus"},{"60A44C","Asus"},{"6C62B0","Asus"},{"74D02B","Asus"},
            {"90E6BA","Asus"},{"AC9E17","Asus"},{"B06EBF","Asus"},{"CC28AA","Asus"},
            {"F832E4","Asus"},{"FC3497","Asus"},
            // Dell
            {"001372","Dell"},{"001A4B","Dell"},{"001E4F","Dell"},{"00215A","Dell"},
            {"002564","Dell"},{"14FEB5","Dell"},{"18A994","Dell"},{"1C400C","Dell"},
            {"1C697A","Dell"},{"249FDA","Dell"},{"2C768A","Dell"},{"34480D","Dell"},
            {"509A4C","Dell"},{"848F69","Dell"},{"8CFD18","Dell"},{"98BA43","Dell"},
            {"A41F72","Dell"},{"B083FE","Dell"},{"BCEE7B","Dell"},{"C81F66","Dell"},
            {"D4AE52","Dell"},{"F4CE46","Dell"},{"F8BC12","Dell"},
            // HP / Hewlett-Packard
            {"001321","HP"},{"001560","HP"},{"001635","HP"},{"001C2E","HP"},
            {"001E0B","HP"},{"001FE1","HP"},{"002170","HP"},{"00237D","HP"},
            {"0024B2","HP"},{"0025B3","HP"},{"00CFE0","HP"},{"30E171","HP"},
            {"382140","HP"},{"38EAA7","HP"},{"3C4A92","HP"},{"40B034","HP"},
            {"68B599","HP"},{"9CB6D0","HP"},{"A0B3CC","HP"},{"B4CAE4","HP"},
            {"D4C9EF","HP"},{"EC9947","HP"},
            // Lenovo
            {"001C25","Lenovo"},{"001FB0","Lenovo"},{"0021CC","Lenovo"},{"286ED4","Lenovo"},
            {"40A8F0","Lenovo"},{"484D7E","Lenovo"},{"54EE75","Lenovo"},{"588A5A","Lenovo"},
            {"70720D","Lenovo"},{"902B34","Lenovo"},{"98BE94","Lenovo"},
            {"ACB57D","Lenovo"},{"C44F33","Lenovo"},{"C4473F","Lenovo"},{"CC52AF","Lenovo"},
            {"D03745","Lenovo"},{"ECF4BB","Lenovo"},{"F4A733","Lenovo"},
            // Microsoft
            {"002248","Microsoft"},{"00125A","Microsoft"},{"0050F2","Microsoft"},
            {"00BD3A","Microsoft"},{"284D24","Microsoft"},{"2835A9","Microsoft"},
            {"485073","Microsoft"},{"601583","Microsoft"},{"60451D","Microsoft"},
            {"7C1E52","Microsoft"},{"DC533D","Microsoft"},{"F4B7E2","Microsoft"},
            // Google / Nest / Chromecast
            {"001A11","Google"},{"3C5AB4","Google"},{"54607E","Google"},{"6C5498","Google"},
            {"94EB2C","Google"},{"A47733","Google"},{"F88FCA","Google"},
            {"20DFB9","Google"},{"48D6D5","Google"},
            // Amazon / Kindle / Echo
            {"0C4733","Amazon"},{"34D270","Amazon"},{"40B4CD","Amazon"},{"44650D","Amazon"},
            {"68037B","Amazon"},{"74C246","Amazon"},{"A002DC","Amazon"},{"AC63BE","Amazon"},
            {"F0272D","Amazon"},{"FC6558","Amazon"},
            // Ubiquiti
            {"002722","Ubiquiti"},{"009069","Ubiquiti"},{"04182A","Ubiquiti"},{"0418D6","Ubiquiti"},
            {"24A43C","Ubiquiti"},{"243691","Ubiquiti"},{"44D9E7","Ubiquiti"},{"688557","Ubiquiti"},
            {"68723D","Ubiquiti"},{"788A20","Ubiquiti"},{"80211A","Ubiquiti"},{"80AAFB","Ubiquiti"},
            {"98DED0","Ubiquiti"},{"ACB5A9","Ubiquiti"},{"E063DA","Ubiquiti"},{"FCECDA","Ubiquiti"},
            {"F09FC2","Ubiquiti"},
            // MikroTik
            {"181C39","MikroTik"},{"48A98A","MikroTik"},{"4C5E0C","MikroTik"},{"64D154","MikroTik"},
            {"6C3B6B","MikroTik"},{"742570","MikroTik"},{"8C882B","MikroTik"},{"B869F4","MikroTik"},
            {"CC2DE0","MikroTik"},{"D4CAD6","MikroTik"},{"DC2C6E","MikroTik"},{"E48D8C","MikroTik"},
            // D-Link
            {"001195","D-Link"},{"001564","D-Link"},{"00179A","D-Link"},{"001E58","D-Link"},
            {"14D64D","D-Link"},{"1C7EE5","D-Link"},{"28107B","D-Link"},{"2C26C5","D-Link"},
            {"340804","D-Link"},{"6805CA","D-Link"},{"744401","D-Link"},{"8CFABA","D-Link"},
            {"9CD643","D-Link"},{"B88BE3","D-Link"},{"C8D3A3","D-Link"},{"CC0DEE","D-Link"},
            {"F07D68","D-Link"},{"F0B4D2","D-Link"},
            // Belkin
            {"000F3D","Belkin"},{"001150","Belkin"},{"001CDF","Belkin"},{"002275","Belkin"},
            {"081F71","Belkin"},{"30231D","Belkin"},{"94103E","Belkin"},{"B4750E","Belkin"},
            {"EC1A59","Belkin"},
            // Huawei
            {"001E10","Huawei"},{"00E0FC","Huawei"},{"0C96BF","Huawei"},{"14A516","Huawei"},
            {"1C8E5C","Huawei"},{"20F17C","Huawei"},{"28312B","Huawei"},{"34A84E","Huawei"},
            {"38BC01","Huawei"},{"3C4713","Huawei"},{"404D8E","Huawei"},{"4455B1","Huawei"},
            {"48DB50","Huawei"},{"4C5499","Huawei"},{"5425EA","Huawei"},{"5C8A38","Huawei"},
            {"60DE44","Huawei"},{"64135C","Huawei"},{"68A0F6","Huawei"},{"6CC1B8","Huawei"},
            {"7072CF","Huawei"},{"7801B8","Huawei"},{"80FB06","Huawei"},
            {"84A8E4","Huawei"},{"8C34FD","Huawei"},{"9C28EF","Huawei"},{"9CC172","Huawei"},
            {"A4B5A1","Huawei"},{"A89985","Huawei"},{"B48615","Huawei"},{"C4072F","Huawei"},
            {"C4F081","Huawei"},{"CC96A0","Huawei"},{"D46AA8","Huawei"},{"D86BBC","Huawei"},
            {"DCD2FC","Huawei"},{"E41B5D","Huawei"},{"F4F951","Huawei"},{"FC3FDB","Huawei"},
            // Xiaomi
            {"286C07","Xiaomi"},{"34CE00","Xiaomi"},{"50642B","Xiaomi"},
            {"64618A","Xiaomi"},{"64CC2E","Xiaomi"},{"7451BA","Xiaomi"},{"7802F8","Xiaomi"},
            {"8CBEBE","Xiaomi"},{"905851","Xiaomi"},{"9C99A0","Xiaomi"},{"ACC1EE","Xiaomi"},
            {"B0E235","Xiaomi"},{"C40BCB","Xiaomi"},{"FC64BA","Xiaomi"},
            // VMware (virtuelle maskiner)
            {"000569","VMware"},{"000C29","VMware"},{"001C14","VMware"},{"005056","VMware"},
            // Raspberry Pi
            {"B827EB","Raspberry Pi"},{"DCA632","Raspberry Pi"},{"E45F01","Raspberry Pi"},
            {"D83ADD","Raspberry Pi"},
            // Synology
            {"001132","Synology"},{"0011A7","Synology"},{"0023AE","Synology"},
            {"6C81B3","Synology"},{"BC5FF4","Synology"},
            // QNAP
            {"002490","QNAP"},{"008006","QNAP"},{"24AEBB","QNAP"},{"642737","QNAP"},
            // Sonos
            {"000E58","Sonos"},{"5CAAFA","Sonos"},{"78288C","Sonos"},{"94B08A","Sonos"},
            // Philips Hue / Signify
            {"000C32","Philips"},{"001788","Philips"},{"ECB5FA","Philips"},
            // AVM / Fritzbox
            {"001EA6","AVM"},{"003AB0","AVM"},{"0090A9","AVM"},{"1CBCD9","AVM"},
            {"304EFB","AVM"},{"4C4E03","AVM"},{"64DE20","AVM"},{"9C0F68","AVM"},
            {"B403E3","AVM"},{"E06E00","AVM"},{"FC3448","AVM"},
            // Espressif (ESP8266 / ESP32 IoT-enheder)
            {"246F28","Espressif"},{"30AEA4","Espressif"},{"3C71BF","Espressif"},
            {"3C7D3B","Espressif"},{"483FDA","Espressif"},{"4C11AE","Espressif"},
            {"5443B2","Espressif"},{"5C313A","Espressif"},{"5CCF7F","Espressif"},
            {"606405","Espressif"},{"688CB5","Espressif"},{"6CBCFC","Espressif"},
            {"7CDFA1","Espressif"},{"807D3A","Espressif"},{"84CCA8","Espressif"},
            {"886348","Espressif"},{"8CF681","Espressif"},{"90970D","Espressif"},
            {"A020A6","Espressif"},{"A4CF12","Espressif"},{"A4E57C","Espressif"},
            {"AC67B2","Espressif"},{"B4E62D","Espressif"},{"BC3BAF","Espressif"},
            {"C45BBE","Espressif"},{"C8F09E","Espressif"},
            {"CC50E3","Espressif"},{"D8A01D","Espressif"},{"E09806","Espressif"},
            {"E868E7","Espressif"},{"EC64C9","Espressif"},{"F4CFA2","Espressif"},
            {"F8A89B","Espressif"},{"FC015D","Espressif"},{"18FE34","Espressif"},
            // Realtek (integrerede NIC chips)
            {"00E04C","Realtek"},{"000ACD","Realtek"},{"E09188","Realtek"},{"F81A67","Realtek"},
            // Broadcom
            {"001018","Broadcom"},{"14B31F","Broadcom"},{"2816A8","Broadcom"},{"90E2BA","Broadcom"},
            // LG Electronics
            {"001C62","LG"},{"001E75","LG"},{"001FE3","LG"},{"003412","LG"},{"009A79","LG"},
            {"1C1AB0","LG"},{"20CF30","LG"},{"34DF2A","LG"},{"3CBD3E","LG"},
            {"40B0FA","LG"},{"50B7C3","LG"},{"58FB84","LG"},{"64899A","LG"},{"7CC33A","LG"},
            {"88C9D0","LG"},{"A0B4A5","LG"},{"C0A0DE","LG"},{"C4BCBA","LG"},{"CC08FB","LG"},
            {"D0F88C","LG"},{"EC6C9A","LG"},
            // Sony
            {"001375","Sony"},{"001A80","Sony"},{"001DA2","Sony"},{"001E33","Sony"},
            {"0024BE","Sony"},{"002618","Sony"},{"006B9E","Sony"},{"14ABC5","Sony"},
            {"1C9BED","Sony"},{"2CC54D","Sony"},{"30172C","Sony"},{"3CC06F","Sony"},
            {"4C7CB5","Sony"},{"50F0D3","Sony"},{"54AA08","Sony"},{"5C96CE","Sony"},
            {"702B8B","Sony"},{"788309","Sony"},{"9065D3","Sony"},{"A0E453","Sony"},
            {"ACDEC0","Sony"},{"BC9CE5","Sony"},{"E83D1C","Sony"},
            // Nintendo
            {"000D67","Nintendo"},{"000F91","Nintendo"},{"00090F","Nintendo"},
            {"001231","Nintendo"},{"001656","Nintendo"},{"0017AB","Nintendo"},
            {"001F32","Nintendo"},{"002444","Nintendo"},{"0022D7","Nintendo"},
            {"58BDB3","Nintendo"},{"78A2A0","Nintendo"},{"A45C27","Nintendo"},
            {"B8AE6E","Nintendo"},{"CC9E00","Nintendo"},{"E0047B","Nintendo"},
            // Roku
            {"000D4B","Roku"},{"087B39","Roku"},{"186090","Roku"},{"507CAB","Roku"},
            {"686284","Roku"},{"D88D76","Roku"},{"DC3A5E","Roku"},
            // ZTE
            {"001E73","ZTE"},{"008255","ZTE"},{"00236A","ZTE"},{"0C100D","ZTE"},
            {"1C71AE","ZTE"},{"200BC7","ZTE"},{"28F366","ZTE"},{"4C06C3","ZTE"},
            {"5C6366","ZTE"},{"6437F7","ZTE"},{"788968","ZTE"},{"8CF578","ZTE"},
            {"A85280","ZTE"},{"B4B362","ZTE"},{"CC0E14","ZTE"},{"DCEAAA","ZTE"},
            // Canon
            {"000085","Canon"},{"003085","Canon"},{"104FA8","Canon"},{"1C4FEE","Canon"},
            {"245C10","Canon"},{"3447BB","Canon"},{"5071C6","Canon"},{"60C5A8","Canon"},
            {"90B11C","Canon"},{"C809A8","Canon"},
            // Epson
            {"001016","Epson"},{"001BF8","Epson"},{"0026AB","Epson"},{"002755","Epson"},
            {"040CA3","Epson"},{"34B2ED","Epson"},{"58BF25","Epson"},{"64D8DC","Epson"},
            {"701CE7","Epson"},{"9C1363","Epson"},{"ACBD7E","Epson"},
            // Brother
            {"001BA9","Brother"},{"002261","Brother"},{"008092","Brother"},{"0C0743","Brother"},
            {"1060EB","Brother"},{"14C57F","Brother"},{"30055C","Brother"},{"74E544","Brother"},
            {"A4BA76","Brother"},
            // HP Printers (HPE)
            {"00237A","HP Printer"},{"24E25E","HP Printer"},{"3C9971","HP Printer"},
            {"6CF049","HP Printer"},{"A0D3C1","HP Printer"},{"D4059D","HP Printer"},
            // Hikvision (overvågningskameraer)
            {"003041","Hikvision"},{"285ED7","Hikvision"},{"4427CB","Hikvision"},
            {"485A3F","Hikvision"},{"6021CB","Hikvision"},{"706268","Hikvision"},
            {"8C97EA","Hikvision"},{"A01F7F","Hikvision"},{"BC6A4A","Hikvision"},
            {"C0DFAA","Hikvision"},{"E4241C","Hikvision"},{"EC448F","Hikvision"},
            // Dahua (overvågningskameraer)
            {"00F040","Dahua"},{"14F487","Dahua"},{"3C1AC5","Dahua"},{"50DB17","Dahua"},
            {"80E0D6","Dahua"},{"C0A8A7","Dahua"},{"E4246C","Dahua"},
            // Axis (netværkskameraer)
            {"00408C","Axis"},{"ACCC8E","Axis"},{"B8A44F","Axis"},
            // Aruba / HPE Networking
            {"000719","Aruba"},{"002366","Aruba"},{"0023EB","Aruba"},{"04BD88","Aruba"},
            {"24DEC6","Aruba"},{"283B82","Aruba"},{"40E3D6","Aruba"},{"6CFD5C","Aruba"},
            {"70888B","Aruba"},{"94B4CF","Aruba"},{"9C8CD8","Aruba"},{"ACA31E","Aruba"},
            {"CC3ADF","Aruba"},{"D8C7C8","Aruba"},{"F04DA2","Aruba"},
            // Microchip (ENC28J60, LAN kontrollere)
            {"0004A3","Microchip"},{"002076","Microchip"},{"002320","Microchip"},
            {"80004A","Microchip"},{"D88039","Microchip"},
            // Siemens (industrielt)
            {"0002A5","Siemens"},{"000EA6","Siemens"},{"001CC0","Siemens"},{"003008","Siemens"},
            {"2016B9","Siemens"},{"6CCD29","Siemens"},{"800F4B","Siemens"},
            // Rockwell / Allen-Bradley (industrielt EtherNet/IP)
            {"00000F","Rockwell"},{"0001DE","Rockwell"},{"000E63","Rockwell"},
            {"00E0B1","Rockwell"},{"7CD60B","Rockwell"},{"90167C","Rockwell"},
            // Schneider Electric
            {"001BB5","Schneider"},{"0C2B0A","Schneider"},{"64D14F","Schneider"},
            {"8C8DF5","Schneider"},{"BC2E02","Schneider"},
            // Moxa (industrielle gateways)
            {"000090","Moxa"},{"001346","Moxa"},{"001E53","Moxa"},{"002190","Moxa"},{"00907F","Moxa"},
            // Phoenix Contact
            {"003000","Phoenix Contact"},{"003BD8","Phoenix Contact"},{"A4D1D2","Phoenix Contact"},
            // Digi International (IoT gateways)
            {"001640","Digi"},{"00192F","Digi"},{"001DD2","Digi"},{"00409D","Digi"},
            // WAGO (industrielt)
            {"00204D","WAGO"},{"0030DE","WAGO"},
            // Texas Instruments
            {"A0F6FD","Texas Instruments"},{"D0FF98","Texas Instruments"},
            // Nordic Semiconductor
            {"EC2DBF","Nordic Semi"},{"D8D4B1","Nordic Semi"},
        };

        public static string Lookup(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return string.Empty;
            // Normaliser: fjern : - . og tag de første 6 tegn
            var normalized = mac.Replace(":", "").Replace("-", "").Replace(".", "");
            if (normalized.Length < 6) return string.Empty;
            var oui = normalized.Substring(0, 6).ToUpperInvariant();
            return _oui.TryGetValue(oui, out var vendor) ? vendor : string.Empty;
        }
    }
}
