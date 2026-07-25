# Sikkerhedspolitik

## Understøttede versioner

Kun den nyeste udgivne version får sikkerhedsrettelser. Der udsendes ikke
backports til ældre versioner — opdatér i stedet via appens indbyggede
"Update now", eller hent den nyeste release fra GitHub.

| Version | Understøttet |
|---------|-------------|
| Nyeste release (1.3.x) | ✅ Ja  |
| Alle ældre versioner   | ❌ Nej |

## Hvad du bør vide om M1Scan's sikkerhedsmodel

- **Appen kører som administrator.** Det er nødvendigt for ARP, rå ICMP-sockets,
  promiscuous capture og `netsh`. Kør den kun fra en kilde du stoler på.
- **Auto-opdatering verificeres.** Den hentede `.exe` skal matche en SHA-256 der er
  offentliggjort i release-noterne; hashen kontrolleres både under download og igen
  lige før filen installeres. Mangler eller mismatcher hashen, afbrydes opdateringen.
- **Geo-opslag er slået fra som standard.** Traceroute kan slå land/ASN op via
  ip-api.com, men det sender rutens offentlige IP'er til en tredjepart over
  ukrypteret HTTP. Funktionen skal aktiveres manuelt.
- **Scanning er aktiv netværkstrafik.** Brug kun M1Scan på netværk du selv ejer
  eller har tilladelse til at scanne.

## Rapportér en sårbarhed

**Rapportér ikke sikkerhedsproblemer som et offentligt GitHub Issue.**

Send i stedet en e-mail til **mm@nice1.dk** med:

- En beskrivelse af sårbarheden
- Trin til at reproducere problemet
- Mulig påvirkning
- Eventuelle forslag til løsning

### Hvad sker der efter din rapport?

- Du modtager en bekræftelse inden for **5 hverdage**
- Vi undersøger problemet og vender tilbage med en statusopdatering
- Når problemet er løst, offentliggøres en ny version og du krediteres (medmindre du ønsker anonymitet)

Tak for at hjælpe med at holde M1Scan sikker.
