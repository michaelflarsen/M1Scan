using System;

namespace M1Scan.Models
{
    /// <summary>
    /// Klassificerer forbindelsestest-tal til ord en lægmand forstår (svartid, tab,
    /// stabilitet) og til en samlet karakter. Tærsklerne er dem der er aftalt i
    /// forbindelsesbevis-redesign.md og bruges ÉT sted, så badge, metric-kort og
    /// forklaringstekster ikke kan drifte fra hinanden ved senere rettelser.
    /// </summary>
    public static class ConnectionGrading
    {
        /// <summary>Severity: 0 = bedst, 3 = værst. Bruges til at finde den dårligste
        /// af flere delkarakterer, jf. "samlet karakter er den dårligste af de tre".</summary>
        public readonly record struct Grade(string Word, string Explanation, int Severity, string ColorHex);

        /// <summary>Svartids-grænser i ms. Delt med SparklineControl's farvezoner og
        /// rapportens graf-konklusion, så en fremtidig justering her ikke kan få
        /// grafens farver til at sige noget andet end ordet på metric-kortet.</summary>
        public const double LatencyGreenTopMs = 80;
        public const double LatencyYellowTopMs = 200;

        public static Grade GradeLatency(double avgMs) => avgMs switch
        {
            < 30 => new Grade("Meget hurtigt", "Alt under 30 ms opleves som øjeblikkeligt", 0, "#4CAF50"),
            < LatencyGreenTopMs => new Grade("Hurtigt", "Mærkes ikke i almindelig brug", 1, "#8BC34A"),
            < LatencyYellowTopMs => new Grade("Mærkbart", "Kan mærkes ved fjernstyring og opkald", 2, "#FF9800"),
            _ => new Grade("Langsomt", "Giver forsinkelse, der generer i daglig brug", 3, "#F44336")
        };

        public static Grade GradeLoss(double lossPercent) => lossPercent switch
        {
            0 => new Grade("Intet tab", "Enheden svarede hver eneste gang", 0, "#4CAF50"),
            <= 1 => new Grade("Enkelte manglende svar", "Få svar udeblev, men forbindelsen holdt", 1, "#8BC34A"),
            <= 5 => new Grade("Ustabil", "Svar udeblev gentagne gange under målingen", 2, "#FF9800"),
            _ => new Grade("Alvorlige afbrydelser", "Enheden var utilgængelig i dele af perioden", 3, "#F44336")
        };

        public static Grade GradeJitter(double jitterMs) => jitterMs switch
        {
            < 5 => new Grade("Meget jævn", "Svarene lå praktisk talt ens hele vejen", 0, "#4CAF50"),
            < 20 => new Grade("Jævn", "Små variationer uden praktisk betydning", 1, "#8BC34A"),
            < 50 => new Grade("Svingende", "Svartiden varierede mærkbart under målingen", 2, "#FF9800"),
            _ => new Grade("Meget ustabil", "Svartiden sprang kraftigt op og ned", 3, "#F44336")
        };

        public readonly record struct OverallGrade(string Badge, string ColorHex, string Summary);

        /// <summary>
        /// Samlet karakter for målet. Hvis referencen selv havde tab eller kraftigt
        /// jitter (<paramref name="referenceUnreliable"/>), kan tallene ikke entydigt
        /// tilskrives målet — der udskrives bevidst ingen karakter, kun "Usikker måling".
        /// </summary>
        public static OverallGrade GradeOverall(ConnectionTestStats target, bool referenceUnreliable)
        {
            if (referenceUnreliable)
                return new OverallGrade("Usikker måling", "#8fa3bf",
                    "Vores egen internetlinje var ustabil under målingen, så tallene ovenfor ikke entydigt kan tilskrives enheden. Foreslå en ny måling, når linjen er rolig.");

            int severity = Math.Max(GradeLatency(target.AvgMs).Severity,
                           Math.Max(GradeLoss(target.LossPercent).Severity, GradeJitter(target.JitterMs).Severity));

            return severity switch
            {
                0 => new OverallGrade("Fremragende", "#4CAF50",
                    "Enheden svarede hver eneste gang i hele perioden. Svaret kom hurtigere end et øjeblik, og der var ingen afbrydelser undervejs."),
                1 => new OverallGrade("God", "#8BC34A",
                    "Enheden var tilgængelig hele perioden igennem. Svartiden var god nok til, at man ikke vil lægge mærke til den."),
                2 => new OverallGrade("Ustabil", "#FF9800",
                    "Enheden svarede, men ikke pålideligt. Der var afbrydelser eller svingende svartid, som vil kunne mærkes i daglig brug."),
                _ => new OverallGrade("Kritisk", "#F44336",
                    "Enheden var ikke pålideligt tilgængelig under målingen. Forbindelsen bør undersøges, før anlægget tages i brug.")
            };
        }
    }
}
