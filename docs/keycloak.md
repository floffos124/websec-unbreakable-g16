# Keycloak (opdracht 4, stap 1: identity provider)

## Keuze

Keycloak 26.8, zelf gehost op onze server, in plaats van Auth0.

| Criterium | Keycloak (zelf gehost) | Auth0 (SaaS) |
|---|---|---|
| Soevereiniteit | Accounts, wachtwoordhashes en TOTP-secrets blijven op onze EU-server (Hetzner) | Okta (VS), valt onder de CLOUD Act |
| Lijn met opdracht 1 | Keycloak stond al in onze architectuur en third-party analyse | Nieuwe derde partij |
| Controle | Realm als code (`keycloak/unbreakable-realm.json`), eigen beleid voor MFA, brute force en wachtwoorden | Beperkt tot wat het abonnement toelaat |
| Nadeel | Wij patchen en beheren zelf: extra container en databank | Geen beheer, wel vendor lock-in |

## Opbouw

| Onderdeel | Waar |
|---|---|
| Keycloak | container `keycloak`, netwerk `bw-services`, publiek via BunkerWeb op `/auth` |
| Databank | container `keycloak-db`, enkel op het interne netwerk `keycloak-db-network` |
| Realm `unbreakable` | `keycloak/unbreakable-realm.json`, geimporteerd bij de eerste start |
| Geheimen | GitHub Secrets (`KC_DB_PASSWORD`, `KC_BOOTSTRAP_ADMIN_PASSWORD`, `KC_SEED_USER_PASSWORD`), lokaal in `.local/keycloak.env` (niet in git) |

Keycloak staat bewust niet op `bw-universe`: dat is het controlenetwerk van BunkerWeb, en `API_WHITELIST_IP` geeft alles op dat netwerk toegang tot de BunkerWeb API.

Instellingen van de realm: TOTP verplicht bij de eerste login, brute force detectie (5 pogingen), wachtwoord minstens 12 tekens, access tokens 5 minuten geldig, registratie uit, gebruikersnaam niet wijzigbaar, login- en admin-events gelogd.

## Bewuste keuze: admin console publiek bereikbaar

De admin console (`https://g16.ipv6.thelastgym.be/auth/admin`) is via internet bereikbaar. **Dit is niet de veiligste manier.** We kozen dit voor gemak: zo bereiken we alle beheerpagina's zonder SSH-tunnel.

Risico: iedereen op internet kan de loginpagina van de master realm aanvallen. Wie binnenraakt, beheert alle identiteiten. Daarnaast zijn er ModSecurity-uitzonderingen nodig voor de console, wat de WAF op die paden zwakker maakt.

Wat we doen om het risico te verkleinen:

- vaste admin-accounts met MFA (TOTP); de tijdelijke bootstrap admin `kcadmin` wordt verwijderd;
- brute force detectie op de master realm;
- sterke, willekeurige wachtwoorden;
- BunkerWeb rate limiting op `/auth/` (20 verzoeken per seconde).

De veiligere optie, die we niet kozen: `/auth/admin` en `/auth/realms/master` blokkeren in BunkerWeb en de console enkel via een SSH-tunnel naar de server openen.

## Bekende beperking

Door de IPv6-proxy van Docker ziet BunkerWeb elke bezoeker als het gateway-adres van het Docker-netwerk. Het brute force-slot van Keycloak werkt per gebruiker en blijft werken, maar een IP-ban van BunkerWeb treft dan iedereen.

## Beheer

- De realm wordt enkel geimporteerd als hij nog niet bestaat. Latere wijzigingen aan het JSON-bestand doe je ook in de admin console, of je wist het volume `keycloak-dbdata`.
- `KC_DB_PASSWORD` in GitHub Secrets moet gelijk blijven aan het wachtwoord waarmee de databank werd aangemaakt.
