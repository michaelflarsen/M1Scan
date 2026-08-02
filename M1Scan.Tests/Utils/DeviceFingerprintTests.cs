using M1Scan.Models;
using M1Scan.Utils;
using Xunit;

namespace M1Scan.Tests.Utils
{
    /// <summary>
    /// DeviceFingerprint er en ren heuristik oven på felter der allerede findes på
    /// HostInfo efter scan-berigelse — testene her konstruerer HostInfo direkte,
    /// ingen netværk eller DB involveret.
    /// </summary>
    public class DeviceFingerprintTests
    {
        [Fact]
        public void HighTtl_ClassifiesAsRouter()
        {
            var host = new HostInfo { OsGuess = "Netværksenhed" };
            Assert.Equal(DeviceCategory.Router, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void RouterVendor_ClassifiesAsRouter()
        {
            var host = new HostInfo { Vendor = "Ubiquiti Networks Inc." };
            Assert.Equal(DeviceCategory.Router, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void Port502Open_ClassifiesAsPlc_EvenWithHighTtl()
        {
            // Modbus (502) er et stærkere, mere entydigt signal end TTL-gættet og
            // skal derfor vinde over en router-klassificering.
            var host = new HostInfo { OsGuess = "Netværksenhed", IsPort502Open = true };
            Assert.Equal(DeviceCategory.Plc, DeviceFingerprint.Classify(host));
        }

        [Theory]
        [InlineData("HEWLETT-PACKARD-LaserJet")]
        [InlineData("printer-stue")]
        public void HostNameMatchingPrinterHints_ClassifiesAsPrinter(string hostName)
        {
            var host = new HostInfo { HostName = hostName };
            Assert.Equal(DeviceCategory.Printer, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void HpVendor_ClassifiesAsPrinter_EvenWithHighTtl()
        {
            // Regression: en rigtig HP-printer på testnetværket blev fejlklassificeret
            // som Router, fordi "HP Inc." (IEEE-registrets korte vendornavn, ikke det
            // fulde "Hewlett-Packard") ikke matchede printer-hints, og en høj TTL
            // (>128) blev derfor tjekket først og vandt.
            var host = new HostInfo { OsGuess = "Netværksenhed", Vendor = "HP Inc." };
            Assert.Equal(DeviceCategory.Printer, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void PanasonicVendor_DoesNotClassifyAsNas()
        {
            // Regression: "Panasonic" indeholder bogstaveligt "nas" ("pa-NAS-onic").
            // Et bart "nas"-hint ville fejlklassificere enhver Panasonic-enhed som NAS.
            var host = new HostInfo { Vendor = "Panasonic Corporation" };
            Assert.NotEqual(DeviceCategory.Nas, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void HostNameContainingCameronOrCamilla_DoesNotClassifyAsCamera()
        {
            // Regression: "Cameron"/"Camilla" indeholder "cam" som en tilfældighed i
            // et DHCP-tildelt hostname — skal ikke udløse kamera-klassificering.
            var host = new HostInfo { HostName = "Cameron-Laptop", OsGuess = "Windows" };
            Assert.Equal(DeviceCategory.Computer, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void WindowsWithAdminPort_ClassifiesAsServer()
        {
            var host = new HostInfo { OsGuess = "Windows", IsPort80Open = true };
            Assert.Equal(DeviceCategory.Server, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void WindowsWithoutAdminPort_ClassifiesAsComputer()
        {
            var host = new HostInfo { OsGuess = "Windows" };
            Assert.Equal(DeviceCategory.Computer, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void LinuxMacWithAdminPort_ClassifiesAsServer()
        {
            var host = new HostInfo { OsGuess = "Linux / Mac", IsPort443Open = true };
            Assert.Equal(DeviceCategory.Server, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void UnknownVendorAndOs_ClassifiesAsUnknown()
        {
            var host = new HostInfo();
            Assert.Equal(DeviceCategory.Unknown, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void KnownVendorWithoutOsGuess_ClassifiesAsIoT()
        {
            var host = new HostInfo { Vendor = "Espressif Inc." };
            Assert.Equal(DeviceCategory.IoT, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void TvVendor_WithAdminPortOpen_DoesNotClassifyAsTv()
        {
            // Undgå falske positiver: en Samsung-telefon med admin-UI skal ikke
            // fejlklassificeres som TV blot fordi vendor-strengen matcher.
            var host = new HostInfo { Vendor = "Samsung Electronics Co.,Ltd", IsPort80Open = true };
            Assert.NotEqual(DeviceCategory.Tv, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void SamsungVendor_WithoutAdminPortOrOsGuess_ClassifiesAsTv_AcceptedAmbiguity()
        {
            // Kendt, accepteret begrænsning: Samsung registrerer ofte telefoner og
            // TV'er under samme OUI-vendorstreng ("Samsung Electronics Co.,Ltd"), så
            // uden admin-UI kan de to ikke skelnes på vendor alene. TV-reglen tjekkes
            // først og vinder — det er en heuristik, ikke en garanti (se klassens
            // XML-doc-kommentar i DeviceFingerprint.cs).
            var host = new HostInfo { Vendor = "Samsung Electronics Co.,Ltd" };
            Assert.Equal(DeviceCategory.Tv, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void PhoneVendor_WithoutOsGuessOrAdminPort_ClassifiesAsPhone()
        {
            var host = new HostInfo { Vendor = "Apple, Inc." };
            Assert.Equal(DeviceCategory.Phone, DeviceFingerprint.Classify(host));
        }

        [Fact]
        public void IconFor_ReturnsExpectedPackIconNames()
        {
            Assert.Equal("Router", DeviceFingerprint.IconFor(DeviceCategory.Router));
            Assert.Equal("Factory", DeviceFingerprint.IconFor(DeviceCategory.Plc));
            Assert.Equal("HelpCircleOutline", DeviceFingerprint.IconFor(DeviceCategory.Unknown));
        }

        [Fact]
        public void HostInfo_CategoryPropertiesReflectClassification()
        {
            var host = new HostInfo { OsGuess = "Netværksenhed" };
            Assert.Equal(DeviceCategory.Router, host.Category);
            Assert.Equal("Router", host.CategoryIcon);
            Assert.Equal("Router / netværksudstyr", host.CategoryLabel);
        }
    }
}
