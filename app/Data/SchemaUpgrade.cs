using Microsoft.EntityFrameworkCore;

namespace Unbreakable.Data;

// EnsureCreated maakt enkel tabellen aan in een lege databank. De databank op
// de server bestaat al sinds opdracht 1, dus nieuwe kolommen en tabellen voegen
// we hier toe. Elke opdracht is idempotent (IF NOT EXISTS): opnieuw uitvoeren
// bij elke start is veilig, en op een nieuwe databank doet dit niets.
public static class SchemaUpgrade
{
    private const string Sql = """
        ALTER TABLE "Messages" ADD COLUMN IF NOT EXISTS "KeyLine" integer;
        ALTER TABLE "Messages" ADD COLUMN IF NOT EXISTS "Hmac" character varying(64);
        ALTER TABLE "Messages" ADD COLUMN IF NOT EXISTS "SenderId" character varying(128);
        ALTER TABLE "Messages" ADD COLUMN IF NOT EXISTS "RecipientId" character varying(128);
        ALTER TABLE "Messages" ADD COLUMN IF NOT EXISTS "FgaSynced" boolean NOT NULL DEFAULT FALSE;
        CREATE INDEX IF NOT EXISTS "IX_Messages_FgaSynced" ON "Messages" ("FgaSynced");

        CREATE TABLE IF NOT EXISTS "Users" (
            "Sub" character varying(128) NOT NULL,
            "Username" character varying(40) NOT NULL,
            "LastSeen" timestamp with time zone NOT NULL,
            CONSTRAINT "PK_Users" PRIMARY KEY ("Sub")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Username" ON "Users" ("Username");

        CREATE TABLE IF NOT EXISTS "FgaState" (
            "Id" integer NOT NULL,
            "StoreId" character varying(64) NOT NULL,
            "ModelId" character varying(64) NOT NULL,
            "ModelHash" character varying(64) NOT NULL,
            CONSTRAINT "PK_FgaState" PRIMARY KEY ("Id")
        );
        """;

    // Berichten van voor opdracht 4 hebben enkel een naam (uit X-User), geen sub.
    // We koppelen ze aan de gebruiker met die naam in het register. Berichten
    // van een naam zonder account blijven ongekoppeld en dus onzichtbaar.
    private const string LinkLegacySql = """
        UPDATE "Messages" AS m SET "SenderId" = u."Sub"
        FROM "Users" AS u
        WHERE m."SenderId" IS NULL AND lower(m."Sender") = u."Username";

        UPDATE "Messages" AS m SET "RecipientId" = u."Sub"
        FROM "Users" AS u
        WHERE m."RecipientId" IS NULL AND lower(m."Recipient") = u."Username";
        """;

    public static void Apply(UnbreakableContext context)
    {
        context.Database.ExecuteSqlRaw(Sql);
    }

    public static void LinkLegacyMessages(UnbreakableContext context)
    {
        context.Database.ExecuteSqlRaw(LinkLegacySql);
    }
}
