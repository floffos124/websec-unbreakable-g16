# OpenFGA als PDP (opdracht 4, stap 3)

## De eisen

1. Je mag enkel de berichten zien die naar jou gestuurd zijn.
2. Enkel wie een bericht verstuurde, kan het verwijderen.
3. De autorisatieregels worden centraal bijgehouden, niet verspreid in de code van de API.
4. Dat geldt ook voor de inbox, het zoeken en de export: ook daar beslist de PDP welke berichten je ziet.

## Vergelijking OpenFGA en OPA

| | OpenFGA | Open Policy Agent (OPA) |
|---|---|---|
| Model | ReBAC, gebaseerd op Google Zanzibar (Google Drive, YouTube) | Algemene policy engine, vooral ABAC (en RBAC als deel daarvan) |
| Taal van de regels | Eenvoudige modeltaal: types en relaties (`define can_read: recipient`) | Rego, een volledige declaratieve taal (afgeleid van Datalog) |
| Waar staan de feiten | In OpenFGA zelf: tuples (`user:anna recipient message:1`) in een eigen databank | Niet in OPA. De PEP stuurt ze mee als `input`, of ze worden als `data` in het geheugen van OPA geladen (bundles) |
| Vraag "mag anna bericht 1 lezen?" | `Check` | Evaluatie van een Rego-regel met `input` |
| Vraag "welke berichten mag anna lezen?" | `ListObjects`, rechtstreeks | Niet rechtstreeks. Ofwel alle berichten aan OPA geven, ofwel partial evaluation die een filter teruggeeft dat de API naar SQL vertaalt |
| Toestand | Stateful: OpenFGA kent alle relaties | Stateless: beslist enkel op wat het krijgt |
| Sterk in | Relaties tussen gebruikers en individuele objecten (eigenaar, gedeeld met, lid van) op grote schaal | Contextuele regels: rol, tijdstip, IP-adres, eigenschappen van het verzoek; ook buiten applicaties (Kubernetes, API gateways, Terraform) |
| Attributen | Beperkt, via conditions (CEL) op tuples en context | Volledig, elk attribuut in `input` of `data` |
| Nadelen | Extra databank; de API moet bij elk nieuw bericht tuples schrijven (twee bronnen die gelijk moeten blijven) | De gegevens om te beslissen (wie is ontvanger?) moeten van de API komen; Rego is moeilijker te leren |
| Project | CNCF, oorspronkelijk van Auth0/Okta | CNCF graduated, zeer breed gebruikt |

Beide kunnen onze regels uitdrukken. In Rego zou het zoiets zijn:

```rego
allow if {
    input.action == "read"
    input.message.recipient == input.user.sub
}
```

Maar dan moet de API eerst het bericht uit de databank halen en `recipient` meesturen. De beslissing gebeurt wel in OPA, maar de feiten waarop ze steunt komen uit de API. En voor de inbox moet de API alle berichten meesturen of de policy omzetten naar een SQL-filter.

## Keuze: OpenFGA

Onze eisen zijn relaties tussen een gebruiker en een specifiek bericht: "is ontvanger van" en "is afzender van". Dat is precies ReBAC.

- **Eis 1 en 2** staan letterlijk in het model: `can_read: recipient` en `can_delete: sender`.
- **Eis 3 (centraal)**: de regels staan in `openfga/model.fga`, buiten de API-code, en OpenFGA bewaart ook zelf de relaties. De API stelt enkel nog vragen. Bij OPA blijft de API verantwoordelijk voor het opzoeken en doorgeven van de feiten, dus een deel van de autorisatie blijft in de API.
- **Eis 4 (inbox, zoeken, export)**: `ListObjects` geeft rechtstreeks de lijst berichten die je mag lezen. De API filtert daarna enkel nog binnen die lijst. Bij OPA is dit het moeilijkste deel.
- **Uitbreidbaar**: groepsberichten, doorsturen of "gedeeld met" zijn een extra relatie in het model, zonder code in de API.

Wat we erbij nemen: een extra container met databank, en de API moet bij elk nieuw bericht de tuples schrijven en bij verwijderen opruimen. Lukt dat schrijven niet, dan kan niemand het bericht lezen (fail closed). Attributen zoals tijdstip of IP-adres zijn bij OpenFGA minder natuurlijk, maar die hebben we niet nodig.

## Waar haalt de PDP de informatie om te beslissen?

| Informatie | Bron |
|---|---|
| Regels | Het autorisatiemodel (`openfga/model.fga`, als JSON in `model.json`), eenmalig in de store van OpenFGA geladen |
| Feiten | Tuples in de eigen PostgreSQL-databank van OpenFGA (`openfga-db`): `user:<sub> sender message:<id>` en `user:<sub> recipient message:<id>`. De API schrijft ze bij elk nieuw bericht (stap 4) |
| De vraag zelf | De API (PEP) stuurt per verzoek wie (`user:<sub>`, uit het gevalideerde access token), welke relatie (`can_read`, `can_delete`) en welk object (`message:<id>`) |

OpenFGA leest dus niet de databank van de app en ziet het token niet. Het kent enkel het model en de tuples. Daarom gebruiken we de `sub` als gebruikers-id: die verandert nooit, een gebruikersnaam wel.

## Opbouw

| Onderdeel | Waar |
|---|---|
| OpenFGA v1.22.1 | container `openfga`, enkel op het interne netwerk `authz`, geen poort naar buiten, geen playground |
| Databank | container `openfga-db` (PostgreSQL 17), enkel op het interne netwerk `openfga-db-network` |
| Schema | container `openfga-migrate`, draait bij elke start `openfga migrate` en stopt dan |
| Toegang | preshared key (`OPENFGA_PRESHARED_KEY`): zonder die key weigert OpenFGA elke vraag |
| Geheimen | GitHub Secrets (`OPENFGA_DB_PASSWORD`, `OPENFGA_PRESHARED_KEY`), lokaal in `.local/keycloak.env` (niet in git) |

Het model testen zonder de app: `fga model test --tests openfga/model.fga.yaml` (OpenFGA CLI).
