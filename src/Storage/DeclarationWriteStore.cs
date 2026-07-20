using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DeclarationWriteStore(string dbPath)
{
    private readonly string _dbPath = dbPath;

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    internal void SaveDeclarations(string snapshotId, IEnumerable<SymbolDeclaration> declarations)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var decl in declarations)
            {
                SaveOne(command, snapshotId, decl);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void SaveOne(SqliteCommand command, string snapshotId, SymbolDeclaration decl)
    {
        command.CommandText = @"
            INSERT OR REPLACE INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind, metadata_json, fqn)
            VALUES (@symbolId, @docCommentId, @assemblyIdentity, @kind, @metadataJson, @fqn);
        ";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
        command.Parameters.AddWithValue("@docCommentId", decl.SymbolId.DocCommentId);
        command.Parameters.AddWithValue("@assemblyIdentity", decl.SymbolId.AssemblyIdentity);
        command.Parameters.AddWithValue("@kind", decl.Kind.ToString());
        command.Parameters.AddWithValue("@metadataJson", (object?)decl.MetadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue("@fqn", (object?)decl.SymbolId.FullyQualifiedName ?? DBNull.Value);
        command.ExecuteNonQuery();

        command.CommandText = @"
            INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn, metadata_json)
            VALUES (@snapshotId, @symbolId, @fqn, @metadataJson)
            ON CONFLICT(snapshot_id, symbol_id) DO UPDATE SET
                fqn = excluded.fqn,
                metadata_json = excluded.metadata_json;
        ";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
        command.Parameters.AddWithValue("@fqn", (object?)decl.SymbolId.FullyQualifiedName ?? DBNull.Value);
        command.Parameters.AddWithValue("@metadataJson", (object?)decl.MetadataJson ?? DBNull.Value);
        command.ExecuteNonQuery();

        command.CommandText = @"
            INSERT INTO declarations (symbol_id, document_version_id,full_start, full_end,signature_start, signature_end,body_start, body_end,name_start, name_end,is_partial,is_generated,generator_identity) VALUES (@symbolId, @documentVersionId,@fullStart, @fullEnd,@signatureStart, @signatureEnd,@bodyStart, @bodyEnd,@nameStart, @nameEnd,@isPartial,@isGenerated,@generatorIdentity)
            ON CONFLICT(symbol_id, document_version_id)
            DO UPDATE SET
                full_start        = excluded.full_start,
                full_end          = excluded.full_end,
                signature_start   = excluded.signature_start,
                signature_end     = excluded.signature_end,
                body_start        = excluded.body_start,
                body_end          = excluded.body_end,
                name_start        = excluded.name_start,
                name_end          = excluded.name_end,
                is_partial        = excluded.is_partial,
                is_generated      = excluded.is_generated,
                generator_identity = excluded.generator_identity;
        ";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
        command.Parameters.AddWithValue("@documentVersionId", decl.DocumentVersionId);
        command.Parameters.AddWithValue("@fullStart", (object?)decl.FullSpan.Start ?? DBNull.Value);
        command.Parameters.AddWithValue("@fullEnd", (object?)decl.FullSpan.End ?? DBNull.Value);
        command.Parameters.AddWithValue("@signatureStart", (object?)decl.SignatureSpan.Start ?? DBNull.Value);
        command.Parameters.AddWithValue("@signatureEnd", (object?)decl.SignatureSpan.End ?? DBNull.Value);
        command.Parameters.AddWithValue("@bodyStart", (object?)decl.BodySpan.Start ?? DBNull.Value);
        command.Parameters.AddWithValue("@bodyEnd", (object?)decl.BodySpan.End ?? DBNull.Value);
        command.Parameters.AddWithValue("@nameStart", (object?)decl.NameSpan.Start ?? DBNull.Value);
        command.Parameters.AddWithValue("@nameEnd", (object?)decl.NameSpan.End ?? DBNull.Value);
        command.Parameters.AddWithValue("@isPartial", decl.IsPartial ? 1 : 0);
        command.Parameters.AddWithValue("@isGenerated", decl.IsGenerated ? 1 : 0);
        command.Parameters.AddWithValue("@generatorIdentity", (object?)decl.GeneratorIdentity ?? DBNull.Value);
        command.ExecuteNonQuery();

        if (decl.IsPartial)
        {
            command.CommandText = @"
                INSERT OR IGNORE INTO partial_declarations (symbol_id, declaration_id)
                SELECT @symbolId, declaration_id
                FROM declarations
                WHERE symbol_id = @symbolId AND document_version_id = @documentVersionId;
            ";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@symbolId", decl.SymbolId.Value);
            command.Parameters.AddWithValue("@documentVersionId", decl.DocumentVersionId);
            command.ExecuteNonQuery();
        }
    }
}
