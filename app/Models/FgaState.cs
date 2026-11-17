namespace Unbreakable.Models;

// Welke OpenFGA store en welk autorisatiemodel bij deze databank horen.
// Er is altijd hoogstens één rij (Id = 1).
//
// Waarom in de app-databank? De tuples verwijzen naar bericht-id's uit deze
// databank. Wordt de databank leeggemaakt, dan beginnen de id's opnieuw bij 1.
// Zou de app dan een bestaande store hergebruiken, dan geven oude tuples
// (message:1 -> anna) toegang tot een nieuw bericht met hetzelfde id.
// Daarom: geen rij in deze tabel = nieuwe, lege store.
public class FgaState
{
    public int Id { get; set; }

    public string StoreId { get; set; }

    public string ModelId { get; set; }

    // SHA-256 van het modelbestand; verandert het model, dan schrijven we een nieuwe versie.
    public string ModelHash { get; set; }
}
