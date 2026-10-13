using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Woi.Core;

public static class InvestigationStore
{
    public const long MaximumFileBytes = 128L * 1024 * 1024;
    public const int MaximumPayloadBytes = 96 * 1024 * 1024;
    public static void Save(string path, InvestigationDocument document, CancellationToken token = default)
    {
        Validate(document);
        path = Local(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Wire.Json);
        if (bytes.Length > MaximumPayloadBytes) throw new WoiException("EvidenceLimit", "The investigation exceeds the 96 MiB evidence payload limit.");
        token.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) throw new WoiException("InvalidPath", "Choose an existing local directory.");
        if (new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace < bytes.Length * 2L + 16 * 1024 * 1024)
            throw new WoiException("InsufficientDiskSpace", "Insufficient free space for an atomic investigation save.");
        var temp = Path.Combine(parent, ".woi-save-" + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false, Vfs = "win32-longpath" }.ToString())) {
                connection.Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "CREATE TABLE document(schema_version INTEGER NOT NULL,payload BLOB NOT NULL,sha256 BLOB NOT NULL); INSERT INTO document VALUES(1,$payload,$hash);";
                command.Parameters.AddWithValue("$payload", bytes);
                command.Parameters.AddWithValue("$hash", SHA256.HashData(bytes));
                command.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); transaction.Commit();
            }
            if (new FileInfo(temp).Length > MaximumFileBytes) throw new WoiException("EvidenceLimit", "The saved investigation exceeds 128 MiB.");
            token.ThrowIfCancellationRequested(); File.Move(temp, path, overwrite: true);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static InvestigationDocument Open(string path)
    {
        path = Local(path);
        if (!File.Exists(path)) throw new WoiException("MissingInvestigation", "The saved investigation was not found.");
        if (new FileInfo(path).Length > MaximumFileBytes) throw new WoiException("EvidenceLimit", "The saved investigation exceeds 128 MiB.");
        try {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, Vfs = "win32-longpath" }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT schema_version,length(payload),payload,sha256 FROM document LIMIT 2";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(0) != 1) throw new WoiException("UnsupportedInvestigation", "The investigation schema is unsupported.");
            if (reader.GetInt64(1) is < 1 or > MaximumPayloadBytes) throw new WoiException("EvidenceLimit", "The evidence payload exceeds the supported bound.");
            var payload = (byte[])reader.GetValue(2); var checksum = (byte[])reader.GetValue(3);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), checksum)) throw new WoiException("DamagedInvestigation", "The saved evidence checksum does not match.");
            if (reader.Read()) throw new WoiException("InvalidInvestigation", "The investigation container has more than one document.");
            var document = JsonSerializer.Deserialize<InvestigationDocument>(payload, Wire.Json) ?? throw new WoiException("InvalidInvestigation", "The saved evidence is empty.");
            Validate(document);
            // Rebuild descriptions from evidence. Saved prose is not authoritative analysis.
            var i = document.Investigation;
            return InvestigationAnalyzer.Analyze(document.Source, document.Snapshots, new(i.Window, i.Symptom, i.ProcessLifetimeId, i.Notes, i.Settings), i.Id) with { AttachedTrace = document.AttachedTrace == null ? null : InvestigationWorkspace.CompactReport(document.AttachedTrace) };
        } catch (Exception ex) when (ex is SqliteException or JsonException or InvalidCastException or NullReferenceException or ArgumentException) {
            throw new WoiException("InvalidInvestigation", "The investigation container or snapshot evidence is invalid: " + ex.Message);
        }
    }
    private static void Validate(InvestigationDocument document)
    {
        if (document == null || document.Schema != 1 || document.Investigation == null || string.IsNullOrWhiteSpace(document.Investigation.Id) || document.Investigation.Id.Length > 100)
            throw new WoiException("UnsupportedInvestigation", "The saved investigation schema or identity is invalid.");
        InvestigationWorkspace.ValidateAttachment(document.AttachedTrace);
        var i = document.Investigation;
        InvestigationAnalyzer.Validate(document.Source, document.Snapshots, new(i.Window, i.Symptom, i.ProcessLifetimeId, i.Notes, i.Settings));
    }
    public static string Local(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new WoiException("InvalidPath", "Choose a local file.");
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new WoiException("LocalInputRequired", "Choose a local investigation file; network access is disabled.");
        return full;
    }
}
