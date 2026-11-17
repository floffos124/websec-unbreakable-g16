namespace Unbreakable.Models;

// Gebruikersregister: koppelt de leesbare naam (preferred_username) aan de
// stabiele identiteit (sub). Wordt gevuld wanneer iemand met een geldig token
// de API gebruikt. Nodig om een bericht aan "anna" te kunnen adresseren,
// terwijl de API autoriseert op sub.
public class AppUser
{
    public string Sub { get; set; }

    public string Username { get; set; }

    public DateTime LastSeen { get; set; }
}
