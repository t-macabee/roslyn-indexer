using Microsoft.Data.Sqlite;

namespace RoslynIndexer.Storage.Migrations
{
    public interface IMigration
    {
        int Version { get; }
        void Up(SqliteConnection connection);
    }
}
