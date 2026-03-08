using System;
using System.Collections.Generic;

namespace MMPing.Utils
{
    public static class OuiLookup
    {
        // OUI = første 6 hex-tegn af MAC (uden separatorer, store bogstaver)
        private static readonly Dictionary<string, string> _oui = new(StringComparer.OrdinalIgnoreCase)
        {
            // Apple
            {"000393","Apple"},{"000A27","Apple"},{"000A95","Apple"},{"000D93","Apple"},
            {"001124","Apple"},{"001451","Apple"},{"0016CB","Apple"},{"001731","Apple"},
            {"001B63","Apple"},{"001CF0","Apple"},{"001E52","Apple"},{"001E8C","Apple"},
            {"002241","Apple"},{"002312","Apple"},{"0023DF","Apple"},{"002500","Apple"},
            {"002608","Apple"},{"0026B9","Apple"},{"0026BB","Apple"},{"003065","Apple"},
            {"34AB37","Apple"},{"3C0754","Apple"},{"3C15C2","Apple"},{"40A6D9","Apple"},
            {"60C547","Apple"},{"8C2937","Apple"},{"A4B197","Apple"},{"A8BE27","Apple"},
            {"AC3C0B","Apple"},{"B8E856","Apple"},{"C82A14","Apple"},{"D0817A","Apple"},
            {"F0DBE2","Apple"},{"F4F15A","Apple"},
            // Samsung
            {"001599","Samsung"},{"002119","Samsung"},{"002339","Samsung"},{"002566","Samsung"},
            {"002638","Samsung"},{"34145F","Samsung"},{"5001BB","Samsung"},{"8C771F","Samsung"},
            {"A84E3F","Samsung"},{"B47443","Samsung"},{"BC765E","Samsung"},{"CC07AB","Samsung"},
            {"D0176A","Samsung"},{"F4428F","Samsung"},{"FC1910","Samsung"},
            // Intel
            {"001B21","Intel"},{"001D92","Intel"},{"001E65","Intel"},{"0021D8","Intel"},
            {"0026C7","Intel"},{"003048","Intel"},{"7085C2","Intel"},{"8086F2","Intel"},
            {"A4C3F0","Intel"},{"AC7BA1","Intel"},
            // Cisco
            {"000142","Cisco"},{"000164","Cisco"},{"0001C7","Cisco"},{"000216","Cisco"},
            {"00024A","Cisco"},{"000261","Cisco"},{"0002B9","Cisco"},{"0002FC","Cisco"},
            {"000301","Cisco"},{"000D29","Cisco"},{"000E38","Cisco"},{"001A6C","Cisco"},
            {"001BD4","Cisco"},{"0021A0","Cisco"},{"002290","Cisco"},{"58AC78","Cisco"},
            {"70105C","Cisco"},{"B8782E","Cisco"},{"C89C1D","Cisco"},{"F8A5C5","Cisco"},
            // TP-Link
            {"1C61B4","TP-Link"},{"5027B7","TP-Link"},{"54AF97","TP-Link"},{"60A4B7","TP-Link"},
            {"90F652","TP-Link"},{"A0F3C1","TP-Link"},{"B0487A","TP-Link"},{"C46E1F","TP-Link"},
            {"E848B8","TP-Link"},{"F4F26D","TP-Link"},
            // Netgear
            {"001E2A","Netgear"},{"00224B","Netgear"},{"20E52A","Netgear"},{"28C68E","Netgear"},
            {"2CAB25","Netgear"},{"30469A","Netgear"},{"4452B9","Netgear"},{"6CB0CE","Netgear"},
            {"84189F","Netgear"},{"A040A0","Netgear"},{"C03F0E","Netgear"},
            // Asus
            {"001A92","Asus"},{"002354","Asus"},{"04D4C4","Asus"},{"107B44","Asus"},
            {"1C872C","Asus"},{"2C56DC","Asus"},{"38D547","Asus"},{"40167E","Asus"},
            {"50465D","Asus"},{"60A44C","Asus"},{"74D02B","Asus"},{"AC9E17","Asus"},
            // Dell
            {"001372","Dell"},{"001A4B","Dell"},{"001E4F","Dell"},{"00215A","Dell"},
            {"002564","Dell"},{"14FEB5","Dell"},{"18A994","Dell"},{"1C400C","Dell"},
            {"249FDA","Dell"},{"2C768A","Dell"},{"848F69","Dell"},{"B083FE","Dell"},
            {"BCEE7B","Dell"},{"F8BC12","Dell"},
            // HP / Hewlett-Packard
            {"001321","HP"},{"001560","HP"},{"001635","HP"},{"001A4B","HP"},
            {"001C2E","HP"},{"001E0B","HP"},{"001FE1","HP"},{"002170","HP"},
            {"00237D","HP"},{"0024B2","HP"},{"0025B3","HP"},{"30E171","HP"},
            {"38EAA7","HP"},{"40B034","HP"},{"A0B3CC","HP"},{"D4C9EF","HP"},
            // Lenovo
            {"001C25","Lenovo"},{"001FB0","Lenovo"},{"0021CC","Lenovo"},{"286ED4","Lenovo"},
            {"40A8F0","Lenovo"},{"484D7E","Lenovo"},{"54EE75","Lenovo"},{"70720D","Lenovo"},
            {"98BE94","Lenovo"},{"C4473F","Lenovo"},
            // Microsoft
            {"002248","Microsoft"},{"0050F2","Microsoft"},{"00BD3A","Microsoft"},
            {"284D24","Microsoft"},{"485073","Microsoft"},{"601583","Microsoft"},
            {"7C1E52","Microsoft"},{"DC533D","Microsoft"},{"F4B7E2","Microsoft"},
            // Google / Nest
            {"001A11","Google"},{"3C5AB4","Google"},{"54607E","Google"},{"94EB2C","Google"},
            {"A47733","Google"},{"F88FCA","Google"},
            // Amazon / Kindle / Echo
            {"0C4733","Amazon"},{"34D270","Amazon"},{"40B4CD","Amazon"},{"44650D","Amazon"},
            {"68037B","Amazon"},{"74C246","Amazon"},{"A002DC","Amazon"},{"AC63BE","Amazon"},
            {"FC6558","Amazon"},{"F0272D","Amazon"},
            // Ubiquiti
            {"002722","Ubiquiti"},{"04182A","Ubiquiti"},{"0418D6","Ubiquiti"},{"243691","Ubiquiti"},
            {"44D9E7","Ubiquiti"},{"68722D","Ubiquiti"},{"788A20","Ubiquiti"},{"80211A","Ubiquiti"},
            // VMware (virtuelle maskiner)
            {"000C29","VMware"},{"000569","VMware"},{"001C14","VMware"},
            // Raspberry Pi
            {"B827EB","Raspberry Pi"},{"DC A6 32","Raspberry Pi"},{"E45F01","Raspberry Pi"},
            // Synology
            {"001132","Synology"},{"0011A7","Synology"},{"0023AE","Synology"},{"BC5FF4","Synology"},
            // QNAP
            {"002490","QNAP"},{"008006","QNAP"},{"24AEBB","QNAP"},
            // Sonos
            {"000E58","Sonos"},{"5CAAFA","Sonos"},{"78288C","Sonos"},{"94B08A","Sonos"},
            // Philips Hue / Signify
            {"001788","Philips"},{"ECB5FA","Philips"},
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
