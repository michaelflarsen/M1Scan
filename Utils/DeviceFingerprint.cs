using System;
using M1Scan.Models;

namespace M1Scan.Utils
{
    public enum DeviceCategory
    {
        Unknown, Router, Server, Computer, Printer, Camera, Tv,
        Phone, IoT, Nas, Plc
    }

    /// <summary>
    /// Regelbaseret enhedsklassificering oven på data der allerede findes fra
    /// scan-berigelsen (TTL-baseret OS-gæt, OUI-vendor, åbne porte, hostname).
    ///
    /// Dette er en HEURISTIK, ikke en garanti — samme ånd som HostInfo.OsGuess'
    /// TTL-gæt. Reglerne kan tage fejl (en Linux-server med lukkede porte
    /// klassificeres fx som "Computer", ikke "Server"), men giver et hurtigt
    /// visuelt overblik i Scan-listen frem for ingen kategorisering.
    /// </summary>
    public static class DeviceFingerprint
    {
        private static readonly string[] RouterVendorHints =
            { "ubiquiti", "tp-link", "netgear", "asustek", "asus", "mikrotik", "fibia", "zyxel" };

        // "hp" er med her fordi IEEE-registret for HP ofte er registreret under det
        // korte navn "HP Inc." (bekræftet mod et rigtigt HP-testnetværk under
        // udvikling af denne feature) — ikke kun det fulde "Hewlett-Packard".
        private static readonly string[] PrinterHints =
            { "printer", "hewlett", "hp inc", " hp ", "hp-", "canon", "epson", "brother", "xerox" };

        // "cam" er UDELADT som bar substring — "Cameron-PC", "Camilla-Laptop" og
        // lignende DHCP-tildelte hostnames indeholder det som en tilfældighed. Kun
        // "camera"/"webcam" og konkrete kamera-producenter er sikre nok signaler.
        private static readonly string[] CameraHints =
            { "camera", "webcam", "hikvision", "dahua", "reolink", "axis" };

        private static readonly string[] TvVendorHints =
            { "samsung electronics", "lg electronics", "sony", "roku", "vizio" };

        private static readonly string[] PhoneVendorHints =
            { "apple", "samsung", "xiaomi", "huawei", "oneplus", "google inc" };

        // "nas" er UDELADT som bar substring — "Panasonic" ("pa-NAS-onic") er en
        // almindelig TV/elektronik-vendor og ville ellers blive fejlklassificeret.
        // Kun "synology"/"qnap" (konkrete NAS-producenter) og eksplicit ordafgrænset
        // "nas" i hostnavnet er sikre nok signaler.
        private static readonly string[] NasVendorHints =
            { "synology", "qnap" };
        private static readonly string[] NasNameHints =
            { "nas-", "-nas", " nas", "nas " };

        public static DeviceCategory Classify(HostInfo host)
        {
            if (host == null) return DeviceCategory.Unknown;

            var vendor = (host.Vendor ?? string.Empty).ToLowerInvariant();
            var name   = ((host.HostName ?? string.Empty) + " " + (host.NetBiosName ?? string.Empty)).ToLowerInvariant();
            bool isHighTtl = host.OsGuess == "Netværksenhed";
            bool hasAdminPort = host.IsPort80Open || host.IsPort443Open || host.IsPort8080Open;

            // 1. PLC/industri — Modbus er en stærk, entydig signal, tjekkes først.
            if (host.IsPort502Open)
                return DeviceCategory.Plc;

            // 2-6: Specifikke vendor/navn-match tjekkes FØR det generiske høj-TTL-gæt
            // (rækkefølge 7). En printer eller et kamera kan sagtens svare med en TTL
            // over 128 — et konkret vendornavn er et stærkere signal end en
            // hop-baseret TTL-heuristik, og skal derfor vinde. Denne rækkefølge blev
            // rettet efter at en rigtig HP-printer på testnetværket blev
            // fejlklassificeret som Router, fordi den simpelthen blev tjekket sidst.

            // 2. Printer — navn eller vendor.
            if (ContainsAny(name, PrinterHints) || ContainsAny(vendor, PrinterHints))
                return DeviceCategory.Printer;

            // 3. Kamera — navn eller vendor.
            if (ContainsAny(name, CameraHints) || ContainsAny(vendor, CameraHints))
                return DeviceCategory.Camera;

            // 4. TV/streaming — kun kendt TV-vendor UDEN admin-UI, for at undgå at
            // ramme fx en Samsung-telefon der også matcher "samsung".
            if (ContainsAny(vendor, TvVendorHints) && !hasAdminPort)
                return DeviceCategory.Tv;

            // 5. NAS — navn (ordafgrænset "nas") eller kendt NAS-producent.
            if (ContainsAny(name, NasNameHints) || ContainsAny(vendor, NasVendorHints))
                return DeviceCategory.Nas;

            // 6. Telefon — kendt mobil-OEM uden Windows/Linux-serverkarakteristik
            // og uden admin-UI (telefoner svarer sjældent på administrative porte).
            if (ContainsAny(vendor, PhoneVendorHints) && string.IsNullOrEmpty(host.OsGuess) && !hasAdminPort)
                return DeviceCategory.Phone;

            // 7. Router — høj TTL (routing-hop) eller kendt netværksudstyrs-vendor.
            // Tjekkes efter de specifikke kategorier ovenfor, som alle er stærkere
            // signaler end en generisk TTL-baseret hop-heuristik.
            if (isHighTtl || ContainsAny(vendor, RouterVendorHints))
                return DeviceCategory.Router;

            // 8. Server/computer — Windows/Linux TTL-gæt afgør skellet på om en
            // administrativ port er åben.
            if (host.OsGuess == "Windows" || host.OsGuess == "Linux / Mac")
                return hasAdminPort ? DeviceCategory.Server : DeviceCategory.Computer;

            // 9. IoT — ukendt embedded enhed uden TTL-match til router/PC.
            if (!string.IsNullOrEmpty(vendor))
                return DeviceCategory.IoT;

            return DeviceCategory.Unknown;
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            foreach (var needle in needles)
                if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>MaterialDesignThemes PackIcon-navn for kategorien.</summary>
        public static string IconFor(DeviceCategory category) => category switch
        {
            DeviceCategory.Router   => "Router",
            DeviceCategory.Plc      => "Factory",
            DeviceCategory.Printer  => "Printer",
            DeviceCategory.Camera   => "Cctv",
            DeviceCategory.Tv       => "Television",
            DeviceCategory.Phone    => "Cellphone",
            DeviceCategory.Nas      => "Nas",
            DeviceCategory.Server   => "Server",
            DeviceCategory.Computer => "Monitor",
            DeviceCategory.IoT      => "Chip",
            _                       => "HelpCircleOutline"
        };

        /// <summary>Dansk label til tooltip.</summary>
        public static string LabelFor(DeviceCategory category) => category switch
        {
            DeviceCategory.Router   => "Router / netværksudstyr",
            DeviceCategory.Plc      => "Industriel styring (PLC)",
            DeviceCategory.Printer  => "Printer",
            DeviceCategory.Camera   => "Kamera",
            DeviceCategory.Tv       => "TV / streaming-enhed",
            DeviceCategory.Phone    => "Telefon",
            DeviceCategory.Nas      => "NAS / netværksdrev",
            DeviceCategory.Server   => "Server",
            DeviceCategory.Computer => "Computer",
            DeviceCategory.IoT      => "IoT-enhed",
            _                       => "Ukendt enhedstype"
        };
    }
}
