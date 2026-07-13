using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public interface IMigration
    {
        int Version { get; }
        void Up(SqliteConnection connection);
    }
}

