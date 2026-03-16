// ============================================================
// IncidentLogger.cs
// Ukládá incidenty do lokální SQLite databáze.
// Data zůstanou i při výpadku sítě → Fáze 3 je odešle na server.
// ============================================================

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian;

public class IncidentLogger
{
    private readonly ILogger<IncidentLogger> _logger;
    private readonly string _dbPath;

    public IncidentLogger(ILogger<IncidentLogger> logger, string dbPath)
    {
        _logger = logger;
        _dbPath = dbPath;

        // Zajistíme existenci adresáře a inicializujeme DB při startu
        EnsureDatabase();
    }

    // --------------------------------------------------------
    // Uloží incident do SQLite
    // --------------------------------------------------------
    public void LogIncident(Incident incident)
    {
        try
        {
            using var conn = OpenConnection();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Incidents
                    (Timestamp, Hostname, Username,
                     VendorId, ProductId, SerialNumber, FriendlyName, DeviceType,
                     Action, WhitelistVersion, SentToServer)
                VALUES
                    ($ts, $host, $user,
                     $vid, $pid, $serial, $name, $dtype,
                     $action, $wlver, 0)";

            cmd.Parameters.AddWithValue("$ts",     incident.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("$host",   incident.Hostname);
            cmd.Parameters.AddWithValue("$user",   incident.Username);
            cmd.Parameters.AddWithValue("$vid",    incident.Device.VendorId);
            cmd.Parameters.AddWithValue("$pid",    incident.Device.ProductId);
            cmd.Parameters.AddWithValue("$serial", incident.Device.SerialNumber);
            cmd.Parameters.AddWithValue("$name",   incident.Device.FriendlyName);
            cmd.Parameters.AddWithValue("$dtype",  incident.Device.Type.ToString());
            cmd.Parameters.AddWithValue("$action", incident.Action.ToString());
            cmd.Parameters.AddWithValue("$wlver",  incident.WhitelistVersion);

            cmd.ExecuteNonQuery();

            _logger.LogInformation(
                "Incident uložen: {User}@{Host} → {Device} → {Action}",
                incident.Username, incident.Hostname,
                incident.Device.FriendlyName, incident.Action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při ukládání incidentu do SQLite");
        }
    }

    // --------------------------------------------------------
    // Vrátí posledních N incidentů (pro debugging / budoucí UI)
    // --------------------------------------------------------
    public List<Incident> GetRecentIncidents(int count = 50)
    {
        var result = new List<Incident>();

        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM Incidents
                ORDER BY Timestamp DESC
                LIMIT $count";
            cmd.Parameters.AddWithValue("$count", count);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(MapRow(reader));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při čtení incidentů z SQLite");
        }

        return result;
    }

    // --------------------------------------------------------
    // Interní: inicializace databáze a tabulky
    // --------------------------------------------------------
    private void EnsureDatabase()
    {
        try
        {
            // Vytvoříme adresář pro DB pokud neexistuje
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();

            // Vytvoříme tabulku pokud neexistuje
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Incidents (
                    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp       TEXT    NOT NULL,
                    Hostname        TEXT    NOT NULL,
                    Username        TEXT    NOT NULL,
                    VendorId        TEXT    NOT NULL,
                    ProductId       TEXT    NOT NULL,
                    SerialNumber    TEXT    NOT NULL,
                    FriendlyName    TEXT    NOT NULL,
                    DeviceType      TEXT    NOT NULL,
                    Action          TEXT    NOT NULL,
                    WhitelistVersion TEXT   NOT NULL,
                    SentToServer    INTEGER NOT NULL DEFAULT 0
                )";

            cmd.ExecuteNonQuery();
            _logger.LogDebug("SQLite databáze inicializována: {Path}", _dbPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při inicializaci SQLite: {Path}", _dbPath);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private Incident MapRow(SqliteDataReader r) => new()
    {
        Id        = r.GetInt32(r.GetOrdinal("Id")),
        Timestamp = DateTime.Parse(r.GetString(r.GetOrdinal("Timestamp"))),
        Hostname  = r.GetString(r.GetOrdinal("Hostname")),
        Username  = r.GetString(r.GetOrdinal("Username")),
        Device    = new()
        {
            VendorId     = r.GetString(r.GetOrdinal("VendorId")),
            ProductId    = r.GetString(r.GetOrdinal("ProductId")),
            SerialNumber = r.GetString(r.GetOrdinal("SerialNumber")),
            FriendlyName = r.GetString(r.GetOrdinal("FriendlyName")),
        },
        Action           = Enum.Parse<IncidentAction>(r.GetString(r.GetOrdinal("Action"))),
        WhitelistVersion = r.GetString(r.GetOrdinal("WhitelistVersion")),
        SentToServer     = r.GetInt32(r.GetOrdinal("SentToServer")) == 1
    };
}
