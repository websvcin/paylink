using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.Sqlite;

namespace PayLink.Infrastructure;

/// <summary>
/// Keeps ASP.NET Core's cookie and form-signing keys in bootstrap.db, next to the secret key, so the data folder needs no
/// separate keys/ directory. Keys from an earlier keys/dataprotection folder are imported once.
/// </summary>
public sealed class BootstrapKeyRepository(string bootstrapFile, string legacyFolder) : IXmlRepository
{
    private readonly object _lock = new();

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = bootstrapFile, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS dataprotection_keys (name VARCHAR(200) PRIMARY KEY, xml TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        return c;
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_lock)
        {
            using var c = Open();
            ImportLegacy(c);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT xml FROM dataprotection_keys;";
            using var reader = cmd.ExecuteReader();
            var list = new List<XElement>();
            while (reader.Read()) list.Add(XElement.Parse(reader.GetString(0)));
            return list;
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (_lock)
        {
            using var c = Open();
            Insert(c, friendlyName, element.ToString(SaveOptions.DisableFormatting));
        }
    }

    private void ImportLegacy(SqliteConnection c)
    {
        if (!Directory.Exists(legacyFolder)) return;
        foreach (var file in Directory.GetFiles(legacyFolder, "*.xml"))
            Insert(c, Path.GetFileNameWithoutExtension(file), File.ReadAllText(file));
        try { Directory.Move(legacyFolder, legacyFolder + ".imported"); } catch (IOException) { }
    }

    private static void Insert(SqliteConnection c, string name, string xml)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO dataprotection_keys (name, xml) VALUES ($name, $xml);";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$xml", xml);
        cmd.ExecuteNonQuery();
    }
}
